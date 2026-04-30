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
        var (_, responseEntry) = await RunTurnAsync(
            input.Messages,
            input.Options,
            input.ConversationId,
            input.ClientKey,
            input.CorrelationId);
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
        DurableChatInput activityInput) =>
        Workflow.ExecuteActivityAsync(
            (DurableChatActivities a) => a.GetResponseAsync(activityInput),
            activityOptions);

    protected override ContinueAsNewException CreateContinueAsNewException(
        DurableChatWorkflowInput input) =>
        Workflow.CreateContinueAsNewException(
            (DurableChatWorkflow wf) => wf.RunAsync(input));
}
