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

    internal static readonly SearchAttributeKey<DateTimeOffset> SessionCreatedAtSearchAttribute =
        SearchAttributeKey.CreateDateTimeOffset("SessionCreatedAt");

    internal static readonly SearchAttributeKey<long> TurnCountSearchAttribute =
        SearchAttributeKey.CreateLong("TurnCount");

    private readonly List<TemporalAgentStateEntry> _history = [];
    private bool _isProcessing;
    private bool _shutdownRequested;
    private AgentWorkflowInput? _input;

    // GAP 6: StateBag persisted across turns so AIContextProvider state survives replay.
    private JsonElement? _currentStateBag;

    // GAP 3: Human-in-the-Loop state.
    private DurableApprovalRequest? _pendingApproval;
    private DurableApprovalDecision? _approvalDecision;

    [WorkflowRun]
    public async Task RunAsync(AgentWorkflowInput input)
    {
        _input = input;

        // Restore history carried forward from a previous run (continue-as-new scenario).
        _history.AddRange(input.CarriedHistory);

        // Restore StateBag carried across continue-as-new.
        _currentStateBag = input.CarriedStateBag;

        TimeSpan ttl = input.TimeToLive ?? TimeSpan.FromDays(14);

        Workflow.Logger.LogWorkflowStarted(input.AgentName, Workflow.Info.WorkflowId, ttl);

        // Upsert search attributes for operational queries in the Temporal UI.
        Workflow.UpsertTypedSearchAttributes(
            AgentNameSearchAttribute.ValueSet(input.AgentName),
            SessionCreatedAtSearchAttribute.ValueSet(Workflow.UtcNow),
            TurnCountSearchAttribute.ValueSet(_history.Count));

        // Wait until shutdown is requested, TTL elapses, or history is large enough to warrant continue-as-new.
        bool conditionMet = await Workflow.WaitConditionAsync(
            () => _shutdownRequested || (!_isProcessing && Workflow.ContinueAsNewSuggested),
            timeout: ttl);

        if (!conditionMet)
        {
            // TTL elapsed without condition being met — session complete.
            Workflow.Logger.LogWorkflowTTLExpired(input.AgentName, Workflow.Info.WorkflowId);
        }
        else if (Workflow.ContinueAsNewSuggested && !_shutdownRequested)
        {
            Workflow.Logger.LogWorkflowContinueAsNew(input.AgentName, Workflow.Info.WorkflowId, _history.Count);

            // Transfer history and StateBag to a fresh workflow run.
            var carriedHistory = _history.ToList();
            var carriedStateBag = _currentStateBag;
            throw Workflow.CreateContinueAsNewException(
                (AgentWorkflow wf) => wf.RunAsync(new AgentWorkflowInput
                {
                    AgentName = input.AgentName,
                    TaskQueue = input.TaskQueue,
                    TimeToLive = input.TimeToLive,
                    CarriedHistory = carriedHistory,
                    CarriedStateBag = carriedStateBag,
                    ActivityStartToCloseTimeout = input.ActivityStartToCloseTimeout,
                    ActivityHeartbeatTimeout = input.ActivityHeartbeatTimeout,
                    ApprovalTimeout = input.ApprovalTimeout
                }));
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

        Workflow.Logger.LogWorkflowUpdateReceived(_input!.AgentName, Workflow.Info.WorkflowId, request.CorrelationId);

        try
        {
            // Intentional: request is added before the activity executes because the activity
            // input includes the full history (the request must be part of it). If the activity
            // fails, this entry remains in history without a matching response.
            _history.Add(TemporalAgentStateRequest.FromRunRequest(request));

            // GAP 6: pass the stored StateBag so the activity can restore provider state.
            var activityInput = new ExecuteAgentInput(
                _input!.AgentName,
                request,
                [.. _history],
                _currentStateBag);

            var result = await Workflow.ExecuteActivityAsync(
                (AgentActivities a) => a.ExecuteAgentAsync(activityInput),
                new ActivityOptions
                {
                    StartToCloseTimeout = _input!.ActivityStartToCloseTimeout ?? TimeSpan.FromMinutes(30),
                    HeartbeatTimeout = _input!.ActivityHeartbeatTimeout ?? TimeSpan.FromMinutes(5)
                });

            // GAP 6: persist the updated StateBag for the next turn.
            _currentStateBag = result.SerializedStateBag;

            _history.Add(TemporalAgentStateResponse.FromResponse(request.CorrelationId, result.Response));

            // Update turn count for operational queries.
            Workflow.UpsertTypedSearchAttributes(
                TurnCountSearchAttribute.ValueSet(_history.Count(e => e is TemporalAgentStateResponse)));

            Workflow.Logger.LogWorkflowUpdateCompleted(_input!.AgentName, Workflow.Info.WorkflowId, request.CorrelationId);
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
    public IReadOnlyList<TemporalAgentStateEntry> GetHistory() => _history;

    // ── GAP 3: Human-in-the-Loop ────────────────────────────────────────────

    /// <summary>
    /// Validates that a <see cref="RequestApprovalAsync"/> request is well-formed before it enters history.
    /// </summary>
    [WorkflowUpdateValidator(nameof(RequestApprovalAsync))]
    public void ValidateRequestApproval(DurableApprovalRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrEmpty(request.RequestId))
            throw new ArgumentException("RequestId must not be null or empty.");
    }

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
    public async Task<DurableApprovalDecision> RequestApprovalAsync(DurableApprovalRequest request)
    {
        _pendingApproval = request;
        _approvalDecision = null;

        Workflow.Logger.LogWorkflowApprovalRequested(_input?.AgentName ?? "unknown",
            Workflow.Info.WorkflowId, request.RequestId, request.Description ?? request.RequestId);

        var timeout = _input?.ApprovalTimeout ?? TimeSpan.FromDays(7);
        var conditionMet = await Workflow.WaitConditionAsync(
            () => _approvalDecision != null && _approvalDecision.RequestId == request.RequestId,
            timeout: timeout);

        if (!conditionMet)
        {
            Workflow.Logger.LogWorkflowApprovalResolved(_input?.AgentName ?? "unknown",
                Workflow.Info.WorkflowId, request.RequestId, approved: false);

            _pendingApproval = null;
            _approvalDecision = null;

            return new DurableApprovalDecision
            {
                RequestId = request.RequestId,
                Approved = false,
                Reason = $"Approval timed out after {timeout.TotalHours:F0} hours with no human response."
            };
        }

        var decision = _approvalDecision!;
        _pendingApproval = null;
        _approvalDecision = null;

        Workflow.Logger.LogWorkflowApprovalResolved(_input?.AgentName ?? "unknown",
            Workflow.Info.WorkflowId, request.RequestId, decision.Approved);

        return decision;
    }

    /// <summary>
    /// Validates that a <see cref="SubmitApprovalAsync"/> decision is well-formed before it enters history.
    /// </summary>
    [WorkflowUpdateValidator(nameof(SubmitApprovalAsync))]
    public void ValidateSubmitApproval(DurableApprovalDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);

        if (_pendingApproval is null)
        {
            throw new InvalidOperationException(
                "No approval request is pending. Ensure RequestApprovalAsync was called first.");
        }

        if (_pendingApproval.RequestId != decision.RequestId)
        {
            throw new InvalidOperationException(
                $"Decision RequestId '{decision.RequestId}' does not match pending request '{_pendingApproval.RequestId}'.");
        }
    }

    /// <summary>
    /// Submits the human decision for the pending approval request.
    /// Unblocks the tool that called <see cref="RequestApprovalAsync"/>.
    /// </summary>
    [WorkflowUpdate("SubmitApproval")]
    public Task<DurableApprovalDecision> SubmitApprovalAsync(DurableApprovalDecision decision)
    {
        _approvalDecision = decision;
        return Task.FromResult(decision);
    }

    /// <summary>
    /// Returns the currently pending approval request, or <see langword="null"/> if none.
    /// Use this query to poll for pending approvals from a UI or monitoring tool.
    /// </summary>
    [WorkflowQuery("GetPendingApproval")]
    public DurableApprovalRequest? GetPendingApproval() => _pendingApproval;

    private async Task ProcessFireAndForgetAsync(RunRequest request)
    {
        await Workflow.WaitConditionAsync(() => !_isProcessing);
        _isProcessing = true;
        try
        {
            _history.Add(TemporalAgentStateRequest.FromRunRequest(request));

            var activityInput = new ExecuteAgentInput(
                _input!.AgentName,
                request,
                [.. _history],
                _currentStateBag);

            var result = await Workflow.ExecuteActivityAsync(
                (AgentActivities a) => a.ExecuteAgentAsync(activityInput),
                new ActivityOptions
                {
                    StartToCloseTimeout = _input!.ActivityStartToCloseTimeout ?? TimeSpan.FromMinutes(30),
                    HeartbeatTimeout = _input!.ActivityHeartbeatTimeout ?? TimeSpan.FromMinutes(5)
                });

            _currentStateBag = result.SerializedStateBag;
            _history.Add(TemporalAgentStateResponse.FromResponse(request.CorrelationId, result.Response));
        }
        finally
        {
            _isProcessing = false;
        }
    }
}
