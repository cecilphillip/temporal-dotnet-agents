using Microsoft.Extensions.AI;

namespace Temporalio.Extensions.AI;

/// <summary>
/// Input for the <see cref="DurableChatWorkflow"/>.
/// </summary>
internal sealed class DurableChatWorkflowInput
{
    /// <summary>
    /// The session time-to-live. The workflow completes when idle for this duration.
    /// </summary>
    public TimeSpan TimeToLive { get; init; } = TimeSpan.FromDays(14);

    /// <summary>
    /// Conversation history carried forward from a previous run (continue-as-new).
    /// </summary>
    public List<ChatMessage>? CarriedHistory { get; init; }

    /// <summary>
    /// Activity timeout for LLM calls.
    /// </summary>
    public TimeSpan ActivityTimeout { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Heartbeat timeout for LLM call activities.
    /// </summary>
    public TimeSpan HeartbeatTimeout { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Maximum time to wait for a human to respond to a tool approval request.
    /// Defaults to 7 days.
    /// </summary>
    public TimeSpan ApprovalTimeout { get; init; } = TimeSpan.FromDays(7);
}
