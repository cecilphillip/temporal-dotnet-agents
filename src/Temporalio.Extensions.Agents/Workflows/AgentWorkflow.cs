using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Temporalio.Common;
using Temporalio.Extensions.Agents.Session;
using Temporalio.Extensions.Agents.State;
using Temporalio.Extensions.AI;
using Temporalio.Workflows;

namespace Temporalio.Extensions.Agents.Workflows;

/// <summary>
/// Long-lived Temporal workflow that acts as the durable backing store for an agent session.
/// Drives the durable-agent dispatch loop: each LLM call is a separate <c>RunDurableAgentStep</c>
/// activity, and each tool call is a separate <c>InvokeAgentTool</c> activity dispatched in
/// parallel via <see cref="Workflow.WhenAllAsync{TResult}(IEnumerable{Task{TResult}})"/>.
/// </summary>
[Workflow("Temporalio.Extensions.Agents.AgentWorkflow")]
internal class AgentWorkflow : DurableChatWorkflowBase<AgentResponse>
{
    internal static readonly SearchAttributeKey<string> AgentNameSearchAttribute =
        SearchAttributeKey.CreateKeyword("AgentName");

    // MAF-specific input (typed view of the base's Input). Set in RunAsync.
    private AgentWorkflowInput? _input;

    // GAP 6: StateBag persisted across turns so AIContextProvider state survives replay.
    private JsonElement? _currentStateBag;

    [WorkflowRun]
    public async Task RunAsync(AgentWorkflowInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        _input = input;
        _currentStateBag = input.CarriedStateBag;

        Workflow.Logger.LogWorkflowStarted(input.AgentName, Workflow.Info.WorkflowId, input.TimeToLive);

        // Detect "external history mode" from the resolved agent input — when ANY history
        // store is configured (worker default or per-agent override), the workflow strips
        // message payloads from history entries before adding them, and the activity is
        // responsible for loading/appending via IAgentHistoryStore.
        // The `useExternalStore` flag below is computed from a workflow-only signal we set
        // when reducing history for continue-as-new (see CreateContinueAsNewException).

        // External-store mode + HistoryReducer: the base throws ContinueAsNewException after
        // calling our CreateContinueAsNewException hook (which is synchronous, so it cannot
        // dispatch activities). Intercept the throw here to fire the ReduceHistoryInStoreAsync
        // activity before re-throwing, so the next workflow run sees a bounded store.
        try
        {
            await base.RunAsync(input).ConfigureAwait(true);
        }
        catch (ContinueAsNewException can) when (UseExternalStoreMode)
        {
            var reduceInput = new ReduceHistoryInStoreInput
            {
                AgentName = input.AgentName,
                SessionId = Workflow.Info.WorkflowId,
                MaxEntryCount = input.MaxEntryCount,
            };
            await Workflow.ExecuteActivityAsync(
                (AgentActivities a) => a.ReduceHistoryInStoreAsync(reduceInput),
                new ActivityOptions
                {
                    StartToCloseTimeout = input.ActivityTimeout,
                    HeartbeatTimeout = input.HeartbeatTimeout,
                    Summary = AgentActivities.BuildActivitySummary(input.AgentName),
                    RetryPolicy = input.RetryPolicy,
                }).ConfigureAwait(true);
            _ = can;
            throw;
        }
    }

    /// <summary>
    /// Indicates whether this workflow is operating in external-history mode. The workflow side
    /// reads this from the resolved cached state on first dispatch. Until then it cannot be known
    /// (the activity composes the cache lazily); we therefore compute it from the activity's
    /// echoed value via the workflow input. The agent client populates this via
    /// <see cref="AgentWorkflowInput.UseExternalStoreMode"/> when starting the workflow.
    /// </summary>
    private bool UseExternalStoreMode => _input?.UseExternalStoreMode == true;

    /// <summary>
    /// Validates that a <see cref="RunAgentAsync"/> request is well-formed before it enters history.
    /// </summary>
    [WorkflowUpdateValidator(nameof(RunAgentAsync))]
    public void ValidateRunAgent(RunRequest request)
    {
        if (IsShutdownRequested)
            throw new InvalidOperationException("Session has been shut down.");
        if (request?.Messages is null || request.Messages.Count == 0)
            throw new ArgumentException("At least one message is required.");
    }

