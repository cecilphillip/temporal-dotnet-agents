using System.Text.Json;
using System.Text.Json.Serialization;
using Temporalio.Extensions.Agents.State;

namespace Temporalio.Extensions.Agents;

/// <summary>
/// Input passed to <see cref="AgentWorkflow"/> when starting a new run.
/// </summary>
internal sealed class AgentWorkflowInput
{
    /// <summary>Gets the name of the agent that this workflow manages.</summary>
    public required string AgentName { get; init; }

    /// <summary>Gets the agent-specific time-to-live for the workflow. Overrides the default.</summary>
    public TimeSpan? TimeToLive { get; init; }

    /// <summary>Gets the task queue on which <see cref="AgentActivities"/> are registered.</summary>
    public required string TaskQueue { get; init; }

    /// <summary>
    /// Gets conversation history carried forward from a previous run (for continue-as-new scenarios).
    /// </summary>
    public IReadOnlyList<TemporalAgentStateEntry> CarriedHistory { get; init; } = [];

    /// <summary>
    /// Gets the serialized <see cref="AgentSessionStateBag"/> carried forward from a
    /// previous run (for continue-as-new scenarios). Allows AIContextProvider state
    /// (e.g. Mem0 thread IDs) to survive workflow continuation.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? CarriedStateBag { get; init; }

    /// <summary>
    /// Gets the <c>StartToCloseTimeout</c> for agent activity invocations.
    /// When <see langword="null"/>, the workflow falls back to a 30-minute default.
    /// </summary>
    public TimeSpan? ActivityStartToCloseTimeout { get; init; }

    /// <summary>
    /// Gets the <c>HeartbeatTimeout</c> for agent activity invocations.
    /// When <see langword="null"/>, the workflow falls back to a 5-minute default.
    /// </summary>
    public TimeSpan? ActivityHeartbeatTimeout { get; init; }
}
