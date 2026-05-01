using System.Text.Json;
using Microsoft.Agents.AI;
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
[Workflow("Temporalio.Extensions.Agents.AgentWorkflow")]
internal class AgentWorkflow
{
    internal static readonly SearchAttributeKey<string> AgentNameSearchAttribute =
        SearchAttributeKey.CreateKeyword("AgentName");

    // Shared with DurableChatWorkflow (Temporalio.Extensions.AI) so a single Temporal
    // list query can span both workflow types.
    internal static readonly SearchAttributeKey<DateTimeOffset> SessionCreatedAtSearchAttribute =
        DurableSessionAttributes.SessionCreatedAt;

    internal static readonly SearchAttributeKey<long> TurnCountSearchAttribute =
        DurableSessionAttributes.TurnCount;

    private List<DurableSessionEntry> _history = new(16);
    private int _turnCount;
    private bool _isProcessing;
    private bool _shutdownRequested;
    private AgentWorkflowInput? _input;

    // GAP 6: StateBag persisted across turns so AIContextProvider state survives replay.
    private JsonElement? _currentStateBag;

    // GAP 3: Human-in-the-Loop state machine delegated to shared mixin.
    private readonly DurableApprovalMixin _approval = new();

    [WorkflowRun]
    public async Task RunAsync(AgentWorkflowInput input)
    {
        _input = input;

        // Restore history carried forward from a previous run (continue-as-new scenario).
        if (input.CarriedHistory is { Count: > 0 })
        {
            if (_history.Capacity < input.CarriedHistory.Count)
                _history.Capacity = input.CarriedHistory.Count;
            _history.AddRange(input.CarriedHistory);
            foreach (var e in input.CarriedHistory)
                if (e is DurableSessionResponse) _turnCount++;
        }

        // Restore StateBag carried across continue-as-new.
        _currentStateBag = input.CarriedStateBag;

        // Capture the original creation time on the first run; carry it forward on CAN transitions.
        var sessionCreatedAt = input.OriginalCreatedAt ?? Workflow.UtcNow;

        TimeSpan ttl = input.TimeToLive ?? TimeSpan.FromDays(14);

        Workflow.Logger.LogWorkflowStarted(input.AgentName, Workflow.Info.WorkflowId, ttl);

        // Opt-in: upsert search attributes only when explicitly requested.
        // Guards against failure on servers where the attributes are not pre-registered.
        if (input.EnableSearchAttributes)
        {
            Workflow.UpsertTypedSearchAttributes(
                AgentNameSearchAttribute.ValueSet(input.AgentName),
                SessionCreatedAtSearchAttribute.ValueSet(sessionCreatedAt),
                TurnCountSearchAttribute.ValueSet(_history.Count));
        }

        // Wait until shutdown is requested, TTL elapses, or history is large enough to warrant continue-as-new.
        bool conditionMet = await Workflow.WaitConditionAsync(
            () => _shutdownRequested || (!_isProcessing && (Workflow.ContinueAsNewSuggested || _history.Count >= input.MaxEntryCount)),
            timeout: ttl);

        if (!conditionMet)
        {
            // TTL elapsed without condition being met — session complete.
            Workflow.Logger.LogWorkflowTTLExpired(input.AgentName, Workflow.Info.WorkflowId);
        }
        else if ((Workflow.ContinueAsNewSuggested || _history.Count >= input.MaxEntryCount) && !_shutdownRequested)
        {
            Workflow.Logger.LogWorkflowContinueAsNew(input.AgentName, Workflow.Info.WorkflowId, _history.Count);

            // Apply the optional history reducer before carrying history forward.
            IReadOnlyList<DurableSessionEntry> carriedHistory =
                input.HistoryReducer?.Invoke(_history.ToList()).ToList() ?? _history.ToList();
            var carriedStateBag = _currentStateBag;
            var canInput = new AgentWorkflowInput
            {
                AgentName = input.AgentName,
                TaskQueue = input.TaskQueue,
                TimeToLive = input.TimeToLive,
                CarriedHistory = carriedHistory,
                CarriedStateBag = carriedStateBag,
                ActivityStartToCloseTimeout = input.ActivityStartToCloseTimeout,
                ActivityHeartbeatTimeout = input.ActivityHeartbeatTimeout,
                ApprovalTimeout = input.ApprovalTimeout,
                RetryPolicy = input.RetryPolicy,
                MaxEntryCount = input.MaxEntryCount,
                HistoryReducer = input.HistoryReducer,
                EnableSearchAttributes = input.EnableSearchAttributes,
                OriginalCreatedAt = sessionCreatedAt,
            };
            throw Workflow.CreateContinueAsNewException(
                (AgentWorkflow wf) => wf.RunAsync(canInput));
        }
    }