    /// <summary>
    /// Runs the agent with the given request and returns the response.
    /// Updates are serialized — only one runs at a time.
    /// </summary>
    [WorkflowUpdate("Run")]
    public async Task<AgentResponse> RunAgentAsync(RunRequest request)
    {
        var requestEntry = AgentSessionRequest.FromRunRequest(request, Workflow.UtcNow);

        var (output, _) = await RunTurnAsync(requestEntry, chatOptions: null);

        Workflow.Logger.LogWorkflowUpdateCompleted(
            _input!.AgentName, Workflow.Info.WorkflowId, request.CorrelationId ?? string.Empty);
        return output;
    }

    /// <summary>
    /// Queues a fire-and-forget run. The workflow does not wait for this to complete.
    /// </summary>
    [WorkflowSignal("RunFireAndForget")]
    public Task RunAgentFireAndForgetAsync(RunRequest request)
    {
        _ = ProcessFireAndForgetAsync(request);
        return Task.CompletedTask;
    }

    // ── Hooks supplied to the base class ────────────────────────────────────

    /// <inheritdoc/>
    protected override DurableSessionResponse BuildResponseEntry(
        string correlationId,
        AgentResponse output,
        DateTimeOffset createdAt) =>
        AgentSessionResponse.FromAgentResponse(correlationId, output, createdAt);

    /// <inheritdoc/>
    protected override Task<AgentResponse> ExecuteTurnAsync(
        ActivityOptions activityOptions,
        DurableSessionRequest requestEntry,
        ChatOptions? chatOptions)
    {
        _ = activityOptions;
        var agentRequestEntry = (AgentSessionRequest)requestEntry;
        var runRequest = ToRunRequest(agentRequestEntry);

        return ExecuteAgentTurnAsync(runRequest);
    }

    /// <inheritdoc/>
    protected override ContinueAsNewException CreateContinueAsNewException(
        DurableChatWorkflowInput input)
    {
        ArgumentNullException.ThrowIfNull(_input);

        var useExternalStore = _input.UseExternalStoreMode;

        var carriedInput = new AgentWorkflowInput
        {
            AgentName = _input.AgentName,
            TaskQueue = _input.TaskQueue,
            CarriedStateBag = _currentStateBag,
            RetryPolicy = _input.RetryPolicy,
            UseExternalStoreMode = useExternalStore,
            MaxToolCallsPerTurn = _input.MaxToolCallsPerTurn,
            DurableAgentToolActivityOptions = _input.DurableAgentToolActivityOptions,
            WorkerSettingsResolved = _input.WorkerSettingsResolved,

            TimeToLive = input.TimeToLive,
            CarriedHistory = useExternalStore ? null : input.CarriedHistory,
            ApprovalTimeout = input.ApprovalTimeout,
            EnableSearchAttributes = input.EnableSearchAttributes,
            MaxEntryCount = input.MaxEntryCount,
            HistoryReducer = input.HistoryReducer,
            OriginalCreatedAt = input.OriginalCreatedAt,
            ActivityTimeout = input.ActivityTimeout,
            HeartbeatTimeout = input.HeartbeatTimeout,
        };

        Workflow.Logger.LogWorkflowContinueAsNew(
            _input.AgentName, Workflow.Info.WorkflowId,
            input.CarriedHistory?.Count ?? 0);

        return Workflow.CreateContinueAsNewException(
            (AgentWorkflow wf) => wf.RunAsync(carriedInput));
    }

    /// <inheritdoc/>
    protected override void UpsertCustomSearchAttributes()
    {
        if (_input is not null)
        {
            Workflow.UpsertTypedSearchAttributes(
                AgentNameSearchAttribute.ValueSet(_input.AgentName));
        }
    }

    /// <inheritdoc/>
    protected override bool ShouldStripMessagesFromHistoryEntry() => UseExternalStoreMode;

