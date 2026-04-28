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

    /// <summary>
    /// Gets or sets whether <c>TurnCount</c> and <c>SessionCreatedAt</c> typed search attributes
    /// are upserted on the workflow. Defaults to <see langword="false"/>.
    /// </summary>
    /// <remarks>
    /// Requires <c>TurnCount</c> (Long) and <c>SessionCreatedAt</c> (Datetime) to be
    /// pre-registered on the Temporal server before the first workflow start.
    /// Use the Temporal CLI: <c>temporal operator search-attribute create</c>.
    /// </remarks>
    public bool EnableSearchAttributes { get; set; }

    /// <summary>
    /// Default keyed DI service key used to resolve <see cref="IChatClient"/>.
    /// When null (default), the unkeyed registration is used.
    /// Per-call overrides via <see cref="TemporalChatOptionsExtensions.WithChatClientKey"/> take precedence.
    /// </summary>
    public string? DefaultChatClientKey { get; set; }

    /// <summary>
    /// Gets or sets whether to register the default <see cref="DurableChatWorkflow"/> and
    /// <see cref="DurableChatSessionClient"/>. Defaults to <see langword="true"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Set to <see langword="false"/> when using a custom workflow derived from
    /// <see cref="DurableChatWorkflowBase{TOutput}"/>. The workflow and session client are
    /// still required for most applications, so only disable if you are providing your own
    /// workflow implementation and do not need <see cref="DurableChatSessionClient"/>.
    /// </para>
    /// <para>
    /// Disabling this only skips the default workflow and session client registration.
    /// All supporting infrastructure (options, DataConverter, activities, embeddings) is
    /// still registered regardless of this setting.
    /// </para>
    /// </remarks>
    public bool RegisterDefaultWorkflow { get; set; } = true;

    internal void Validate()
    {
        if (string.IsNullOrEmpty(TaskQueue))
        {
            throw new InvalidOperationException(
                $"{nameof(TaskQueue)} must be set in {nameof(DurableExecutionOptions)}.");
        }
    }
}
