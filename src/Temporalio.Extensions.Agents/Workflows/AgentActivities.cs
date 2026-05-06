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

namespace Temporalio.Extensions.Agents.Workflows;

/// <summary>
/// Temporal activities that perform the actual AI inference for agent sessions.
/// All AI inference must run inside an activity to preserve workflow determinism.
/// </summary>
internal sealed class AgentActivities(
    IReadOnlyDictionary<string, Func<IServiceProvider, AIAgent>> factories,
    IServiceProvider services,
    IAgentResponseHandler? responseHandler = null,
    ILoggerFactory? loggerFactory = null,
    IAgentHistoryStore? historyStore = null)
{
    private readonly ILogger _logger = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<AgentActivities>();
    private readonly ConcurrentDictionary<string, AIAgent> _agentCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Builds the activity summary value (visible in the Temporal Web UI activity list).
    /// Uses the agent name when available; returns null otherwise so the SDK omits the field.
    /// </summary>
    internal static string? BuildActivitySummary(string? agentName) =>
        string.IsNullOrWhiteSpace(agentName) ? null : agentName;

    /// <summary>
    /// Executes the agent with the given input and returns the response plus updated StateBag.
    /// </summary>
    [Activity("Temporalio.Extensions.Agents.ExecuteAgent")]
    public async Task<ExecuteAgentResult> ExecuteAgentAsync(ExecuteAgentInput input)
    {
        var ctx = ActivityExecutionContext.Current;
        var ct = ctx.CancellationToken;

        if (!factories.ContainsKey(input.AgentName))
        {
            throw new AgentNotRegisteredException(input.AgentName);
        }

        var realAgent = _agentCache.GetOrAdd(input.AgentName, name => factories[name](services));
        var sessionId = input.SessionId ?? TemporalAgentSessionId.Parse(ctx.Info.WorkflowId!);

        // Restore StateBag from the previous turn so providers skip re-initialization.
        var session = TemporalAgentSession.FromStateBag(sessionId, input.SerializedStateBag);

        var wrapper = new AgentWorkflowWrapper(realAgent, input.Request, session, services);

        // Resolve conversation history: either inline from the input (default mode) or
        // loaded from the external store (opt-in PII-safe mode). In external-store mode the
        // workflow has already pruned history out of the activity-scheduled event, so the
        // request entry for the current turn must be reconstructed here from input.Request.
        IReadOnlyList<DurableSessionEntry> historyForActivity;
        AgentSessionRequest? externalRequestEntry = null;
        if (input.UseExternalStore)
        {
            if (historyStore is null)
            {
                throw new InvalidOperationException(
                    "ExecuteAgentInput.UseExternalStore is true but no IAgentHistoryStore is registered. " +
                    "Register an implementation in DI before enabling TemporalAgentsOptions.UseExternalHistory.");
            }

            var prior = await historyStore.LoadAsync(sessionId.WorkflowId, ct).ConfigureAwait(false);
            externalRequestEntry = AgentSessionRequest.FromRunRequest(input.Request, DateTimeOffset.UtcNow);
            // Concatenate prior history with the just-constructed request entry so the LLM
            // sees the new user message alongside all prior turns.
            var combined = new List<DurableSessionEntry>(prior.Count + 1);
            combined.AddRange(prior);
            combined.Add(externalRequestEntry);
            historyForActivity = combined;
        }
        else
        {
            historyForActivity = input.ConversationHistory
                ?? throw new InvalidOperationException(
                    "ExecuteAgentInput.ConversationHistory is null but UseExternalStore is false. " +
                    "When UseExternalStore is false the workflow must supply ConversationHistory.");
        }

        // Rebuild the full conversation from the resolved history.
        int messageCount = 0;
        foreach (var entry in historyForActivity)
            messageCount += entry.Messages.Count;

        var allMessages = new List<ChatMessage>(messageCount);
        foreach (var entry in historyForActivity)
            foreach (var msg in entry.Messages)
                allMessages.Add(msg);

        _logger.LogActivityHistoryRebuilt(input.AgentName, sessionId.WorkflowId,
            historyForActivity.Count, allMessages.Count);

        var agentSession = await wrapper.CreateSessionAsync(ct).ConfigureAwait(false);
        var temporalContext = new TemporalAgentContext(ctx.TemporalClient, session, services);
        TemporalAgentContext.SetCurrent(temporalContext);

        // GAP 4: emit an OpenTelemetry span for this agent turn.
        using var span = TemporalAgentTelemetry.ActivitySource.StartActivity(
            TemporalAgentTelemetry.AgentTurnSpanName,
            ActivityKind.Client);

        span?.SetTag(TemporalAgentTelemetry.AgentNameAttribute, input.AgentName);
        span?.SetTag(TemporalAgentTelemetry.AgentSessionIdAttribute, sessionId.WorkflowId);
        span?.SetTag(TemporalAgentTelemetry.AgentCorrelationIdAttribute, input.Request.CorrelationId);

        try
        {
            _logger.LogAgentActivityStarted(input.AgentName, sessionId.WorkflowId);

            IAsyncEnumerable<AgentResponseUpdate> responseStream = wrapper.RunStreamingAsync(
                allMessages,
                agentSession,
                options: null,
                ct);

            AgentResponse response;
            if (responseHandler is null)
            {
                // Heartbeat on each streamed chunk even when no handler is registered,
                // so that long-running LLM calls don't hit the heartbeat timeout.
                List<AgentResponseUpdate> collectedUpdates = [];
                await foreach (var update in responseStream.WithCancellation(ct))
                {
                    collectedUpdates.Add(update);
                    ctx.Heartbeat(update.Text);
                }
                response = collectedUpdates.ToAgentResponse();
            }
            else
            {
                List<AgentResponseUpdate> updates = [];

                async IAsyncEnumerable<AgentResponseUpdate> StreamWithHeartbeat()
                {
                    await foreach (var update in responseStream)
                    {
                        updates.Add(update);
                        ctx.Heartbeat(update.Text);
                        yield return update;
                    }
                }

                await responseHandler.OnStreamingResponseUpdateAsync(StreamWithHeartbeat(), ct).ConfigureAwait(false);
                response = updates.ToAgentResponse();
            }

            // GAP 4: tag token usage onto the span.
            if (span?.IsAllDataRequested == true)
            {
                span.SetTag(TemporalAgentTelemetry.InputTokensAttribute, response.Usage?.InputTokenCount);
                span.SetTag(TemporalAgentTelemetry.OutputTokensAttribute, response.Usage?.OutputTokenCount);
                span.SetTag(TemporalAgentTelemetry.TotalTokensAttribute, response.Usage?.TotalTokenCount);
            }

            _logger.LogAgentActivityCompleted(input.AgentName, sessionId.WorkflowId,
                response.Usage?.InputTokenCount, response.Usage?.OutputTokenCount, response.Usage?.TotalTokenCount);

            // GAP 6: capture the updated StateBag so the workflow can persist it.
            var serializedStateBag = session.SerializeStateBag();

            // External-store mode: append both entries (request + response) to the store
            // so subsequent turns load them via LoadAsync. The workflow does not append
            // these to its in-workflow history (Messages are stripped — see
            // DurableChatWorkflowBase.ShouldStripMessagesFromHistoryEntry).
            if (input.UseExternalStore && historyStore is not null && externalRequestEntry is not null)
            {
                var responseEntry = AgentSessionResponse.FromAgentResponse(
                    input.Request.CorrelationId!,
                    response,
                    DateTimeOffset.UtcNow);

                await historyStore.AppendAsync(
                    sessionId.WorkflowId,
                    new DurableSessionEntry[] { externalRequestEntry, responseEntry },
                    ct).ConfigureAwait(false);
            }

            return new ExecuteAgentResult(response, serializedStateBag);
        }
        catch (Exception ex)
        {
            span?.SetStatus(ActivityStatusCode.Error, ex.Message);
            _logger.LogAgentActivityFailed(input.AgentName, sessionId.WorkflowId, ex);
            throw;
        }
        finally
        {
            TemporalAgentContext.SetCurrent(null);
        }
    }

    /// <summary>
    /// Step-mode activity used when <see cref="TemporalAgentsOptions.EnablePerToolActivities"/>
    /// is enabled. Performs ONE LLM call without invoking any tools and returns either:
    /// <list type="bullet">
    ///   <item>a final assistant message (no tool calls), or</item>
    ///   <item>the assistant message containing one or more <see cref="FunctionCallContent"/>
    ///   items that the workflow then dispatches in parallel as separate
    ///   <c>InvokeFunctionAsync</c> activities.</item>
    /// </list>
    /// </summary>
    /// <remarks>
    /// <para>
    /// To bypass <c>FunctionInvokingChatClient</c> we resolve the agent's underlying
    /// <see cref="IChatClient"/> from DI (registered by the user before <c>AddDurableAI</c>)
    /// and call <see cref="IChatClient.GetResponseAsync"/> directly. The agent's instructions
    /// are pulled from <see cref="ChatClientAgent.Instructions"/> when the registered agent is
    /// a <see cref="ChatClientAgent"/>; otherwise instructions are omitted.
    /// </para>
    /// <para>
    /// Tools visible to the LLM are sourced from the registered <c>DurableFunctionRegistry</c>
    /// (populated via <c>AddDurableTools(...)</c>). These same tools resolve by name when
    /// the workflow dispatches <c>InvokeFunctionAsync</c>.
    /// </para>
    /// </remarks>
    [Activity("Temporalio.Extensions.Agents.RunAgentStep")]
    public async Task<AgentStepResult> RunAgentStepAsync(AgentStepInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var ctx = ActivityExecutionContext.Current;
        var ct = ctx.CancellationToken;

        if (!factories.ContainsKey(input.AgentName))
        {
            throw new AgentNotRegisteredException(input.AgentName);
        }

        var realAgent = _agentCache.GetOrAdd(input.AgentName, name => factories[name](services));
        var sessionId = input.SessionId ?? TemporalAgentSessionId.Parse(ctx.Info.WorkflowId!);

        // Restore StateBag so providers skip re-initialization across step iterations.
        var session = TemporalAgentSession.FromStateBag(sessionId, input.SerializedStateBag);

        // Pull per-call inputs from the registered agent. We bypass FunctionInvokingChatClient
        // by talking directly to IChatClient (resolved from DI) — the workflow owns the tool
        // dispatch loop in step mode.
        var chatClient = services.GetRequiredService<IChatClient>();

        // Tools come from the durable function registry — same registry the InvokeFunctionAsync
        // activity resolves by name. This keeps the schema visible to the LLM consistent with
        // the names the workflow will dispatch.
        var registry = services.GetService<IReadOnlyDictionary<string, AIFunction>>();
        var tools = registry is null
            ? new List<AITool>()
            : registry.Values.Cast<AITool>().ToList();

        // Pull instructions when the registered agent is a ChatClientAgent; otherwise omit.
        string? instructions = realAgent is ChatClientAgent cca ? cca.Instructions : null;

        // Seed from the agent's registration-time ChatOptions when available so per-agent
        // settings (Temperature, ModelId, TopP, FrequencyPenalty, etc.) are honored. Without
        // this clone, step mode would silently drop those configurations because we bypass
        // the agent's own GetResponseAsync pipeline. Two lookup paths:
        //   1) Pre-captured options from AddAIAgent(instance) — captured at registration time.
        //   2) Live read off the cached ChatClientAgent via the same reflection accessor — covers
        //      the AddAIAgentFactory path where the agent did not exist at registration time.
        var registeredOptions = services.GetService<TemporalAgentsOptions>()
            ?.GetAgentChatOptions(input.AgentName)
            ?? (realAgent is ChatClientAgent ccaForOptions
                ? TemporalAgentsOptions.ReadChatOptionsFromAgent(ccaForOptions)
                : null);
        var chatOptions = registeredOptions?.Clone() ?? new ChatOptions();

        // Override the request-scoped fields. Instructions / Tools / ResponseFormat are the
        // three values the workflow + request own per-turn, so they always replace whatever the
        // registration-time options carried.
        chatOptions.Instructions = instructions;
        chatOptions.Tools = tools.Count > 0 ? tools : null;
        chatOptions.ResponseFormat = input.Request.ResponseFormat;

        // Apply tool filtering from the request (mirrors AgentWorkflowWrapper behavior).
        if (!input.Request.EnableToolCalls)
        {
            chatOptions.Tools = null;
        }
        else if (input.Request.EnableToolNames is { Count: > 0 } enabledNames && chatOptions.Tools is not null)
        {
            chatOptions.Tools = [.. chatOptions.Tools.Where(t => enabledNames.Contains(t.Name))];
        }

        using var span = TemporalAgentTelemetry.ActivitySource.StartActivity(
            TemporalAgentTelemetry.AgentTurnSpanName,
            ActivityKind.Client);

        span?.SetTag(TemporalAgentTelemetry.AgentNameAttribute, input.AgentName);
        span?.SetTag(TemporalAgentTelemetry.AgentSessionIdAttribute, sessionId.WorkflowId);
        span?.SetTag(TemporalAgentTelemetry.AgentCorrelationIdAttribute, input.Request.CorrelationId);

        try
        {
            _logger.LogAgentActivityStarted(input.AgentName, sessionId.WorkflowId);

            // Heartbeat on each streamed chunk so long LLM calls stay alive.
            var collected = new List<ChatResponseUpdate>();
            await foreach (var update in chatClient.GetStreamingResponseAsync(
                    input.AccumulatedMessages, chatOptions, ct).WithCancellation(ct).ConfigureAwait(false))
            {
                collected.Add(update);
                ctx.Heartbeat(update.Text);
            }

            var response = collected.ToChatResponse();
            var assistantMessage = response.Messages.Count > 0
                ? response.Messages[response.Messages.Count - 1]
                : new ChatMessage(ChatRole.Assistant, string.Empty);

            // Detect FunctionCallContent items in the message — when present, the LLM is
            // requesting tool invocation and the workflow will fan out separate activities.
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

            var serializedStateBag = session.SerializeStateBag();

            return new AgentStepResult
            {
                IsFinal = toolCalls.Count == 0,
                AssistantMessage = assistantMessage,
                ToolCalls = toolCalls.Count == 0 ? null : toolCalls,
                UpdatedStateBag = serializedStateBag,
                Usage = response.Usage,
            };
        }
        catch (Exception ex)
        {
            span?.SetStatus(ActivityStatusCode.Error, ex.Message);
            _logger.LogAgentActivityFailed(input.AgentName, sessionId.WorkflowId, ex);
            throw;
        }
    }

    /// <summary>
    /// Loads the externally stored history and, when it exceeds <c>input.MaxEntryCount</c>,
    /// writes the most recent <c>MaxEntryCount</c> entries back via
    /// <see cref="IAgentHistoryStore.ReplaceAsync"/> (a deterministic tail-trim).
    /// Dispatched by the workflow at continue-as-new time when <c>UseExternalStore</c> is
    /// enabled. This activity does <b>not</b> apply <c>TemporalAgentsOptions.HistoryReducer</c>:
    /// the reducer delegate is <c>[JsonIgnore]</c> and cannot cross the activity boundary, so
    /// the store-side reduction is intentionally a fixed tail-trim. Implementations that need
    /// custom reduction can override the behaviour inside their own
    /// <see cref="IAgentHistoryStore.ReplaceAsync"/> (re-load and re-write the store however
    /// they want) or run reduction from a separate background process.
    /// </summary>
    [Activity("Temporalio.Extensions.Agents.ReduceHistoryInStore")]
    public async Task ReduceHistoryInStoreAsync(ReduceHistoryInStoreInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (historyStore is null)
        {
            throw new InvalidOperationException(
                "ReduceHistoryInStoreAsync was dispatched but no IAgentHistoryStore is registered. " +
                "Register an implementation in DI before enabling TemporalAgentsOptions.UseExternalHistory.");
        }

        var ct = ActivityExecutionContext.Current.CancellationToken;
        var prior = await historyStore.LoadAsync(input.SessionId, ct).ConfigureAwait(false);

        // We can't transport the reducer delegate across the activity boundary; this activity is
        // a no-op when no reducer is configured and the workflow side is responsible for only
        // dispatching it when there IS one. Implementations that want history pruning must
        // set TemporalAgentsOptions.HistoryReducer; this activity then truncates by keeping the
        // most recent N entries (input.MaxEntryCount) as a deterministic, store-side reduction.
        if (prior.Count <= input.MaxEntryCount)
        {
            return;
        }

        var trimmed = prior.Skip(prior.Count - input.MaxEntryCount).ToList();
        await historyStore.ReplaceAsync(input.SessionId, trimmed, ct).ConfigureAwait(false);
    }
}

/// <summary>
/// Input for <see cref="AgentActivities.ReduceHistoryInStoreAsync"/>.
/// </summary>
internal sealed class ReduceHistoryInStoreInput
{
    /// <summary>The session ID (agent workflow ID) whose external history should be reduced.</summary>
    public required string SessionId { get; init; }

    /// <summary>
    /// Maximum number of entries to retain in the store after reduction. Mirrors the workflow's
    /// <c>MaxEntryCount</c> so the store stays bounded alongside the workflow's in-memory list.
    /// </summary>
    public required int MaxEntryCount { get; init; }
}