    /// <summary>
    /// Validates that a <see cref="RunAgentAsync"/> request is well-formed before it enters history.
    /// </summary>
    [WorkflowUpdateValidator(nameof(RunAgentAsync))]
    public void ValidateRunAgent(RunRequest request)
    {
        if (_shutdownRequested)
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
        // Serialize: wait for any in-progress run to finish first.
        await Workflow.WaitConditionAsync(() => !_isProcessing);
        _isProcessing = true;

        Workflow.Logger.LogWorkflowUpdateReceived(_input!.AgentName, Workflow.Info.WorkflowId, request.CorrelationId ?? string.Empty);

        try
        {
            // Intentional: request is added before the activity executes because the activity
            // input includes the full history (the request must be part of it). If the activity
            // fails, this entry remains in history without a matching response.
            _history.Add(AgentSessionRequest.FromRunRequest(request, Workflow.UtcNow));

            // GAP 6: pass the stored StateBag so the activity can restore provider state.
            // _history is passed directly (not copied) — the activity input is serialized
            // eagerly by Workflow.ExecuteActivityAsync, snapshotting the contents at dispatch.
            var activityInput = new ExecuteAgentInput(
                _input!.AgentName,
                request,
                _history,
                _currentStateBag);

            var result = await Workflow.ExecuteActivityAsync(
                (AgentActivities a) => a.ExecuteAgentAsync(activityInput),
                new ActivityOptions
                {
                    StartToCloseTimeout = _input!.ActivityStartToCloseTimeout ?? TimeSpan.FromMinutes(30),
                    HeartbeatTimeout = _input!.ActivityHeartbeatTimeout ?? TimeSpan.FromMinutes(5),
                    Summary = AgentActivities.BuildActivitySummary(_input!.AgentName),
                    RetryPolicy = _input!.RetryPolicy,
                });

            // GAP 6: persist the updated StateBag for the next turn.
            _currentStateBag = result.SerializedStateBag;

            _history.Add(AgentSessionResponse.FromAgentResponse(request.CorrelationId!, result.Response, Workflow.UtcNow));
            _turnCount++;

            // Update turn count for operational queries (opt-in only).
            if (_input!.EnableSearchAttributes)
            {
                Workflow.UpsertTypedSearchAttributes(
                    TurnCountSearchAttribute.ValueSet(_turnCount));
            }

            Workflow.Logger.LogWorkflowUpdateCompleted(_input!.AgentName, Workflow.Info.WorkflowId, request.CorrelationId ?? string.Empty);
            return result.Response;
        }
        finally
        {
            _isProcessing = false;
        }
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

    /// <summary>
    /// Requests graceful shutdown of this workflow.
    /// </summary>
    [WorkflowSignal("Shutdown")]
    public Task RequestShutdownAsync()
    {
        Workflow.Logger.LogWorkflowShutdownRequested(_input?.AgentName ?? "unknown", Workflow.Info.WorkflowId);
        _shutdownRequested = true;
        return Task.CompletedTask;
    }

    /// <summary>
    /// Returns the current conversation history.
    /// </summary>
    [WorkflowQuery("GetHistory")]
    public IReadOnlyList<DurableSessionEntry> GetHistory() => _history;

    // ── GAP 3: Human-in-the-Loop ────────────────────────────────────────────

    /// <summary>
    /// Validates that a <see cref="RequestApprovalAsync"/> request is well-formed before it enters history.
    /// </summary>
    [WorkflowUpdateValidator(nameof(RequestApprovalAsync))]
    public void ValidateRequestApproval(DurableApprovalRequest request) =>
        _approval.ValidateRequestApproval(request);

    /// <summary>
    /// Blocks until a human submits a decision via <see cref="SubmitApprovalAsync"/>.
    /// Called from inside a tool via <see cref="TemporalAgentContext.RequestApprovalAsync"/>.
    /// </summary>
    /// <remarks>
    /// <b>Timeout note:</b> the calling activity blocks for the duration of human review.
    /// Set <see cref="AgentWorkflowInput.ActivityStartToCloseTimeout"/> to a value that
    /// exceeds your expected review time (e.g. <c>TimeSpan.FromHours(24)</c>).
    /// </remarks>
    [WorkflowUpdate("RequestApproval")]
    public Task<DurableApprovalDecision> RequestApprovalAsync(DurableApprovalRequest request) =>
        _approval.RequestApprovalAsync(
            request,
            approvalTimeout: _input?.ApprovalTimeout ?? TimeSpan.FromDays(7),
            onRequested: req => Workflow.Logger.LogWorkflowApprovalRequested(
                _input?.AgentName ?? "unknown", Workflow.Info.WorkflowId,
                req.RequestId, req.Description ?? req.RequestId),
            onResolved: d => Workflow.Logger.LogWorkflowApprovalResolved(
                _input?.AgentName ?? "unknown", Workflow.Info.WorkflowId,
                d.RequestId, approved: d.Approved));

    /// <summary>
    /// Validates that a <see cref="SubmitApprovalAsync"/> decision is well-formed before it enters history.
    /// </summary>
    [WorkflowUpdateValidator(nameof(SubmitApprovalAsync))]
    public void ValidateSubmitApproval(DurableApprovalDecision decision) =>
        _approval.ValidateSubmitApproval(decision);

    /// <summary>
    /// Submits the human decision for the pending approval request.
    /// Unblocks the tool that called <see cref="RequestApprovalAsync"/>.
    /// </summary>
    [WorkflowUpdate("SubmitApproval")]
    public Task<DurableApprovalDecision> SubmitApprovalAsync(DurableApprovalDecision decision) =>
        Task.FromResult(_approval.SubmitApprovalAsync(decision));

    /// <summary>
    /// Returns the currently pending approval request, or <see langword="null"/> if none.
    /// Use this query to poll for pending approvals from a UI or monitoring tool.
    /// </summary>
    [WorkflowQuery("GetPendingApproval")]
    public DurableApprovalRequest? GetPendingApproval() => _approval.GetPendingApproval();

    private async Task ProcessFireAndForgetAsync(RunRequest request)
    {
        await Workflow.WaitConditionAsync(() => !_isProcessing);
        _isProcessing = true;
        int historyCountBefore = _history.Count;   // snapshot before add
        try
        {
            _history.Add(AgentSessionRequest.FromRunRequest(request, Workflow.UtcNow));

            var activityInput = new ExecuteAgentInput(
                _input!.AgentName,
                request,
                _history,
                _currentStateBag);

            var result = await Workflow.ExecuteActivityAsync(
                (AgentActivities a) => a.ExecuteAgentAsync(activityInput),
                new ActivityOptions
                {
                    StartToCloseTimeout = _input!.ActivityStartToCloseTimeout ?? TimeSpan.FromMinutes(30),
                    HeartbeatTimeout = _input!.ActivityHeartbeatTimeout ?? TimeSpan.FromMinutes(5),
                    Summary = AgentActivities.BuildActivitySummary(_input!.AgentName),
                    RetryPolicy = _input!.RetryPolicy,
                });

            _currentStateBag = result.SerializedStateBag;
            _history.Add(AgentSessionResponse.FromAgentResponse(
                request.CorrelationId!, result.Response, Workflow.UtcNow));
        }
        catch (Exception ex)
        {
            // Rollback orphaned request entry to keep history balanced across CAN.
            while (_history.Count > historyCountBefore)
                _history.RemoveAt(_history.Count - 1);
            Workflow.Logger.LogFireAndForgetActivityFailed(
                _input?.AgentName ?? "unknown", Workflow.Info.WorkflowId, ex);
            // Swallow — fire-and-forget errors must not crash the session.
        }
        finally
        {
            _isProcessing = false;
        }
    }
}
