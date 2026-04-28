using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Temporalio.Workflows;

namespace Temporalio.Extensions.AI;

/// <summary>
/// Abstract base class for durable chat workflows with typed turn output.
/// Provides the shared session loop, conversation history, HITL approval support,
/// continue-as-new handling, search attribute upserts, and serialized turn execution.
/// Concrete subclasses implement the three abstract members to dispatch to their
/// own activities and define how assistant messages are extracted from the output.
/// </summary>
/// <typeparam name="TOutput">The type returned from each completed chat turn.</typeparam>
public abstract class DurableChatWorkflowBase<TOutput>
{
    private List<ChatMessage> _history = new(16);
    private readonly DurableApprovalMixin _approval = new();
    private bool _isProcessing;
    private bool _shutdownRequested;
    private int _turnCount;

    /// <summary>
    /// The workflow input set at the start of <see cref="RunAsync"/>.
    /// Available to subclasses after the first call to <see cref="RunAsync"/>.
    /// </summary>
    protected DurableChatWorkflowInput? Input { get; private set; }

    /// <summary>
    /// Returns <see langword="true"/> once a <c>Shutdown</c> signal has been received.
    /// Subclass update validators can use this to reject new turns after shutdown.
    /// </summary>
    protected bool IsShutdownRequested => _shutdownRequested;

    // ── Abstract members ────────────────────────────────────────────────────

    /// <summary>
    /// Extracts the assistant messages from a completed turn output so they can be
    /// appended to the conversation history.
    /// </summary>
    protected abstract IEnumerable<ChatMessage> GetHistoryMessages(TOutput output);

    /// <summary>
    /// Dispatches the LLM call (or equivalent) as a Temporal activity.
    /// Called by <see cref="RunTurnAsync"/> on each turn.
    /// </summary>
    protected abstract Task<TOutput> ExecuteTurnAsync(
        ActivityOptions activityOptions,
        DurableChatInput activityInput);

    /// <summary>
    /// Creates the <see cref="ContinueAsNewException"/> typed to the concrete workflow class.
    /// Called by <see cref="RunAsync"/> when the workflow history grows large enough to
    /// trigger a continue-as-new transition.
    /// </summary>
    protected abstract ContinueAsNewException CreateContinueAsNewException(
        DurableChatWorkflowInput input);

    // ── Session loop ────────────────────────────────────────────────────────

    /// <summary>
    /// Runs the durable session loop. Subclasses annotate their own <c>RunAsync</c>
    /// override with <c>[WorkflowRun]</c> and delegate to this method.
    /// </summary>
    protected virtual async Task RunAsync(DurableChatWorkflowInput input)
    {
        Input = input;

        // Restore history carried forward from a previous run (continue-as-new).
        if (input.CarriedHistory is { Count: > 0 })
        {
            if (_history.Capacity < input.CarriedHistory.Count)
                _history.Capacity = input.CarriedHistory.Count;
            _history.AddRange(input.CarriedHistory);
        }

        // Opt-in: upsert search attributes only when explicitly requested.
        // Guards against failure on servers where the attributes are not pre-registered.
        if (input.SearchAttributes is not null)
        {
            Workflow.UpsertTypedSearchAttributes(
                DurableSessionAttributes.SessionCreatedAt.ValueSet(Workflow.UtcNow),
                DurableSessionAttributes.TurnCount.ValueSet(_turnCount));
        }

        // Wait until shutdown or history grows large enough for continue-as-new.
        bool conditionMet = await Workflow.WaitConditionAsync(
            () => _shutdownRequested || (!_isProcessing && Workflow.ContinueAsNewSuggested),
            timeout: input.TimeToLive);

        if (!conditionMet)
        {
            // TTL elapsed — session complete.
            return;
        }

        if (Workflow.ContinueAsNewSuggested && !_shutdownRequested)
        {
            var carriedHistory = _history.ToList();
            var carriedInput = new DurableChatWorkflowInput
            {
                TimeToLive = input.TimeToLive,
                CarriedHistory = carriedHistory,
                ActivityTimeout = input.ActivityTimeout,
                HeartbeatTimeout = input.HeartbeatTimeout,
                ApprovalTimeout = input.ApprovalTimeout,
                SearchAttributes = input.SearchAttributes,
            };
            throw CreateContinueAsNewException(carriedInput);
        }
    }

