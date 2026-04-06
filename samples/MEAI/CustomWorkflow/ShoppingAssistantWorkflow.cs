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
    public Task<ShoppingTurnOutput> ShopAsync(DurableChatInput input) =>
        RunTurnAsync(input.Messages, input.Options, input.ConversationId);

    protected override IEnumerable<ChatMessage> GetHistoryMessages(ShoppingTurnOutput output) =>
        output.Response.Messages;

    protected override Task<ShoppingTurnOutput> ExecuteTurnAsync(
        ActivityOptions activityOptions,
        DurableChatInput activityInput) =>
        Workflow.ExecuteActivityAsync(
            (ShoppingActivities a) => a.GetShoppingResponseAsync(activityInput),
            activityOptions);

    protected override ContinueAsNewException CreateContinueAsNewException(
        DurableChatWorkflowInput input) =>
        Workflow.CreateContinueAsNewException(
            (ShoppingAssistantWorkflow wf) => wf.RunAsync(input));
}
