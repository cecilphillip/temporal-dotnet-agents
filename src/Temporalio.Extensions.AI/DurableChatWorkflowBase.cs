using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Temporalio.Workflows;

namespace Temporalio.Extensions.AI;

/// <summary>
/// Abstract base class for durable chat workflows with typed turn output.
/// Provides the shared session loop, conversation history, HITL approval support,
/// continue-as-new handling, search attribute upserts, and serialized turn execution.
/// Concrete subclasses implement the abstract members to dispatch to their own
/// activities and to convert per-turn output into <see cref="DurableSessionResponse"/>
/// entries that get appended to history.
/// </summary>
/// <typeparam name="TOutput">The type returned from each completed chat turn.</typeparam>
public abstract class DurableChatWorkflowBase<TOutput>
{
    private List<DurableSessionEntry> _history = new(16);
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

    // ── Abstract / virtual hooks ────────────────────────────────────────────

    /// <summary>
    /// Builds the request entry that gets appended to history before the activity is dispatched.
    /// Subclasses can override to attach library-specific request metadata. The default
    /// implementation calls <see cref="DurableSessionRequest.FromMessages"/>.
    /// </summary>
    /// <param name="userMessages">The user-supplied messages for this turn.</param>
    /// <param name="correlationId">Per-turn correlation identifier.</param>
    /// <param name="createdAt">Workflow-time creation timestamp.</param>
    protected virtual DurableSessionRequest BuildRequestEntry(
        IReadOnlyList<ChatMessage> userMessages,
        string correlationId,
        DateTimeOffset createdAt) =>
        DurableSessionRequest.FromMessages(userMessages, correlationId, createdAt);

    /// <summary>
    /// Builds the response entry that gets appended to history after the activity completes.
    /// Subclasses convert their concrete <typeparamref name="TOutput"/> into a
    /// <see cref="DurableSessionResponse"/> (typically wrapping a <see cref="ChatResponse"/>).
    /// </summary>
    /// <param name="correlationId">Per-turn correlation identifier matching the request entry.</param>
    /// <param name="output">The activity's output for this turn.</param>
    /// <param name="createdAt">Workflow-time creation timestamp.</param>
    protected abstract DurableSessionResponse BuildResponseEntry(
        string correlationId,
        TOutput output,
        DateTimeOffset createdAt);

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

        // Capture the original creation time on the first run; carry it forward on CAN transitions.
        var sessionCreatedAt = input.OriginalCreatedAt ?? Workflow.UtcNow;

        // Opt-in: upsert search attributes only when explicitly requested.
        // Guards against failure on servers where the attributes are not pre-registered.
        if (input.SearchAttributes is not null)
        {
            Workflow.UpsertTypedSearchAttributes(
                DurableSessionAttributes.SessionCreatedAt.ValueSet(sessionCreatedAt),
                DurableSessionAttributes.TurnCount.ValueSet(_turnCount));
        }

        // Wait until shutdown, SDK-suggested CAN, or history has grown to MaxEntryCount.
        bool conditionMet = await Workflow.WaitConditionAsync(
            () => _shutdownRequested
                  || (!_isProcessing && Workflow.ContinueAsNewSuggested)
                  || (!_isProcessing && _history.Count >= input.MaxEntryCount),
            timeout: input.TimeToLive);

        if (!conditionMet)
        {
            // TTL elapsed — session complete.
            return;
        }