    /// <summary>
    /// Executes a single chat turn: serializes concurrent turns, appends user messages,
    /// dispatches the LLM call via <see cref="ExecuteTurnAsync"/>, appends the response
    /// messages, and updates the turn count search attribute if opted in.
    /// </summary>
    protected async Task<TOutput> RunTurnAsync(
        IEnumerable<ChatMessage> userMessages,
        ChatOptions? options,
        string? conversationId,
        string? clientKey = null)
    {
        // Serialize: wait for any in-progress turn to finish.
        await Workflow.WaitConditionAsync(() => !_isProcessing);
        _isProcessing = true;

        try
        {
            // Append user messages to history.
            foreach (var msg in userMessages)
            {
                _history.Add(msg);
            }

            _turnCount++;

            var activityInput = new DurableChatInput
            {
                Messages = [.. _history],
                Options = options,
                ConversationId = conversationId ?? Workflow.Info.WorkflowId,
                TurnNumber = _turnCount,
                ClientKey = clientKey,
            };

            var activityOptions = new ActivityOptions
            {
                StartToCloseTimeout = Input!.ActivityTimeout,
                HeartbeatTimeout = Input!.HeartbeatTimeout,
            };

            var output = await ExecuteTurnAsync(activityOptions, activityInput);

            // Append response messages to history.
            foreach (var msg in GetHistoryMessages(output))
            {
                _history.Add(msg);
            }

            // Update turn count search attribute if opt-in was requested.
            if (Input!.SearchAttributes is not null)
            {
                Workflow.UpsertTypedSearchAttributes(
                    DurableSessionAttributes.TurnCount.ValueSet(_turnCount));
            }

            return output;
        }
        finally
        {
            _isProcessing = false;
        }
    }

    // ── Queries ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the current conversation history.
    /// </summary>
    [WorkflowQuery("GetHistory")]
    public IReadOnlyList<ChatMessage> GetHistory() => _history;

    // ── Signals ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Requests graceful shutdown of this session.
    /// </summary>
    [WorkflowSignal("Shutdown")]
    public Task RequestShutdownAsync()
    {
        _shutdownRequested = true;
        return Task.CompletedTask;
    }

    // ── HITL: Tool Approval ──────────────────────────────────────────────────

    /// <summary>
    /// Validates a tool approval request before it enters workflow history.
    /// </summary>
    [WorkflowUpdateValidator(nameof(RequestApprovalAsync))]
    public void ValidateRequestApproval(DurableApprovalRequest request) =>
        _approval.ValidateRequestApproval(request);

    /// <summary>
    /// Blocks the workflow until a human submits a decision via <see cref="SubmitApprovalAsync"/>.
    /// Returns the decision as a <see cref="DurableApprovalDecision"/>.
    /// </summary>
    [WorkflowUpdate("RequestApproval")]
    public Task<DurableApprovalDecision> RequestApprovalAsync(DurableApprovalRequest request) =>
        _approval.RequestApprovalAsync(
            request,
            approvalTimeout: Input?.ApprovalTimeout ?? TimeSpan.FromDays(7),
            onRequested: req => Workflow.Logger.LogInformation(
                "[{ConversationId}] Approval requested (RequestId: {RequestId}, Description: {Description})",
                Workflow.Info.WorkflowId, req.RequestId, req.Description ?? req.RequestId),
            onResolved: d => Workflow.Logger.LogInformation(
                "[{ConversationId}] Approval resolved (RequestId: {RequestId}, Approved: {Approved})",
                Workflow.Info.WorkflowId, d.RequestId, d.Approved));

    /// <summary>
    /// Validates a submitted approval decision.
    /// </summary>
    [WorkflowUpdateValidator(nameof(SubmitApprovalAsync))]
    public void ValidateSubmitApproval(DurableApprovalDecision decision) =>
        _approval.ValidateSubmitApproval(decision);

    /// <summary>
    /// Submits the human decision for the pending approval request.
    /// Unblocks <see cref="RequestApprovalAsync"/>.
    /// </summary>
    [WorkflowUpdate("SubmitApproval")]
    public Task<DurableApprovalDecision> SubmitApprovalAsync(DurableApprovalDecision decision) =>
        Task.FromResult(_approval.SubmitApprovalAsync(decision));

    /// <summary>
    /// Returns the currently pending approval request, or null if none.
    /// </summary>
    [WorkflowQuery("GetPendingApproval")]
    public DurableApprovalRequest? GetPendingApproval() => _approval.GetPendingApproval();
}