    /// <inheritdoc/>
    protected override DurableSessionEntry StripMessagesFromEntry(DurableSessionEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return entry switch
        {
            AgentSessionRequest agentReq => new AgentSessionRequest
            {
                CorrelationId = agentReq.CorrelationId,
                CreatedAt = agentReq.CreatedAt,
                Messages = [],
                OrchestrationId = agentReq.OrchestrationId,
                ResponseType = agentReq.ResponseType,
                ResponseSchema = agentReq.ResponseSchema,
                AdditionalProperties = agentReq.AdditionalProperties,
            },
            AgentSessionResponse agentResp => new AgentSessionResponse
            {
                CorrelationId = agentResp.CorrelationId,
                CreatedAt = agentResp.CreatedAt,
                Messages = [],
                Usage = agentResp.Usage,
                AdditionalProperties = agentResp.AdditionalProperties,
            },
            _ => base.StripMessagesFromEntry(entry),
        };
    }

    // ── Internals ───────────────────────────────────────────────────────────

    /// <summary>
    /// Reconstructs the original <see cref="RunRequest"/> from a stored
    /// <see cref="AgentSessionRequest"/>.
    /// </summary>
    private static RunRequest ToRunRequest(AgentSessionRequest entry)
    {
        ChatResponseFormat? responseFormat = null;
        if (string.Equals(entry.ResponseType, "json", StringComparison.OrdinalIgnoreCase))
        {
            responseFormat = entry.ResponseSchema is { } schema
                ? ChatResponseFormat.ForJsonSchema(schema)
                : ChatResponseFormat.Json;
        }

        return new RunRequest(entry.Messages.ToList(), responseFormat: responseFormat)
        {
            CorrelationId = entry.CorrelationId,
            OrchestrationId = entry.OrchestrationId,
        };
    }

    private async Task<AgentResponse> ExecuteAgentTurnAsync(RunRequest runRequest)
    {
        var stepActivityOptions = new ActivityOptions
        {
            StartToCloseTimeout = _input!.ActivityTimeout,
            HeartbeatTimeout = _input!.HeartbeatTimeout,
            Summary = AgentActivities.BuildActivitySummary(_input!.AgentName),
            RetryPolicy = _input!.RetryPolicy,
        };

        return await ExecuteDurableAgentTurnAsync(runRequest, stepActivityOptions).ConfigureAwait(true);
    }