        if ((Workflow.ContinueAsNewSuggested || _history.Count >= input.MaxEntryCount) && !_shutdownRequested)
        {
            var carriedHistory = input.HistoryReducer is not null
                ? input.HistoryReducer(_history.ToList()).ToList()
                : _history.ToList();
            var carriedInput = new DurableChatWorkflowInput
            {
                TimeToLive = input.TimeToLive,
                CarriedHistory = carriedHistory,
                ActivityTimeout = input.ActivityTimeout,
                HeartbeatTimeout = input.HeartbeatTimeout,
                ApprovalTimeout = input.ApprovalTimeout,
                SearchAttributes = input.SearchAttributes,
                MaxEntryCount = input.MaxEntryCount,
                HistoryReducer = input.HistoryReducer,
                OriginalCreatedAt = sessionCreatedAt,
            };
            throw CreateContinueAsNewException(carriedInput);
        }
    }

    /// <summary>
    /// Executes a single chat turn: serializes concurrent turns, appends a request entry,
    /// dispatches the LLM call via <see cref="ExecuteTurnAsync"/>, appends a response entry,
    /// and updates the turn count search attribute if opted in.
    /// </summary>
    /// <returns>
    /// A tuple containing the activity's raw <typeparamref name="TOutput"/> and the
    /// <see cref="DurableSessionResponse"/> entry that was appended to history.
    /// Subclass update handlers typically return one or the other depending on the
    /// shape they want to expose to callers.
    /// </returns>
    protected async Task<(TOutput Output, DurableSessionResponse ResponseEntry)> RunTurnAsync(
        IEnumerable<ChatMessage> userMessages,
        ChatOptions? options,
        string? conversationId,
        string? clientKey = null,
        string? correlationId = null)
    {
        // Serialize: wait for any in-progress turn to finish.
        // Safety note: after WaitConditionAsync returns, the workflow is in a synchronous
        // execution window. Temporal's single-threaded scheduler cannot interleave another
        // update handler until the next await point. Setting _isProcessing = true immediately
        // after the condition is therefore atomic — no concurrent handler can observe
        // _isProcessing == false and enter this section between these two lines.
        await Workflow.WaitConditionAsync(() => !_isProcessing);
        _isProcessing = true;

        try
        {
            var userMessageList = userMessages as IReadOnlyList<ChatMessage> ?? userMessages.ToList();

            // Auto-generate correlation ID via Workflow.NewGuid() (deterministic, replay-safe)
            // when caller did not supply one.
            var effectiveCorrelationId = string.IsNullOrEmpty(correlationId)
                ? Workflow.NewGuid().ToString("N")
                : correlationId;

            var nowUtc = Workflow.UtcNow;

            // Build and append the request entry for this turn.
            var requestEntry = BuildRequestEntry(userMessageList, effectiveCorrelationId, nowUtc);
            _history.Add(requestEntry);

            _turnCount++;

            // Build the activity input with the flattened message list (request + prior history).
            var activityMessages = _history
                .SelectMany(e => e.Messages)
                .ToList();

            var activityInput = new DurableChatInput
            {
                Messages = activityMessages,
                Options = options,
                ConversationId = conversationId ?? Workflow.Info.WorkflowId,
                TurnNumber = _turnCount,
                ClientKey = clientKey,
                CorrelationId = effectiveCorrelationId,
            };

            var activityOptions = new ActivityOptions
            {
                StartToCloseTimeout = Input!.ActivityTimeout,
                HeartbeatTimeout = Input!.HeartbeatTimeout,
                Summary = DurableChatClient.BuildActivitySummary(options),
            };

            var output = await ExecuteTurnAsync(activityOptions, activityInput);

            // Build and append the response entry.
            var responseEntry = BuildResponseEntry(effectiveCorrelationId, output, Workflow.UtcNow);
            _history.Add(responseEntry);

            // Update turn count search attribute if opt-in was requested.
            if (Input!.SearchAttributes is not null)
            {
                Workflow.UpsertTypedSearchAttributes(
                    DurableSessionAttributes.TurnCount.ValueSet(_turnCount));
            }

            return (output, responseEntry);
        }
        finally
        {
            _isProcessing = false;
        }
    }

    // ── Queries ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the current conversation history as a list of <see cref="DurableSessionEntry"/>
    /// instances. Each turn appends a request entry followed by a response entry.
    /// </summary>
    [WorkflowQuery("GetHistory")]
    public IReadOnlyList<DurableSessionEntry> GetHistory() => _history;

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
            approvalTimeout: Input!.ApprovalTimeout,
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
