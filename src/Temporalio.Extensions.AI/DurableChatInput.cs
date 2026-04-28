using Microsoft.Extensions.AI;

namespace Temporalio.Extensions.AI;

/// <summary>
/// Serializable input for the durable chat activity.
/// Carries the messages and options needed to invoke the inner <see cref="IChatClient"/>.
/// </summary>
public sealed class DurableChatInput
{
    /// <summary>
    /// The chat messages to send to the LLM.
    /// </summary>
    public required IList<ChatMessage> Messages { get; init; }

    /// <summary>
    /// Optional chat options. Non-serializable fields (e.g. RawRepresentationFactory)
    /// are not preserved across the activity boundary.
    /// </summary>
    public ChatOptions? Options { get; init; }

    /// <summary>
    /// The conversation/session identifier for correlation.
    /// </summary>
    public string? ConversationId { get; init; }

    /// <summary>
    /// The turn number within the conversation (for diagnostics).
    /// </summary>
    public int TurnNumber { get; init; }

    /// <summary>
    /// Optional DI service key used to resolve <see cref="IChatClient"/> on the worker.
    /// When null, the unkeyed <see cref="IChatClient"/> registration is used.
    /// </summary>
    public string? ClientKey { get; init; }
}
