using Temporalio.Workflows;

namespace Temporalio.Extensions.AI;

/// <summary>
/// Internal helper that encapsulates the shared HITL approval state machine.
/// Both <c>DurableChatWorkflow</c> and <c>AgentWorkflow</c> hold an instance of this class
/// and delegate their approval-related update and query methods to it.
/// </summary>
/// <remarks>
/// This class is not a workflow — it carries no <c>[Workflow]</c> attribute.
/// The host workflow is responsible for keeping its own <c>[WorkflowUpdate]</c>,
/// <c>[WorkflowUpdateValidator]</c>, and <c>[WorkflowQuery]</c> attribute declarations so
/// Temporal can discover the handlers. The methods here are thin implementations that
/// the workflow delegates to.
///
/// <b>Temporal constraint:</b> this class calls <c>Workflow.WaitConditionAsync</c> from within
/// <see cref="RequestApprovalAsync"/>. It must therefore only ever be called from workflow-thread
/// code. Because the class is <c>internal</c> that is always the case.
/// </remarks>
internal sealed class DurableApprovalMixin
{
    private DurableApprovalRequest? _pendingApproval;
    private DurableApprovalDecision? _approvalDecision;

    // ── Validators ──────────────────────────────────────────────────────────

    /// <summary>
    /// Validates an incoming <see cref="DurableApprovalRequest"/> before it enters workflow history.
    /// Throws <see cref="InvalidOperationException"/> when a request is already pending, matching
    /// the exception type used by both callers prior to this extraction.
    /// </summary>
    public void ValidateRequestApproval(DurableApprovalRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrEmpty(request.RequestId))
            throw new ArgumentException("RequestId must not be null or empty.");
        if (_pendingApproval is not null)
            throw new InvalidOperationException(
                "An approval request is already pending. Submit or timeout the current request before sending another.");
    }

    /// <summary>
    /// Validates an incoming <see cref="DurableApprovalDecision"/> before it enters workflow history.
    /// Throws <see cref="InvalidOperationException"/> when no request is pending or the RequestId
    /// does not match, matching the exception type used by both callers prior to this extraction.
    /// </summary>
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

    // ── Updates ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Stores the pending request, then blocks the workflow via
    /// <see cref="Workflow.WaitConditionAsync"/> until a matching decision arrives or
    /// <paramref name="approvalTimeout"/> elapses.
    /// </summary>
    /// <param name="request">The approval request that was validated by <see cref="ValidateRequestApproval"/>.</param>
    /// <param name="approvalTimeout">How long to wait before auto-denying.</param>
    /// <param name="onRequested">
    /// Optional callback fired once when the request is stored.
    /// Receives the request so callers can emit structured log events.
    /// </param>
    /// <param name="onResolved">
    /// Optional callback fired once when the request resolves (approved, rejected, or timed out).
    /// Receives the final decision so callers can emit structured log events.
    /// </param>
    public async Task<DurableApprovalDecision> RequestApprovalAsync(
        DurableApprovalRequest request,
        TimeSpan approvalTimeout,
        Action<DurableApprovalRequest>? onRequested = null,
        Action<DurableApprovalDecision>? onResolved = null)
    {
        _pendingApproval = request;
        _approvalDecision = null;

        onRequested?.Invoke(request);

        var conditionMet = await Workflow.WaitConditionAsync(
            () => _approvalDecision is not null && _approvalDecision.RequestId == request.RequestId,
            timeout: approvalTimeout);

        if (!conditionMet)
        {
            _pendingApproval = null;
            _approvalDecision = null;

            var timedOutDecision = new DurableApprovalDecision
            {
                RequestId = request.RequestId,
                Approved = false,
                Reason = $"Approval timed out after {approvalTimeout.TotalHours:F0} hours with no human response.",
            };

            onResolved?.Invoke(timedOutDecision);
            return timedOutDecision;
        }

        var decision = _approvalDecision!;
        _pendingApproval = null;
        _approvalDecision = null;

        onResolved?.Invoke(decision);
        return decision;
    }

    /// <summary>
    /// Stores the human decision and returns it, unblocking the
    /// <see cref="RequestApprovalAsync"/> wait condition.
    /// </summary>
    public DurableApprovalDecision SubmitApprovalAsync(DurableApprovalDecision decision)
    {
        _approvalDecision = decision;
        return decision;
    }

    /// <summary>
    /// Returns the currently pending approval request, or <see langword="null"/> if none.
    /// </summary>
    public DurableApprovalRequest? GetPendingApproval() => _pendingApproval;
}
