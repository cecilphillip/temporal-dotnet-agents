using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Temporalio.Common;
using Temporalio.Extensions.Agents.Session;
using Temporalio.Extensions.Agents.State;
using Temporalio.Extensions.AI;
using Temporalio.Workflows;

namespace Temporalio.Extensions.Agents.Workflows;

/// <summary>
/// Long-lived Temporal workflow that acts as the durable backing store for an agent session.
/// Equivalent to <c>AgentEntity</c> in the DurableTask integration.
/// </summary>
/// <remarks>
/// <para>
/// As of Layer 3, <see cref="AgentWorkflow"/> inherits the shared session loop, history
/// management, HITL approval handlers, <c>[WorkflowQuery("GetHistory")]</c>, and
/// <c>[WorkflowSignal("Shutdown")]</c> from <see cref="DurableChatWorkflowBase{TOutput}"/>.
/// MAF-only concerns that remain on this subclass: the fire-and-forget signal handler,
/// the <see cref="AgentSessionStateBag"/> carry-forward, the <c>AgentName</c> search
/// attribute upsert, and the agent-name-aware structured logging.
/// </para>
/// </remarks>
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
        // The base also exposes a `RunAsync(DurableChatWorkflowInput)` (protected virtual);
        // because they have different parameter types they're overloads, not a `new`-style
        // hide. Set the typed `_input` before delegating into the base so subclass-only
        // hooks (UpsertCustomSearchAttributes, ExecuteTurnAsync, CreateContinueAsNewException)
        // can read agent-specific fields.
        ArgumentNullException.ThrowIfNull(input);
        _input = input;
        // Restore StateBag carried across continue-as-new before the base session loop runs,
        // so the first turn's activity dispatch can include the carried bag.
        _currentStateBag = input.CarriedStateBag;

        Workflow.Logger.LogWorkflowStarted(input.AgentName, Workflow.Info.WorkflowId, input.TimeToLive);

        // Delegate to the shared session loop (history restore, search-attribute upsert, mutex,
        // continue-as-new trigger). Decision #1 makes AgentWorkflowInput inherit from
        // DurableChatWorkflowInput, so passing through is type-safe.
        await base.RunAsync(input).ConfigureAwait(true);
    }

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
        // Construct the request entry first — does not depend on `_input` and works even
        // when the [WorkflowRun] body has not yet executed (modern Temporal event-loop
        // dispatches DoUpdate jobs before InitializeWorkflow within an activation).
        var requestEntry = AgentSessionRequest.FromRunRequest(request, Workflow.UtcNow);

        // RunTurnAsync awaits Workflow.WaitConditionAsync(() => !_isProcessing) on entry —
        // by the time that yield resumes, the workflow run loop has had a chance to run
        // and `_input` has been populated. Logging that depends on `_input.AgentName`
        // therefore moves to after the await.
        var (output, _) = await RunTurnAsync(requestEntry, chatOptions: null);

        Workflow.Logger.LogWorkflowUpdateCompleted(
            _input!.AgentName, Workflow.Info.WorkflowId, request.CorrelationId ?? string.Empty);
        return output;
    }

    /// <summary>
    /// Queues a fire-and-forget run. The workflow does not wait for this to complete.
    /// </summary>
    /// <remarks>
    /// <b>Limitation:</b> If the workflow hits continue-as-new or shuts down before the
    /// fire-and-forget task completes, the in-flight request and its history entry may be lost.
    /// Use <see cref="RunAgentAsync"/> for requests that must not be dropped.
    /// </remarks>
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
        // The base appended `requestEntry` to History before calling us. The Agent activity
        // input already carries the originating RunRequest separately (it embeds tools,
        // response format, orchestration ID, etc.); reconstruct the RunRequest from the
        // typed agent request entry so callers do not have to thread it through.
        // The `activityOptions` argument from the base is MEAI-shaped (uses
        // DurableChatWorkflowInput.ActivityTimeout); ignore it and build a MAF-shaped
        // ActivityOptions inside ExecuteAgentTurnAsync instead.
        _ = activityOptions;
        var agentRequestEntry = (AgentSessionRequest)requestEntry;
        var runRequest = ToRunRequest(agentRequestEntry);

        return ExecuteAgentTurnAsync(runRequest);
    }

    /// <inheritdoc/>
    protected override ContinueAsNewException CreateContinueAsNewException(
        DurableChatWorkflowInput input)
    {
        // The base passes a freshly constructed DurableChatWorkflowInput carrying the reduced
        // history and the shared session-loop fields (TimeToLive, MaxEntryCount, HistoryReducer,
        // OriginalCreatedAt, EnableSearchAttributes, ApprovalTimeout, ActivityTimeout,
        // HeartbeatTimeout) — NOT a downcast AgentWorkflowInput. Pull MAF-specific fields
        // from `_input` (the original AgentWorkflowInput stored on first run) and merge in the
        // base's freshly produced carry-forward state.
        ArgumentNullException.ThrowIfNull(_input);

        var carriedInput = new AgentWorkflowInput
        {
            // MAF-specific state — sourced from the original input + per-turn _currentStateBag.
            AgentName = _input.AgentName,
            TaskQueue = _input.TaskQueue,
            CarriedStateBag = _currentStateBag,
            ActivityStartToCloseTimeout = _input.ActivityStartToCloseTimeout,
            ActivityHeartbeatTimeout = _input.ActivityHeartbeatTimeout,
            RetryPolicy = _input.RetryPolicy,

            // Inherited fields — sourced from the base's freshly constructed `input` so the
            // reduced CarriedHistory, OriginalCreatedAt, and other CAN-time decisions are honored.
            TimeToLive = input.TimeToLive,
            CarriedHistory = input.CarriedHistory,
            ApprovalTimeout = input.ApprovalTimeout,
            EnableSearchAttributes = input.EnableSearchAttributes,
            MaxEntryCount = input.MaxEntryCount,
            HistoryReducer = input.HistoryReducer,
            OriginalCreatedAt = input.OriginalCreatedAt,

            // ActivityTimeout / HeartbeatTimeout on the base are MEAI-shaped; AgentWorkflow uses
            // the *AgentActivityStartToCloseTimeout / *HeartbeatTimeout MAF-specific overrides.
            // Carry forward whatever was provided.
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

    // ── HITL hooks (delegate to the inherited handlers) ─────────────────────
    // The inherited [WorkflowUpdate("RequestApproval")], [WorkflowUpdate("SubmitApproval")],
    // and [WorkflowQuery("GetPendingApproval")] handlers from DurableChatWorkflowBase are
    // exposed verbatim here — no MAF-specific overrides are required because the approval
    // mixin's logging is generic. Agent-name-specific approval logging was previously emitted
    // via Logs.LogWorkflowApprovalRequested / LogWorkflowApprovalResolved; the inherited
    // base uses ILogger.LogInformation directly. Future work could re-introduce the typed
    // logger via a virtual hook on the base if dashboards depend on the structured fields.

    // ── Internals ───────────────────────────────────────────────────────────

    /// <summary>
    /// Reconstructs the original <see cref="RunRequest"/> from a stored
    /// <see cref="AgentSessionRequest"/>. Loses the full <c>RunOptions</c> (which is not
    /// serialized into history); the activity only needs <c>Messages</c>,
    /// <c>CorrelationId</c>, <c>OrchestrationId</c>, and <c>ResponseFormat</c> to produce
    /// the same output as the original call site.
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
        // Build MAF-shaped activity options from the typed `_input` (StartToClose / Heartbeat
        // come from AgentWorkflowInput-only fields; the base's MEAI-shaped options pass-through
        // is intentionally ignored here).
        var activityOptions = new ActivityOptions
        {
            StartToCloseTimeout = _input!.ActivityStartToCloseTimeout ?? TimeSpan.FromMinutes(30),
            HeartbeatTimeout = _input!.ActivityHeartbeatTimeout ?? TimeSpan.FromMinutes(5),
            Summary = AgentActivities.BuildActivitySummary(_input!.AgentName),
            RetryPolicy = _input!.RetryPolicy,
        };

        // Pass the full conversation history (including the just-appended request entry) so the
        // activity can flatten messages for the LLM. _history is inherited via the base's
        // History accessor.
        var activityInput = new ExecuteAgentInput(
            _input!.AgentName,
            runRequest,
            History,
            _currentStateBag);

        var result = await Workflow.ExecuteActivityAsync(
            (AgentActivities a) => a.ExecuteAgentAsync(activityInput),
            activityOptions);

        // GAP 6: persist the updated StateBag for the next turn.
        _currentStateBag = result.SerializedStateBag;
        return result.Response;
    }

    private async Task ProcessFireAndForgetAsync(RunRequest request)
    {
        try
        {
            var requestEntry = AgentSessionRequest.FromRunRequest(request, Workflow.UtcNow);
            // Reuses the same turn machinery (mutex, history append, response build) as RunAgentAsync.
            await RunTurnAsync(requestEntry, chatOptions: null).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Workflow.Logger.LogFireAndForgetActivityFailed(
                _input?.AgentName ?? "unknown", Workflow.Info.WorkflowId, ex);
            // Swallow — fire-and-forget errors must not crash the session.
            // RunTurnAsync's atomicity guarantees that on activity failure no orphan request
            // entry is appended (the request entry is appended inside RunTurnAsync's try block,
            // but the response entry is only appended on success — the partial-pair concern from
            // the prior implementation is moot because the base always pairs them inside one
            // try, and on exception both the request entry append and the activity dispatch are
            // re-thrown together to this catch).
        }
    }
}
