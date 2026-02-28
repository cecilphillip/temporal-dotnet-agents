using System.Text.Json;
using Microsoft.Agents.AI;
using Temporalio.Extensions.Agents.State;
using Temporalio.Workflows;

namespace Temporalio.Extensions.Agents;

/// <summary>
/// Long-lived Temporal workflow that acts as the durable backing store for an agent session.
/// Equivalent to <c>AgentEntity</c> in the DurableTask integration.
/// </summary>
[Workflow("Temporalio.Extensions.Agents.AgentWorkflow")]
internal class AgentWorkflow
{
    private readonly List<TemporalAgentStateEntry> _history = [];
    private bool _isProcessing;
    private bool _shutdownRequested;
    private AgentWorkflowInput? _input;

    // GAP 6: StateBag persisted across turns so AIContextProvider state survives replay.
    private JsonElement? _currentStateBag;

    // GAP 3: Human-in-the-Loop state.
    private ApprovalRequest? _pendingApproval;
    private ApprovalDecision? _approvalDecision;

    [WorkflowRun]
    public async Task RunAsync(AgentWorkflowInput input)
    {
        _input = input;

        // Restore history carried forward from a previous run (continue-as-new scenario).
        _history.AddRange(input.CarriedHistory);

        // Restore StateBag carried across continue-as-new.
        _currentStateBag = input.CarriedStateBag;

        TimeSpan ttl = input.TimeToLive ?? TimeSpan.FromDays(14);

        Logs.LogWorkflowStarted(Workflow.Logger, input.AgentName, Workflow.Info.WorkflowId, ttl);

        // Wait until shutdown is requested, TTL elapses, or history is large enough to warrant continue-as-new.
        bool conditionMet = await Workflow.WaitConditionAsync(
            () => _shutdownRequested || (!_isProcessing && Workflow.ContinueAsNewSuggested),
            timeout: ttl);

        if (!conditionMet)
        {
            // TTL elapsed without condition being met — session complete.
            Logs.LogWorkflowTTLExpired(Workflow.Logger, input.AgentName, Workflow.Info.WorkflowId);
        }
        else if (Workflow.ContinueAsNewSuggested && !_shutdownRequested)
        {
            Logs.LogWorkflowContinueAsNew(Workflow.Logger, input.AgentName, Workflow.Info.WorkflowId, _history.Count);

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
                    ActivityHeartbeatTimeout = input.ActivityHeartbeatTimeout
                }));
        }
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

        Logs.LogWorkflowUpdateReceived(Workflow.Logger, _input!.AgentName, Workflow.Info.WorkflowId, request.CorrelationId);

        try
        {
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

            Logs.LogWorkflowUpdateCompleted(Workflow.Logger, _input!.AgentName, Workflow.Info.WorkflowId, request.CorrelationId);
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
        Logs.LogWorkflowShutdownRequested(Workflow.Logger, _input?.AgentName ?? "unknown", Workflow.Info.WorkflowId);
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
    /// Blocks until a human submits a decision via <see cref="SubmitApprovalAsync"/>.
    /// Called from inside a tool via <see cref="TemporalAgentContext.RequestApprovalAsync"/>.
    /// </summary>
    /// <remarks>
    /// <b>Timeout note:</b> the calling activity blocks for the duration of human review.
    /// Set <see cref="AgentWorkflowInput.ActivityStartToCloseTimeout"/> to a value that
    /// exceeds your expected review time (e.g. <c>TimeSpan.FromHours(24)</c>).
    /// </remarks>
    [WorkflowUpdate("RequestApproval")]
    public async Task<ApprovalTicket> RequestApprovalAsync(ApprovalRequest request)
    {
        _pendingApproval = request;
        _approvalDecision = null;

        Logs.LogWorkflowApprovalRequested(Workflow.Logger, _input?.AgentName ?? "unknown",
            Workflow.Info.WorkflowId, request.RequestId, request.Action);

        await Workflow.WaitConditionAsync(
            () => _approvalDecision != null && _approvalDecision.RequestId == request.RequestId);

        var decision = _approvalDecision!;
        _pendingApproval = null;
        _approvalDecision = null;

        Logs.LogWorkflowApprovalResolved(Workflow.Logger, _input?.AgentName ?? "unknown",
            Workflow.Info.WorkflowId, request.RequestId, decision.Approved);

        return new ApprovalTicket
        {
            RequestId = decision.RequestId,
            Approved = decision.Approved,
            Comment = decision.Comment
        };
    }

    /// <summary>
    /// Submits the human decision for the pending approval request.
    /// Unblocks the tool that called <see cref="RequestApprovalAsync"/>.
    /// </summary>
    [WorkflowUpdate("SubmitApproval")]
    public Task<ApprovalTicket> SubmitApprovalAsync(ApprovalDecision decision)
    {
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

        _approvalDecision = decision;

        return Task.FromResult(new ApprovalTicket
        {
            RequestId = decision.RequestId,
            Approved = decision.Approved,
            Comment = decision.Comment
        });
    }

    /// <summary>
    /// Returns the currently pending approval request, or <see langword="null"/> if none.
    /// Use this query to poll for pending approvals from a UI or monitoring tool.
    /// </summary>
    [WorkflowQuery("GetPendingApproval")]
    public ApprovalRequest? GetPendingApproval() => _pendingApproval;

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
