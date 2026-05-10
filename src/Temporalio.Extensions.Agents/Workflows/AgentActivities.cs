using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Temporalio.Activities;
using Temporalio.Extensions.Agents.HistoryStore;
using Temporalio.Extensions.Agents.Session;
using Temporalio.Extensions.Agents.State;
using Temporalio.Extensions.AI;
using Temporalio.Workflows;

namespace Temporalio.Extensions.Agents.Workflows;

/// <summary>
/// Cached state for a durable agent registered via <c>TemporalAgentsOptions.AddDurableAgent</c>.
/// Composed once at first activity dispatch (lazy) and reused for the lifetime of the worker.
/// </summary>
internal sealed record CachedDurableAgent(
    AIAgent Agent,
    IReadOnlyDictionary<string, AIFunction> Tools,
    DurableAgentRegistration Registration,
    IAgentHistoryStore? HistoryStore,
    IReadOnlyList<AIContextProvider> ContextProviders,
    TemporalAgentsOptions AgentsOptions);

/// <summary>
/// Temporal activities that perform the actual AI inference for agent sessions.
/// All AI inference must run inside an activity to preserve workflow determinism.
/// </summary>
internal sealed class AgentActivities(
    IServiceProvider services,
    ILoggerFactory? loggerFactory = null)
{
    private readonly ILogger _logger = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<AgentActivities>();

    // Per-durable-agent cache. Composed lazily at first dispatch and reused for the lifetime of
    // the worker. Concurrent first-dispatches for the same agent compose at most once.
    private readonly ConcurrentDictionary<string, CachedDurableAgent> _durableAgentCache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Builds the activity summary value (visible in the Temporal Web UI activity list).
    /// Uses the agent name when available; returns null otherwise so the SDK omits the field.
    /// </summary>
    internal static string? BuildActivitySummary(string? agentName) =>
        string.IsNullOrWhiteSpace(agentName) ? null : agentName;

    /// <summary>
    /// Reduces an externally stored session's history to its most recent <c>MaxEntryCount</c>
    /// entries via <see cref="IAgentHistoryStore.ReplaceAsync"/>. Dispatched by the workflow at
    /// continue-as-new time when the agent is using an external history store.
    /// </summary>
    [Activity("Temporalio.Extensions.Agents.ReduceHistoryInStore")]
    public async Task ReduceHistoryInStoreAsync(ReduceHistoryInStoreInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        // Resolve the agent's history store via the cache (lazy compose). The activity context's
        // workflow ID is the session ID; the agent name is carried on the input so the cache
        // entry resolves correctly even though the activity is dispatched without an agent-name
        // argument by some legacy callers — for v0.3 every dispatch comes from
        // ExecuteDurableAgentTurnAsync which has access to the agent name on AgentWorkflowInput.
        var cached = ResolveDurableAgent(input.AgentName);
        if (cached.HistoryStore is null)
        {
            throw new InvalidOperationException(
                $"ReduceHistoryInStoreAsync was dispatched but no IAgentHistoryStore is configured " +
                $"for agent '{input.AgentName}'.");
        }

        var ct = ActivityExecutionContext.Current.CancellationToken;
        var prior = await cached.HistoryStore.LoadAsync(input.SessionId, ct).ConfigureAwait(false);

        // Resolve effective reducer: per-agent first, then worker default.
        var reducer = cached.Registration.HistoryReducer
                   ?? cached.AgentsOptions.DefaultHistoryReducer;

        IReadOnlyList<DurableSessionEntry> reduced;
        if (reducer is not null)
        {
            // HistoryReducer signature expects IList<DurableSessionEntry>; materialize prior.
            // Materialize the result as a List<T> which satisfies both IList and IReadOnlyList.
            reduced = reducer(prior.ToList()).ToList();
        }
        else
        {
            if (prior.Count <= input.MaxEntryCount)
            {
                return;
            }

            reduced = prior.Skip(prior.Count - input.MaxEntryCount).ToList();
        }

        await cached.HistoryStore.ReplaceAsync(input.SessionId, reduced, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Appends the full turn — request entry + response entry carrying all messages accumulated
    /// across every LLM step and tool call — to the agent's external history store.
    /// Dispatched by <see cref="AgentWorkflow"/> after <c>ExecuteDurableAgentTurnAsync</c>
    /// returns, replacing the former in-activity append that was limited to the final assistant
    /// message and was skipped entirely when the iteration cap was hit.
    /// </summary>
    [Activity("Temporalio.Extensions.Agents.AppendAgentTurn")]
    public async Task AppendAgentTurnAsync(AppendAgentTurnInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var cached = ResolveDurableAgent(input.AgentName);
        if (cached.HistoryStore is null)
        {
            return;
        }

        var ct = ActivityExecutionContext.Current.CancellationToken;
        var now = DateTimeOffset.UtcNow;

        var requestEntry = AgentSessionRequest.FromRunRequest(input.Request, now);
        var responseEntry = AgentSessionResponse.FromAgentResponse(
            input.Request.CorrelationId ?? string.Empty,
            input.TurnResponse,
            now);

        await cached.HistoryStore.AppendAsync(
            input.SessionId,
            [requestEntry, responseEntry],
            ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Step activity used by durable agents. Performs ONE LLM call without invoking any tools.
    /// Returns either a final assistant message or one or more <see cref="FunctionCallContent"/>
    /// items that the workflow then dispatches in parallel as separate
    /// <c>Temporalio.Extensions.Agents.InvokeAgentTool</c> activities.
    /// </summary>
    [Activity("Temporalio.Extensions.Agents.RunDurableAgentStep")]
    public async Task<AgentStepResult> RunDurableAgentStepAsync(AgentStepInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var ctx = ActivityExecutionContext.Current;
        var ct = ctx.CancellationToken;

        var cached = ResolveDurableAgent(input.AgentName);

        // Fix 4 (P1-1 + P1-2): when the workflow was started by a proxy-only client, resolve
        // and return worker-side settings so the workflow can patch its input on the first turn.
        bool? resolvedExternalStore = null;
        Dictionary<string, ActivityOptions>? resolvedToolOpts = null;
        if (input.NeedsWorkerSettingsResolution)
        {
            resolvedExternalStore = cached.HistoryStore is not null
                                 || cached.AgentsOptions.HistoryStore is not null;

            var effectiveActivityTimeout = cached.Registration.ActivityTimeout
                ?? cached.AgentsOptions.DefaultActivityTimeout;
            var effectiveHeartbeatTimeout = cached.Registration.HeartbeatTimeout
                ?? cached.AgentsOptions.DefaultHeartbeatTimeout;
            var effectiveRetryPolicy = cached.Registration.RetryPolicy
                ?? cached.AgentsOptions.DefaultRetryPolicy;

            resolvedToolOpts = DefaultTemporalAgentClient.BuildDurableAgentToolActivityOptions(
                cached.Registration,
                effectiveActivityTimeout,
                effectiveHeartbeatTimeout,
                effectiveRetryPolicy);
        }
        var sessionId = input.SessionId ?? TemporalAgentSessionId.Parse(ctx.Info.WorkflowId!);

        // Restore the StateBag so AIContextProvider state survives across step iterations.
        var session = TemporalAgentSession.FromStateBag(sessionId, input.SerializedStateBag);

        IReadOnlyList<ChatMessage> messagesForLlm = input.AccumulatedMessages;
        if (cached.HistoryStore is not null && input.IsFirstStep)
        {
            var prior = await cached.HistoryStore.LoadAsync(sessionId.WorkflowId, ct).ConfigureAwait(false);
            if (prior.Count > 0)
            {
                var priorMessageCount = 0;
                foreach (var entry in prior)
                {
                    priorMessageCount += entry.Messages.Count;
                }

                var combined = new List<ChatMessage>(priorMessageCount + input.AccumulatedMessages.Count);
                foreach (var entry in prior)
                {
                    foreach (var m in entry.Messages)
                    {
                        combined.Add(m);
                    }
                }
                combined.AddRange(input.AccumulatedMessages);
                messagesForLlm = combined;
            }
        }

        var registration = cached.Registration;
        var chatOptions = registration.ChatOptions?.Clone() ?? new ChatOptions();
        chatOptions.Instructions = registration.Instructions;
        var tools = cached.Tools.Values.Cast<AITool>().ToList();
        chatOptions.Tools = tools.Count > 0 ? tools : null;
        chatOptions.ResponseFormat = input.Request.ResponseFormat;

        if (!input.Request.EnableToolCalls)
        {
            chatOptions.Tools = null;
        }
        else if (input.Request.EnableToolNames is { Count: > 0 } enabledNames && chatOptions.Tools is not null)
        {
            chatOptions.Tools = [.. chatOptions.Tools.Where(t => enabledNames.Contains(t.Name))];
        }

        var chatClient = (cached.Agent as ChatClientAgent)?.ChatClient
            ?? throw new InvalidOperationException(
                $"Durable agent '{input.AgentName}' is not a ChatClientAgent; cannot resolve its IChatClient pipeline.");

        var augmentedMessages = messagesForLlm;
        var providerAIContexts = cached.ContextProviders.Count == 0
            ? null
            : new List<Microsoft.Agents.AI.AIContext>(cached.ContextProviders.Count);
        if (cached.ContextProviders.Count > 0)
        {
            var aggregated = new Microsoft.Agents.AI.AIContext();
            foreach (var provider in cached.ContextProviders)
            {
                var invokingCtx = new Microsoft.Agents.AI.AIContextProvider.InvokingContext(
                    cached.Agent, session, aggregated);
                var providerCtx = await provider.InvokingAsync(invokingCtx, ct).ConfigureAwait(false);
                providerAIContexts!.Add(providerCtx);
            }

            var extraMessages = new List<ChatMessage>();
            foreach (var ctxResult in providerAIContexts!)
            {
                if (ctxResult.Messages is { } extra)
                {
                    foreach (var m in extra)
                    {
                        extraMessages.Add(m);
                    }
                }
            }

            if (extraMessages.Count > 0)
            {
                var combined = new List<ChatMessage>(extraMessages.Count + messagesForLlm.Count);
                combined.AddRange(extraMessages);
                combined.AddRange(messagesForLlm);
                augmentedMessages = combined;
            }
        }

        var temporalContext = new TemporalAgentContext(ctx.TemporalClient, session, services);
        TemporalAgentContext.SetCurrent(temporalContext);

        using var span = TemporalAgentTelemetry.ActivitySource.StartActivity(
            TemporalAgentTelemetry.AgentTurnSpanName,
            ActivityKind.Client);

        span?.SetTag(TemporalAgentTelemetry.AgentNameAttribute, input.AgentName);
        span?.SetTag(TemporalAgentTelemetry.AgentSessionIdAttribute, sessionId.WorkflowId);
        span?.SetTag(TemporalAgentTelemetry.AgentCorrelationIdAttribute, input.Request.CorrelationId);

        try
        {
            _logger.LogAgentActivityStarted(input.AgentName, sessionId.WorkflowId);

            var collected = new List<ChatResponseUpdate>();
            await foreach (var update in chatClient.GetStreamingResponseAsync(
                    augmentedMessages, chatOptions, ct).WithCancellation(ct).ConfigureAwait(false))
            {
                collected.Add(update);
                ctx.Heartbeat(update.Text);
            }

            var response = collected.ToChatResponse();
            var assistantMessage = response.Messages.Count > 0
                ? response.Messages[response.Messages.Count - 1]
                : new ChatMessage(ChatRole.Assistant, string.Empty);

            var toolCalls = assistantMessage.Contents
                .OfType<FunctionCallContent>()
                .ToList();

            if (span?.IsAllDataRequested == true)
            {
                span.SetTag(TemporalAgentTelemetry.InputTokensAttribute, response.Usage?.InputTokenCount);
                span.SetTag(TemporalAgentTelemetry.OutputTokensAttribute, response.Usage?.OutputTokenCount);
                span.SetTag(TemporalAgentTelemetry.TotalTokensAttribute, response.Usage?.TotalTokenCount);
            }

            _logger.LogAgentActivityCompleted(input.AgentName, sessionId.WorkflowId,
                response.Usage?.InputTokenCount, response.Usage?.OutputTokenCount, response.Usage?.TotalTokenCount);

            if (cached.ContextProviders.Count > 0)
            {
                var invokedCtx = new Microsoft.Agents.AI.AIContextProvider.InvokedContext(
                    cached.Agent,
                    session,
                    requestMessages: augmentedMessages,
                    responseMessages: response.Messages);
                foreach (var provider in cached.ContextProviders)
                {
                    await provider.InvokedAsync(invokedCtx, ct).ConfigureAwait(false);
                }
            }

            var serializedStateBag = session.SerializeStateBag();
            var isFinal = toolCalls.Count == 0;

            return new AgentStepResult
            {
                IsFinal = isFinal,
                AssistantMessage = assistantMessage,
                ToolCalls = isFinal ? null : toolCalls,
                UpdatedStateBag = serializedStateBag,
                Usage = response.Usage,
                ResolvedUseExternalStoreMode = resolvedExternalStore,
                ResolvedToolActivityOptions = resolvedToolOpts,
            };
        }
        catch (Exception ex)
        {
            span?.SetStatus(ActivityStatusCode.Error, ex.Message);
            _logger.LogAgentActivityFailed(input.AgentName, sessionId.WorkflowId, ex);

            if (cached.ContextProviders.Count > 0)
            {
                var invokedCtx = new Microsoft.Agents.AI.AIContextProvider.InvokedContext(
                    cached.Agent, session, requestMessages: augmentedMessages, invokeException: ex);
                foreach (var provider in cached.ContextProviders)
                {
                    try
                    {
                        await provider.InvokedAsync(invokedCtx, ct).ConfigureAwait(false);
                    }
                    catch
                    {
                        // Suppressed — re-throwing the original exception below is more useful.
                    }
                }
            }

            throw;
        }
        finally
        {
            TemporalAgentContext.SetCurrent(null);
        }
    }

    /// <summary>
    /// Resolves (and lazily composes) a durable agent's cached state.
    /// </summary>
    /// <exception cref="AgentNotRegisteredException">
    /// Thrown when no durable agent with this name is registered.
    /// </exception>
    internal CachedDurableAgent ResolveDurableAgent(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        return _durableAgentCache.GetOrAdd(name, static (n, ctx) =>
        {
            var (self, providerServices) = ctx;
            return self.ComposeDurableAgent(n, providerServices);
        }, (this, services));
    }

    private CachedDurableAgent ComposeDurableAgent(string name, IServiceProvider providerServices)
    {
        var agentsOptions = providerServices.GetService<TemporalAgentsOptions>()
            ?? throw new InvalidOperationException(
                "TemporalAgentsOptions is not registered in DI. Call AddTemporalAgents on the worker " +
                "builder before invoking the durable-agent dispatch path.");

        if (!agentsOptions.DurableAgentRegistrations.TryGetValue(name, out var registration))
        {
            throw new AgentNotRegisteredException(name);
        }

        var userClient = registration.ChatClient(providerServices);
        var providers = registration.ContextProviderFactories.Count == 0
            ? Array.Empty<AIContextProvider>()
            : registration.ContextProviderFactories.Select(f => f(providerServices)).ToArray();

        IChatClient chatClient = userClient;

        var resolvedTools = new Dictionary<string, AIFunction>(StringComparer.OrdinalIgnoreCase);
        var toolList = new List<AIFunction>(registration.Tools.Count);
        foreach (var tool in registration.Tools)
        {
            var resolved = tool.Factory(providerServices);
            if (resolved is null)
            {
                throw new InvalidOperationException(
                    $"Tool factory for '{tool.Name}' on agent '{name}' returned null.");
            }

            if (!string.Equals(resolved.Name, tool.Name, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Tool factory for '{tool.Name}' on agent '{name}' returned an AIFunction with " +
                    $"name '{resolved.Name}'. The factory's resolved name must match the name declared " +
                    "on AddTool.");
            }

            resolvedTools[tool.Name] = resolved;
            toolList.Add(resolved);
        }

        var chatOptions = registration.ChatOptions?.Clone() ?? new ChatOptions();
        chatOptions.Instructions = registration.Instructions;
        chatOptions.Tools = toolList.Count > 0 ? toolList.Cast<AITool>().ToList() : null;

        var agentOptions = new ChatClientAgentOptions
        {
            Name = registration.Name,
            Description = registration.Description,
            ChatOptions = chatOptions,
            AIContextProviders = providers.Length == 0 ? null : providers.ToList(),
            UseProvidedChatClientAsIs = true,
        };

        var agent = new ChatClientAgent(chatClient, agentOptions);

        // Per-agent factory wins; worker-level factory is the fallback.
        var storeFactory = registration.HistoryStore ?? agentsOptions.HistoryStore;
        var resolvedStore = storeFactory?.Invoke(providerServices);

        return new CachedDurableAgent(agent, resolvedTools, registration, resolvedStore, providers, agentsOptions);
    }

    /// <summary>
    /// Per-tool activity used by durable agents. Looks up the named agent's local tool registry,
    /// invokes the tool with the supplied arguments, and returns the result.
    /// </summary>
    [Activity("Temporalio.Extensions.Agents.InvokeAgentTool")]
    public async Task<InvokeAgentToolResult> InvokeAgentToolAsync(InvokeAgentToolInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var ctx = ActivityExecutionContext.Current;
        var ct = ctx.CancellationToken;

        var cached = ResolveDurableAgent(input.AgentName);

        if (!cached.Tools.TryGetValue(input.ToolName, out var fn))
        {
            throw new InvalidOperationException(
                $"Tool '{input.ToolName}' is not registered on agent '{input.AgentName}'.");
        }

        ctx.Heartbeat($"invoking tool '{input.ToolName}'");

        using var span = TemporalAgentTelemetry.ActivitySource.StartActivity(
            TemporalAgentTelemetry.AgentToolInvokeSpanName,
            ActivityKind.Internal);
        span?.SetTag(TemporalAgentTelemetry.AgentNameAttribute, input.AgentName);
        span?.SetTag(TemporalAgentTelemetry.AgentToolNameAttribute, input.ToolName);
        if (!string.IsNullOrEmpty(input.CallId))
        {
            span?.SetTag(TemporalAgentTelemetry.AgentToolCallIdAttribute, input.CallId);
        }

        try
        {
            _logger.LogAgentToolInvocationStarted(input.AgentName, input.ToolName);

            var arguments = input.Arguments is null
                ? new AIFunctionArguments()
                : new AIFunctionArguments(input.Arguments);

            var result = await fn.InvokeAsync(arguments, ct).ConfigureAwait(false);

            _logger.LogAgentToolInvocationCompleted(input.AgentName, input.ToolName);

            return new InvokeAgentToolResult
            {
                Result = result,
                CallId = input.CallId,
            };
        }
        catch (Exception ex)
        {
            span?.SetStatus(ActivityStatusCode.Error, ex.Message);
            _logger.LogAgentToolInvocationFailed(input.AgentName, input.ToolName, ex);
            throw;
        }
    }
}

/// <summary>
/// Input for <see cref="AgentActivities.ReduceHistoryInStoreAsync"/>.
/// </summary>
internal sealed class ReduceHistoryInStoreInput
{
    /// <summary>The agent name (used to resolve the per-agent <see cref="IAgentHistoryStore"/>).</summary>
    public required string AgentName { get; init; }

    /// <summary>The session ID (agent workflow ID) whose external history should be reduced.</summary>
    public required string SessionId { get; init; }

    /// <summary>
    /// Maximum number of entries to retain in the store after reduction.
    /// </summary>
    public required int MaxEntryCount { get; init; }
}
