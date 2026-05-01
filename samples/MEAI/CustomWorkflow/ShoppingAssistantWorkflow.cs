using Microsoft.Extensions.AI;
using Temporalio.Extensions.AI;
using Temporalio.Workflows;

namespace CustomWorkflow;

/// <summary>
/// Durable shopping assistant workflow.
/// Extends <see cref="DurableChatWorkflowBase{TOutput}"/> with <see cref="ShoppingTurnOutput"/>
/// so each Update returns both the assistant response and the list of cart actions
/// that occurred during the LLM tool calls in that turn.
/// </summary>
[Workflow("CustomWorkflow.ShoppingAssistant")]
public sealed class ShoppingAssistantWorkflow : DurableChatWorkflowBase<ShoppingTurnOutput>
{
    // Per-turn metadata captured by ShopAsync before the base session loop dispatches
    // the activity. Read inside ExecuteTurnAsync to populate the activity input.
    private string? _lastConversationId;

    [WorkflowRun]
    public new Task RunAsync(DurableChatWorkflowInput input) => base.RunAsync(input);

    /// <summary>
    /// Validates a shopping turn request before it enters workflow history.
    /// </summary>
    [WorkflowUpdateValidator(nameof(ShopAsync))]
    public void ValidateShop(DurableChatInput input)
    {
        if (IsShutdownRequested)
            throw new InvalidOperationException("Session has been shut down.");
        if (input?.Messages is null || input.Messages.Count == 0)
            throw new ArgumentException("At least one message is required.");
    }

    /// <summary>
    /// Executes a shopping assistant turn and returns the response along with
    /// any cart mutations that occurred during tool calls in this turn.
    /// </summary>
    [WorkflowUpdate("Shop")]
    public async Task<ShoppingTurnOutput> ShopAsync(DurableChatInput input)
    {
        // Capture per-turn metadata for ExecuteTurnAsync.
        _lastConversationId = input.ConversationId;

        // Build the request entry — factory auto-generates the correlation ID via
        // Workflow.NewGuid() when the caller did not supply one.
        var messages = input.Messages as IReadOnlyList<ChatMessage> ?? input.Messages.ToList();
        var requestEntry = DurableSessionRequest.FromMessages(messages, input.CorrelationId);

        var (output, _) = await RunTurnAsync(requestEntry, input.Options);
        return output;
    }

    /// <summary>
    /// Wraps the shopping turn output's <see cref="ChatResponse"/> into a
    /// <see cref="DurableSessionResponse"/> for history storage. Cart-action data is
    /// retained on the live <see cref="ShoppingTurnOutput"/> returned by <see cref="ShopAsync"/>;
    /// only the chat response is persisted in the durable session history.
    /// </summary>
    protected override DurableSessionResponse BuildResponseEntry(
        string correlationId,
        ShoppingTurnOutput output,
        DateTimeOffset createdAt) =>
        DurableSessionResponse.FromChatResponse(correlationId, output.Response, createdAt);

    protected override Task<ShoppingTurnOutput> ExecuteTurnAsync(
        ActivityOptions activityOptions,
        DurableSessionRequest requestEntry,
        ChatOptions? chatOptions)
    {
        // Flatten the entire history (including the just-appended request entry) into a
        // single message list so the LLM sees the full conversation each turn.
        var activityMessages = History
            .SelectMany(e => e.Messages)
            .ToList();

        var activityInput = new DurableChatInput
        {
            Messages = activityMessages,
            Options = chatOptions,
            ConversationId = _lastConversationId ?? Workflow.Info.WorkflowId,
            TurnNumber = CurrentTurnNumber,
            CorrelationId = requestEntry.CorrelationId,
        };
        return Workflow.ExecuteActivityAsync(
            (ShoppingActivities a) => a.GetShoppingResponseAsync(activityInput),
            activityOptions);
    }

    protected override ContinueAsNewException CreateContinueAsNewException(
        DurableChatWorkflowInput input) =>
        Workflow.CreateContinueAsNewException(
            (ShoppingAssistantWorkflow wf) => wf.RunAsync(input));
}
