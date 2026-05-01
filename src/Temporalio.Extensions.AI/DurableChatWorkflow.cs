using Microsoft.Extensions.AI;
using Temporalio.Workflows;

namespace Temporalio.Extensions.AI;

/// <summary>
/// Temporal workflow that manages a durable conversation session.
/// Conversation history is persisted in workflow state as a list of
/// <see cref="DurableSessionEntry"/> instances. Chat turns are executed via
/// <c>[WorkflowUpdate]</c> for synchronous request/response semantics.
/// Includes HITL approval support via <c>[WorkflowUpdate]</c> for tool approval gates.
/// </summary>
[Workflow("Temporalio.Extensions.AI.DurableChatWorkflow")]
internal sealed class DurableChatWorkflow : DurableChatWorkflowBase<ChatResponse>
{
    // Per-turn metadata captured by ChatAsync before the base session loop dispatches
    // the activity. Read inside ExecuteTurnAsync to populate the activity input.
    private string? _lastClientKey;
    private string? _lastConversationId;

    [WorkflowRun]
    public new Task RunAsync(DurableChatWorkflowInput input) => base.RunAsync(input);

    /// <summary>
    /// Validates a chat request before it enters workflow history.
    /// </summary>
    [WorkflowUpdateValidator(nameof(ChatAsync))]
    public void ValidateChat(DurableChatInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (IsShutdownRequested)
            throw new InvalidOperationException("Session has been shut down.");
        if (input.Messages is null || input.Messages.Count == 0)
            throw new ArgumentException("At least one message is required.");
    }

    /// <summary>
    /// Executes a chat turn: appends user messages, calls the LLM via activity,
    /// appends response, and returns the response entry.
    /// </summary>
    [WorkflowUpdate("Chat")]
    public async Task<DurableSessionResponse> ChatAsync(DurableChatInput input)
    {
        // Capture per-turn metadata for ExecuteTurnAsync. ClientKey and ConversationId
        // are carried on DurableChatInput (caller-supplied / session-client-supplied) but
        // not embedded in DurableSessionRequest, so we stash them on private fields
        // until ExecuteTurnAsync runs.
        _lastClientKey = input.ClientKey;
        _lastConversationId = input.ConversationId;

        // Build the request entry for this turn — the factory auto-generates the
        // correlation ID via Workflow.NewGuid() (deterministic, replay-safe) when the
        // caller did not supply one.
        var messages = input.Messages as IReadOnlyList<ChatMessage> ?? input.Messages.ToList();
        var requestEntry = DurableSessionRequest.FromMessages(messages, input.CorrelationId);

        var (_, responseEntry) = await RunTurnAsync(requestEntry, input.Options);
        return responseEntry;
    }

    /// <summary>
    /// Wraps the activity's <see cref="ChatResponse"/> into a <see cref="DurableSessionResponse"/>
    /// for history storage.
    /// </summary>
    protected override DurableSessionResponse BuildResponseEntry(
        string correlationId,
        ChatResponse output,
        DateTimeOffset createdAt) =>
        DurableSessionResponse.FromChatResponse(correlationId, output, createdAt);

    protected override Task<ChatResponse> ExecuteTurnAsync(
        ActivityOptions activityOptions,
        DurableSessionRequest requestEntry,
        ChatOptions? chatOptions)
    {
        // Flatten the entire history (including the just-appended request entry) into
        // a single message list so the LLM sees the full conversation each turn.
        var activityMessages = History
            .SelectMany(e => e.Messages)
            .ToList();

        var activityInput = new DurableChatInput
        {
            Messages = activityMessages,
            Options = chatOptions,
            ConversationId = _lastConversationId ?? Workflow.Info.WorkflowId,
            TurnNumber = CurrentTurnNumber,
            ClientKey = _lastClientKey,
            CorrelationId = requestEntry.CorrelationId,
        };
        return Workflow.ExecuteActivityAsync(
            (DurableChatActivities a) => a.GetResponseAsync(activityInput),
            activityOptions);
    }

    protected override ContinueAsNewException CreateContinueAsNewException(
        DurableChatWorkflowInput input) =>
        Workflow.CreateContinueAsNewException(
            (DurableChatWorkflow wf) => wf.RunAsync(input));
}