    /// <summary>
    /// Durable-agent dispatch loop. Drives the alternation between
    /// <c>RunDurableAgentStepAsync</c> (one LLM call) and <c>InvokeAgentToolAsync</c> per tool
    /// call. Tool calls within a single LLM response fan out via
    /// <see cref="Workflow.WhenAllAsync{TResult}(IEnumerable{Task{TResult}})"/>; the loop
    /// terminates when the LLM returns a final assistant message or when
    /// <see cref="DurableAgentBuilder.MaxToolCallsPerTurn"/> iterations are exceeded.
    /// </summary>
    private async Task<AgentResponse> ExecuteDurableAgentTurnAsync(
        RunRequest runRequest,
        ActivityOptions stepActivityOptions)
    {
        Workflow.Logger.LogDurableAgentTurnStarted(_input!.AgentName, Workflow.Info.WorkflowId);

        // External history mode: workflow does not retain message payloads in History entries
        // (ShouldStripMessagesFromHistoryEntry returns true). Seed the LLM with just the current
        // request's messages; the activity will load prior session history from the store on
        // the first step (IsFirstStep = true).
        var accumulated = UseExternalStoreMode
            ? new List<ChatMessage>(runRequest.Messages)
            : FlattenHistoryMessages();

        var allTurnMessages = new List<ChatMessage>();
        UsageDetails? totalUsage = null;

        var maxIterations = _input!.MaxToolCallsPerTurn;

        for (var iteration = 0; iteration < maxIterations; iteration++)
        {
            // Fix 4 (P1-1 + P1-2): proxy-started sessions have WorkerSettingsResolved=false.
            // On the first step of the first turn, ask the activity to resolve worker-side
            // settings (external-store mode, per-tool activity options) and return them.
            var needsResolution = iteration == 0 && !_input!.WorkerSettingsResolved;

            var stepInput = new AgentStepInput
            {
                AgentName = _input!.AgentName,
                Request = runRequest,
                AccumulatedMessages = accumulated,
                SerializedStateBag = _currentStateBag,
                SessionId = null,
                IsFirstStep = iteration == 0,
                NeedsWorkerSettingsResolution = needsResolution,
            };

            var stepResult = await Workflow.ExecuteActivityAsync(
                (AgentActivities a) => a.RunDurableAgentStepAsync(stepInput),
                stepActivityOptions).ConfigureAwait(true);

            // Fix 4: apply resolved worker-side settings once and carry forward via CAN.
            if (needsResolution && stepResult.ResolvedUseExternalStoreMode.HasValue)
            {
                _input = new AgentWorkflowInput
                {
                    AgentName = _input!.AgentName,
                    TaskQueue = _input!.TaskQueue,
                    CarriedStateBag = _currentStateBag,
                    RetryPolicy = _input!.RetryPolicy,
                    UseExternalStoreMode = stepResult.ResolvedUseExternalStoreMode.Value,
                    MaxToolCallsPerTurn = _input!.MaxToolCallsPerTurn,
                    DurableAgentToolActivityOptions = stepResult.ResolvedToolActivityOptions,
                    WorkerSettingsResolved = true,

                    TimeToLive = _input!.TimeToLive,
                    CarriedHistory = _input!.CarriedHistory,
                    ApprovalTimeout = _input!.ApprovalTimeout,
                    EnableSearchAttributes = _input!.EnableSearchAttributes,
                    MaxEntryCount = _input!.MaxEntryCount,
                    HistoryReducer = _input!.HistoryReducer,
                    OriginalCreatedAt = _input!.OriginalCreatedAt,
                    ActivityTimeout = _input!.ActivityTimeout,
                    HeartbeatTimeout = _input!.HeartbeatTimeout,
                };
            }

            _currentStateBag = stepResult.UpdatedStateBag;

            if (stepResult.Usage is not null)
            {
                totalUsage ??= new UsageDetails();
                totalUsage.InputTokenCount = (totalUsage.InputTokenCount ?? 0) + (stepResult.Usage.InputTokenCount ?? 0);
                totalUsage.OutputTokenCount = (totalUsage.OutputTokenCount ?? 0) + (stepResult.Usage.OutputTokenCount ?? 0);
                totalUsage.TotalTokenCount = (totalUsage.TotalTokenCount ?? 0) + (stepResult.Usage.TotalTokenCount ?? 0);
            }

            accumulated.Add(stepResult.AssistantMessage);
            allTurnMessages.Add(stepResult.AssistantMessage);

            if (stepResult.IsFinal || stepResult.ToolCalls is null || stepResult.ToolCalls.Count == 0)
            {
                Workflow.Logger.LogDurableAgentTurnCompleted(_input!.AgentName, iteration + 1);
                var finalResponse = new AgentResponse
                {
                    Messages = allTurnMessages,
                    Usage = totalUsage,
                    CreatedAt = Workflow.UtcNow,
                };

                // Fix 2 (P1-3): append the full turn to the external store. This captures all
                // messages accumulated during the turn (tool-call messages, tool-result messages,
                // and the final assistant message) rather than just the final assistant message.
                if (UseExternalStoreMode)
                {
                    await Workflow.ExecuteActivityAsync(
                        (AgentActivities a) => a.AppendAgentTurnAsync(new AppendAgentTurnInput
                        {
                            AgentName = _input!.AgentName,
                            SessionId = Workflow.Info.WorkflowId,
                            Request = runRequest,
                            TurnResponse = finalResponse,
                        }),
                        new ActivityOptions
                        {
                            StartToCloseTimeout = _input!.ActivityTimeout,
                            HeartbeatTimeout = _input!.HeartbeatTimeout,
                            Summary = AgentActivities.BuildActivitySummary(_input!.AgentName),
                            RetryPolicy = _input!.RetryPolicy,
                        }).ConfigureAwait(true);
                }

                return finalResponse;
            }

            var toolCalls = stepResult.ToolCalls;

            Workflow.Logger.LogDurableAgentTurnIteration(_input!.AgentName, iteration + 1, toolCalls.Count);

            var toolTasks = new List<Task<InvokeAgentToolResult>>(toolCalls.Count);
            foreach (var tc in toolCalls)
            {
                var toolOptions = ResolveDurableToolActivityOptions(tc.Name);

                var toolInput = new InvokeAgentToolInput
                {
                    AgentName = _input!.AgentName,
                    ToolName = tc.Name,
                    Arguments = tc.Arguments is null
                        ? null
                        : new Dictionary<string, object?>(tc.Arguments),
                    CallId = tc.CallId,
                };

                toolTasks.Add(Workflow.ExecuteActivityAsync(
                    (AgentActivities a) => a.InvokeAgentToolAsync(toolInput),
                    toolOptions));
            }

            var toolResults = await Workflow.WhenAllAsync(toolTasks).ConfigureAwait(true);

            var functionResultContents = new List<AIContent>(toolCalls.Count);
            for (var i = 0; i < toolCalls.Count; i++)
            {
                functionResultContents.Add(new FunctionResultContent(
                    callId: toolCalls[i].CallId,
                    result: toolResults[i].Result));
            }

            var toolResultMessage = new ChatMessage(ChatRole.Tool, functionResultContents);
            accumulated.Add(toolResultMessage);
            allTurnMessages.Add(toolResultMessage);
        }

        Workflow.Logger.LogDurableAgentTurnAborted(_input!.AgentName, maxIterations);

        var errorMessage = new ChatMessage(
            ChatRole.Assistant,
            $"Maximum tool-call iterations ({maxIterations}) exceeded for agent '{_input!.AgentName}'. " +
            "The agent did not converge on a final answer.");
        allTurnMessages.Add(errorMessage);

        var abortedResponse = new AgentResponse
        {
            Messages = allTurnMessages,
            Usage = totalUsage,
            CreatedAt = Workflow.UtcNow,
        };

        // Fix 2 (P1-3): also append max-iteration turns. Previously isFinal was never true
        // when the cap was hit, so nothing was written to the external store.
        if (UseExternalStoreMode)
        {
            await Workflow.ExecuteActivityAsync(
                (AgentActivities a) => a.AppendAgentTurnAsync(new AppendAgentTurnInput
                {
                    AgentName = _input!.AgentName,
                    SessionId = Workflow.Info.WorkflowId,
                    Request = runRequest,
                    TurnResponse = abortedResponse,
                }),
                new ActivityOptions
                {
                    StartToCloseTimeout = _input!.ActivityTimeout,
                    HeartbeatTimeout = _input!.HeartbeatTimeout,
                    Summary = AgentActivities.BuildActivitySummary(_input!.AgentName),
                    RetryPolicy = _input!.RetryPolicy,
                }).ConfigureAwait(true);
        }

        return abortedResponse;
    }

