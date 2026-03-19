using Microsoft.Extensions.AI;
using Temporalio.Workflows;

namespace Temporalio.Extensions.AI;

/// <summary>
/// Temporal workflow that manages a durable conversation session.
/// Conversation history is persisted in workflow state. Chat turns are executed
/// via <c>[WorkflowUpdate]</c> for synchronous request/response semantics.
/// Includes HITL approval support via <c>[WorkflowUpdate]</c> for tool approval gates.
/// </summary>
[Workflow("Temporalio.Extensions.AI.DurableChatWorkflow")]
internal sealed class DurableChatWorkflow
{
    private readonly List<ChatMessage> _history = [];
    private bool _isProcessing;
    private bool _shutdownRequested;
    private DurableChatWorkflowInput? _input;
    private int _turnCount;

    // ── HITL approval state ──────────────────────────────────────────────
    private DurableApprovalRequest? _pendingApproval;
    private DurableApprovalDecision? _approvalDecision;

    [WorkflowRun]
    public async Task RunAsync(DurableChatWorkflowInput input)
    {
        _input = input;

        // Restore history from continue-as-new.
        if (input.CarriedHistory is { Count: > 0 })
        {
            _history.AddRange(input.CarriedHistory);
        }

        var ttl = input.TimeToLive;

        // Wait until shutdown or history grows large enough for continue-as-new.
        bool conditionMet = await Workflow.WaitConditionAsync(
            () => _shutdownRequested || (!_isProcessing && Workflow.ContinueAsNewSuggested),
            timeout: ttl);

        if (!conditionMet)
        {
            // TTL elapsed — session complete.
            return;
        }

        if (Workflow.ContinueAsNewSuggested && !_shutdownRequested)
        {
            var carriedHistory = _history.ToList();
            throw Workflow.CreateContinueAsNewException(
                (DurableChatWorkflow wf) => wf.RunAsync(new DurableChatWorkflowInput
                {
                    TimeToLive = input.TimeToLive,
                    CarriedHistory = carriedHistory,
                    ActivityTimeout = input.ActivityTimeout,
                    HeartbeatTimeout = input.HeartbeatTimeout,
                    ApprovalTimeout = input.ApprovalTimeout,
                }));
        }
    }

    /// <summary>
    /// Validates a chat request before it enters workflow history.
    /// </summary>
    [WorkflowUpdateValidator(nameof(ChatAsync))]
    public void ValidateChat(DurableChatInput input)
    {
        if (_shutdownRequested)
            throw new InvalidOperationException("Session has been shut down.");
        if (input?.Messages is null || input.Messages.Count == 0)
            throw new ArgumentException("At least one message is required.");
    }

    /// <summary>
    /// Executes a chat turn: appends user messages, calls the LLM via activity,
    /// appends response, and returns the result.
    /// </summary>
    [WorkflowUpdate("Chat")]
    public async Task<DurableChatOutput> ChatAsync(DurableChatInput input)
    {
        // Serialize: wait for any in-progress turn to finish.
        await Workflow.WaitConditionAsync(() => !_isProcessing);
        _isProcessing = true;

        try
        {
            // Append user messages to history.
            foreach (var msg in input.Messages)
            {
                _history.Add(msg);
            }

            _turnCount++;

            // Build activity input with full conversation history.
            var activityInput = new DurableChatInput
            {
                Messages = [.. _history],
                Options = input.Options,
                ConversationId = input.ConversationId ?? Workflow.Info.WorkflowId,
                TurnNumber = _turnCount,
            };

            var output = await Workflow.ExecuteActivityAsync(
                (DurableChatActivities a) => a.GetResponseAsync(activityInput),
                new ActivityOptions
                {
                    StartToCloseTimeout = _input!.ActivityTimeout,
                    HeartbeatTimeout = _input!.HeartbeatTimeout,
                });

            // Append response messages to history.
            foreach (var msg in output.Response.Messages)
            {
                _history.Add(msg);
            }

            return output;
        }
        finally
        {
            _isProcessing = false;
        }
    }

    /// <summary>
    /// Returns the current conversation history.
    /// </summary>
    [WorkflowQuery("GetHistory")]
    public IReadOnlyList<ChatMessage> GetHistory() => _history;

    /// <summary>
    /// Requests graceful shutdown of this session.
    /// </summary>
    [WorkflowSignal("Shutdown")]
    public Task RequestShutdownAsync()
    {
        _shutdownRequested = true;
        return Task.CompletedTask;
    }

    // ── HITL: Tool Approval ──────────────────────────────────────────────

    /// <summary>
    /// Validates a tool approval request before it enters workflow history.
    /// </summary>
    [WorkflowUpdateValidator(nameof(RequestApprovalAsync))]
    public void ValidateRequestApproval(DurableApprovalRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrEmpty(request.RequestId))
            throw new ArgumentException("RequestId must not be null or empty.");
    }

    /// <summary>
    /// Blocks the workflow until a human submits a decision via <see cref="SubmitApprovalAsync"/>.
    /// Returns the decision as a <see cref="DurableApprovalDecision"/>.
    /// </summary>
    [WorkflowUpdate("RequestApproval")]
    public async Task<DurableApprovalDecision> RequestApprovalAsync(DurableApprovalRequest request)
    {
        _pendingApproval = request;
        _approvalDecision = null;

        var timeout = _input?.ApprovalTimeout ?? TimeSpan.FromDays(7);
        var conditionMet = await Workflow.WaitConditionAsync(
            () => _approvalDecision is not null && _approvalDecision.RequestId == request.RequestId,
            timeout: timeout);

        if (!conditionMet)
        {
            // Timed out without human response.
            _pendingApproval = null;
            _approvalDecision = null;

            return new DurableApprovalDecision
            {
                RequestId = request.RequestId,
                Approved = false,
                Reason = $"Approval timed out after {timeout.TotalHours:F0} hours with no human response.",
            };
        }

        var decision = _approvalDecision!;
        _pendingApproval = null;
        _approvalDecision = null;

        return decision;
    }

    /// <summary>
    /// Validates a submitted approval decision.
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
    /// Unblocks <see cref="RequestApprovalAsync"/>.
    /// </summary>
    [WorkflowUpdate("SubmitApproval")]
    public Task<DurableApprovalDecision> SubmitApprovalAsync(DurableApprovalDecision decision)
    {
        _approvalDecision = decision;
        return Task.FromResult(decision);
    }

    /// <summary>
    /// Returns the currently pending approval request, or null if none.
    /// </summary>
    [WorkflowQuery("GetPendingApproval")]
    public DurableApprovalRequest? GetPendingApproval() => _pendingApproval;
}
