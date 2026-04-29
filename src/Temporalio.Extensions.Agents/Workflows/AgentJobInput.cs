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
    /// Gets the <c>StartToCloseTimeout</c> for the agent activity invocation.
    /// When <see langword="null"/>, the workflow falls back to a 30-minute default.
    /// </summary>
    public TimeSpan? ActivityStartToCloseTimeout { get; init; }

    /// <summary>
    /// Gets the <c>HeartbeatTimeout</c> for the agent activity invocation.
    /// When <see langword="null"/>, the workflow falls back to a 5-minute default.
    /// </summary>
    public TimeSpan? ActivityHeartbeatTimeout { get; init; }

    /// <summary>
    /// Gets the retry policy applied to the agent activity invocation.
    /// When <see langword="null"/>, Temporal SDK defaults apply (unbounded retries).
    /// </summary>
    public RetryPolicy? RetryPolicy { get; init; }
}
