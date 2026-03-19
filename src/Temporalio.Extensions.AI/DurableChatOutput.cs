using Microsoft.Extensions.AI;

namespace Temporalio.Extensions.AI;

/// <summary>
/// Serializable output from the durable chat activity.
/// Wraps the <see cref="ChatResponse"/> returned by the inner <see cref="IChatClient"/>.
/// </summary>
internal sealed class DurableChatOutput
{
    /// <summary>
    /// The chat response from the LLM.
    /// </summary>
    public required ChatResponse Response { get; init; }
}
