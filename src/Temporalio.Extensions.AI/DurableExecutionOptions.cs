using Temporalio.Common;

namespace Temporalio.Extensions.AI;

/// <summary>
/// Configuration options for durable AI execution via Temporal.
/// </summary>
public sealed class DurableExecutionOptions
{
    /// <summary>
    /// Gets or sets the Temporal task queue for chat activities.
    /// Must be set before use.
    /// </summary>
    public string? TaskQueue { get; set; }

    /// <summary>
    /// Gets or sets the activity start-to-close timeout for LLM calls. Defaults to 5 minutes.
    /// </summary>
    public TimeSpan ActivityTimeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Gets or sets the Temporal retry policy for activities. When null, Temporal defaults apply.
    /// </summary>
    public RetryPolicy? RetryPolicy { get; set; }

    /// <summary>
    /// Gets or sets the workflow ID prefix for chat sessions. Defaults to "chat-".
    /// </summary>
    public string WorkflowIdPrefix { get; set; } = "chat-";

    /// <summary>
    /// Gets or sets the session time-to-live. Defaults to 14 days.
    /// </summary>
    public TimeSpan SessionTimeToLive { get; set; } = TimeSpan.FromDays(14);

    /// <summary>
    /// Gets or sets whether session management (workflow-backed conversations) is enabled.
    /// When false, the middleware only wraps individual calls as activities.
    /// Defaults to false.
    /// </summary>
    public bool EnableSessionManagement { get; set; }

    /// <summary>
    /// Gets or sets the activity heartbeat timeout. Defaults to 2 minutes.
    /// </summary>
    public TimeSpan HeartbeatTimeout { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Gets or sets the maximum time to wait for a human to respond to a tool approval request.
    /// Defaults to 7 days.
    /// </summary>
    public TimeSpan ApprovalTimeout { get; set; } = TimeSpan.FromDays(7);

    internal void Validate()
    {
        if (string.IsNullOrEmpty(TaskQueue))
        {
            throw new InvalidOperationException(
                $"{nameof(TaskQueue)} must be set in {nameof(DurableExecutionOptions)}.");
        }
    }
}