    private List<ChatMessage> FlattenHistoryMessages()
    {
        var totalMessageCount = 0;
        foreach (var entry in History)
        {
            totalMessageCount += entry.Messages.Count;
        }

        var messages = new List<ChatMessage>(totalMessageCount);
        foreach (var entry in History)
        {
            foreach (var m in entry.Messages)
            {
                messages.Add(m);
            }
        }

        return messages;
    }

    private ActivityOptions ResolveDurableToolActivityOptions(string toolName)
    {
        if (_input!.DurableAgentToolActivityOptions is not null
            && _input!.DurableAgentToolActivityOptions.TryGetValue(toolName, out var perTool))
        {
            return perTool;
        }

        return new ActivityOptions
        {
            StartToCloseTimeout = _input!.ActivityTimeout,
            HeartbeatTimeout = _input!.HeartbeatTimeout,
            Summary = toolName,
            RetryPolicy = _input!.RetryPolicy,
        };
    }

    private async Task ProcessFireAndForgetAsync(RunRequest request)
    {
        try
        {
            var requestEntry = AgentSessionRequest.FromRunRequest(request, Workflow.UtcNow);
            await RunTurnAsync(requestEntry, chatOptions: null).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Workflow.Logger.LogFireAndForgetActivityFailed(
                _input?.AgentName ?? "unknown", Workflow.Info.WorkflowId, ex);
            // Swallow — fire-and-forget errors must not crash the session.
        }
    }
}
