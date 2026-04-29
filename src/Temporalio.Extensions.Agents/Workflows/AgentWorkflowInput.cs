using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using Temporalio.Common;
using Temporalio.Extensions.Agents.State;

namespace Temporalio.Extensions.Agents.Workflows;

/// <summary>
/// Input passed to <see cref="AgentWorkflow"/> when starting a new run.
/// </summary>
internal sealed record AgentWorkflowInput
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

    /// <summary>
    /// Gets the maximum time to wait for a human to respond to an approval request.
    /// When <see langword="null"/>, the workflow falls back to a 7-day default.
    /// </summary>
    public TimeSpan? ApprovalTimeout { get; init; }

    /// <summary>
    /// Gets the retry policy applied to every agent activity invocation.
    /// When <see langword="null"/>, Temporal SDK defaults apply (unbounded retries).
    /// </summary>
    public RetryPolicy? RetryPolicy { get; init; }

    /// <summary>Default 1000. Workflow triggers continue-as-new when history reaches this count.</summary>
    public int MaxHistorySize { get; init; } = 1000;

    /// <summary>
    /// Not serialized. Re-supplied on each <c>StartWorkflowAsync</c> call.
    /// The library carries it in memory across continue-as-new on the same worker.
    /// </summary>
    [JsonIgnore]
    public Func<IList<TemporalAgentStateEntry>, IList<TemporalAgentStateEntry>>? HistoryReducer { get; init; }

    /// <summary>
    /// Null on the first run. Set to the original session start time on continue-as-new
    /// so <c>SessionCreatedAt</c> does not drift across transitions.
    /// </summary>
    public DateTimeOffset? OriginalCreatedAt { get; init; }
}
