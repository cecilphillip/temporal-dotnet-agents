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

    /// <summary>
    /// The current turn count, available to subclass overrides for telemetry or
    /// activity-input fields. Updated inside <see cref="RunTurnAsync"/> after each turn
    /// completes, and initialized from carried history at the start of each run via
    /// <see cref="InitializeTurnCount"/>.
    /// </summary>
    protected int CurrentTurnNumber => _turnCount;

    /// <summary>
    /// The current conversation history, available to subclass overrides that need to
    /// pass the full flattened message log to their activity (e.g. so the LLM sees
    /// prior turns). The request entry for the current turn is appended to history
    /// <em>before</em> <see cref="ExecuteTurnAsync"/> is invoked, so the latest entry
    /// in this list is always the request that triggered the activity dispatch.
    /// </summary>
    protected IReadOnlyList<DurableSessionEntry> History => _history;

    // ── Abstract / virtual hooks ────────────────────────────────────────────

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
    /// Called by <see cref="RunTurnAsync"/> on each turn. The base no longer constructs
    /// a <see cref="DurableChatInput"/> on the subclass's behalf — subclasses own activity-input
    /// construction so they can include library-specific fields (e.g. MAF's
    /// <c>SerializedStateBag</c> / <c>AgentName</c>).
    /// </summary>
    /// <param name="activityOptions">
    /// Pre-built <see cref="ActivityOptions"/> with timeouts and summary populated from
    /// <see cref="DurableChatWorkflowInput"/> and <paramref name="chatOptions"/>.
    /// </param>
    /// <param name="requestEntry">
    /// The request entry that was just appended to history. Subclasses can extract
    /// <see cref="DurableSessionEntry.Messages"/>, <see cref="DurableSessionEntry.CorrelationId"/>,
    /// or library-specific fields from a derived entry type.
    /// </param>
    /// <param name="chatOptions">
    /// Optional chat options for this turn (e.g. model id, tools list). May be null when
    /// the subclass does not need MEAI-shaped options.
    /// </param>
    protected abstract Task<TOutput> ExecuteTurnAsync(
        ActivityOptions activityOptions,
        DurableSessionRequest requestEntry,
        ChatOptions? chatOptions);

    /// <summary>
    /// Creates the <see cref="ContinueAsNewException"/> typed to the concrete workflow class.
    /// Called by <see cref="RunAsync"/> when the workflow history grows large enough to
    /// trigger a continue-as-new transition.
    /// </summary>
    protected abstract ContinueAsNewException CreateContinueAsNewException(
        DurableChatWorkflowInput input);

    /// <summary>
    /// Computes the initial turn count when restoring carried history at workflow start.
    /// Default implementation re-derives the count by counting <see cref="DurableSessionResponse"/>
    /// entries in <paramref name="carriedHistory"/>, ensuring the <c>TurnCount</c> search
    /// attribute monotonically grows across continue-as-new boundaries instead of resetting.
    /// Subclasses can override for different semantics (e.g., per-CAN reset).
    /// </summary>
    /// <param name="carriedHistory">
    /// History entries carried forward from a prior run. Empty on the first run of a session.
    /// </param>
    protected virtual int InitializeTurnCount(IReadOnlyList<DurableSessionEntry> carriedHistory) =>
        carriedHistory.Count(e => e is DurableSessionResponse);

    /// <summary>
    /// Hook invoked after the base upserts the standard <c>TurnCount</c> and
    /// <c>SessionCreatedAt</c> search attributes. Subclasses override to upsert
    /// additional library-specific attributes (e.g. MAF's <c>AgentName</c>).
    /// Only called when <see cref="DurableChatWorkflowInput.EnableSearchAttributes"/>
    /// is <see langword="true"/>. Default implementation is a no-op.
    /// </summary>
    protected virtual void UpsertCustomSearchAttributes() { }

    /// <summary>
    /// Builds a copy of <paramref name="entry"/> with <see cref="DurableSessionEntry.Messages"/>
    /// replaced by an empty list, preserving correlation ID and creation timestamp. Default
    /// implementation handles the base library's <see cref="DurableSessionRequest"/> and
    /// <see cref="DurableSessionResponse"/> types. Subclasses with additional concrete entry
    /// types (e.g. MAF's <c>AgentSessionRequest</c> / <c>AgentSessionResponse</c>) override
    /// to preserve their library-specific fields.
    /// </summary>
    /// <param name="entry">The entry to strip.</param>
    /// <returns>A new entry of the same runtime type with empty <c>Messages</c>.</returns>
    protected virtual DurableSessionEntry StripMessagesFromEntry(DurableSessionEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        // Note: this base implementation only knows about the AI library's concrete types.
        // Library-specific subclasses must override and add their own type branches BEFORE
        // delegating to this base if they need to preserve subclass-only fields.
        return entry switch
        {
            DurableSessionResponse resp => new DurableSessionResponse
            {
                CorrelationId = resp.CorrelationId,
                CreatedAt = resp.CreatedAt,
                Messages = [],
                Usage = resp.Usage,
                AdditionalProperties = resp.AdditionalProperties,
            },
            DurableSessionRequest req => new DurableSessionRequest
            {
                CorrelationId = req.CorrelationId,
                CreatedAt = req.CreatedAt,
                Messages = [],
                AdditionalProperties = req.AdditionalProperties,
            },
            _ => entry,
        };
    }

    /// <summary>
    /// When <see langword="true"/>, response entries appended to the in-workflow history have
    /// their <see cref="DurableSessionEntry.Messages"/> replaced with an empty collection.
    /// Used by external-history modes (e.g. <c>TemporalAgentsOptions.UseExternalHistory</c>):
    /// the in-workflow history continues to drive turn-counting, search-attribute upserts,
    /// and the <c>MaxEntryCount</c>-triggered continue-as-new check, but the message payloads
    /// — which are the source of PII and Temporal event-log bloat — live only in the external
    /// store. Default implementation returns <see langword="false"/> (full messages retained).
    /// </summary>
    /// <remarks>
    /// Note that the base class only strips the <em>response</em> entry; the request entry is
    /// supplied by the subclass via <see cref="RunTurnAsync"/> and the subclass is responsible
    /// for stripping it before append if appropriate. The
    /// <c>GetHistoryAsync</c> query therefore returns metadata-only entries when this is on.
    /// </remarks>
    protected virtual bool ShouldStripMessagesFromHistoryEntry() => false;

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

        // Re-derive the turn count from carried history so search attributes and per-turn
        // diagnostics stay monotonic across continue-as-new transitions. Subclasses override
        // InitializeTurnCount for different semantics.
        _turnCount = InitializeTurnCount(_history);

        // Capture the original creation time on the first run; carry it forward on CAN transitions.
        var sessionCreatedAt = input.OriginalCreatedAt ?? Workflow.UtcNow;

        // Opt-in: upsert search attributes only when explicitly requested.
        // Guards against failure on servers where the attributes are not pre-registered.
        if (input.EnableSearchAttributes)
        {
            Workflow.UpsertTypedSearchAttributes(
                DurableSessionAttributes.SessionCreatedAt.ValueSet(sessionCreatedAt),
                DurableSessionAttributes.TurnCount.ValueSet(_turnCount));
            UpsertCustomSearchAttributes();
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
            // No-reducer path: pass _history directly — the workflow exits after this throw,
            // so there is no aliasing risk. Reducer path: pass _history as IList<T> (no copy
            // needed), then materialize the reducer's result once into a List<T>.
            var carriedHistory = input.HistoryReducer is not null
                ? input.HistoryReducer(_history).ToList()
                : _history;
            var carriedInput = new DurableChatWorkflowInput
            {
                TimeToLive = input.TimeToLive,
                CarriedHistory = carriedHistory,
                ActivityTimeout = input.ActivityTimeout,
                HeartbeatTimeout = input.HeartbeatTimeout,
                ApprovalTimeout = input.ApprovalTimeout,
                EnableSearchAttributes = input.EnableSearchAttributes,
                MaxEntryCount = input.MaxEntryCount,
                HistoryReducer = input.HistoryReducer,
                OriginalCreatedAt = sessionCreatedAt,
            };
            throw CreateContinueAsNewException(carriedInput);
        }
    }

    /// <summary>
    /// Executes a single chat turn: serializes concurrent turns, appends the supplied
    /// request entry, dispatches the LLM call via <see cref="ExecuteTurnAsync"/>, appends
    /// a response entry, and updates the turn count search attribute if opted in.
    /// </summary>
    /// <param name="requestEntry">
    /// The request entry to append to history before the activity is dispatched. Subclass
    /// <c>[WorkflowUpdate]</c> handlers construct this via library-specific factories
    /// (<see cref="DurableSessionRequest.FromMessages"/> for chat workflows;
    /// <c>AgentSessionRequest.FromRunRequest</c> for MAF agent workflows).
    /// </param>
    /// <param name="chatOptions">
    /// Optional chat options for the activity dispatch. May be null.
    /// </param>
    /// <param name="cancellationToken">Cancellation token for the workflow update.</param>
    /// <returns>
    /// A tuple containing the activity's raw <typeparamref name="TOutput"/> and the
    /// <see cref="DurableSessionResponse"/> entry that was appended to history.
    /// Subclass update handlers typically return one or the other depending on the
    /// shape they want to expose to callers.
    /// </returns>
    protected async Task<(TOutput Output, DurableSessionResponse ResponseEntry)> RunTurnAsync(
        DurableSessionRequest requestEntry,
        ChatOptions? chatOptions = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requestEntry);

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
            // Append the request entry for this turn. When external-history mode is on we
            // replace the messages with an empty list so the in-workflow history never holds
            // the raw user prompt — only metadata (CorrelationId, CreatedAt) for turn counting.
            var requestEntryToAppend = ShouldStripMessagesFromHistoryEntry()
                ? (DurableSessionRequest)StripMessagesFromEntry(requestEntry)
                : requestEntry;
            _history.Add(requestEntryToAppend);

            _turnCount++;

            var activityOptions = new ActivityOptions
            {
                StartToCloseTimeout = Input!.ActivityTimeout,
                HeartbeatTimeout = Input!.HeartbeatTimeout,
                Summary = DurableChatClient.BuildActivitySummary(chatOptions),
            };

            var output = await ExecuteTurnAsync(activityOptions, requestEntry, chatOptions);

            // Build the response entry, then optionally strip its message payload before
            // appending so external-history mode keeps the workflow history metadata-only.
            var responseEntry = BuildResponseEntry(requestEntry.CorrelationId, output, Workflow.UtcNow);
            var responseEntryToAppend = ShouldStripMessagesFromHistoryEntry()
                ? (DurableSessionResponse)StripMessagesFromEntry(responseEntry)
                : responseEntry;
            _history.Add(responseEntryToAppend);

            // Update turn count search attribute if opt-in was requested.
            if (Input!.EnableSearchAttributes)
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
