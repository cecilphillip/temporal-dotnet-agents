using Temporalio.Common;

namespace Temporalio.Extensions.Agents.Workflows;

/// <summary>
/// Input passed to <see cref="AgentJobWorkflow"/> for a single, isolated agent run.
/// Unlike <see cref="AgentWorkflowInput"/>, there is no conversation history, StateBag,
/// TTL, or continue-as-new — the job runs once and completes.
/// </summary>
internal sealed record AgentJobInput
{
    /// <summary>Gets the name of the agent to invoke.</summary>
    public required string AgentName { get; init; }

    /// <summary>Gets the task queue on which <see cref="AgentActivities"/> are registered.</summary>
    public required string TaskQueue { get; init; }

    /// <summary>Gets the run request (messages + options) for this job.</summary>
    public required RunRequest Request { get; init; }

    /// <summary>
    /// Gets the activity timeout for the agent activity invocation.
    /// Defaults to 5 minutes.
    /// </summary>
    public TimeSpan ActivityTimeout { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Gets the heartbeat timeout for the agent activity invocation.
    /// Defaults to 2 minutes.
    /// </summary>
    public TimeSpan HeartbeatTimeout { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Gets the retry policy applied to the agent activity invocation.
    /// When <see langword="null"/>, Temporal SDK defaults apply (unbounded retries).
    /// </summary>
    public RetryPolicy? RetryPolicy { get; init; }
}
