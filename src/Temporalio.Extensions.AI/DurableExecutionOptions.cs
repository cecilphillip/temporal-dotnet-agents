using Microsoft.Extensions.AI;
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
    /// <remarks>
    /// Reserved for future use. This property currently has no effect — the session client and
    /// default workflow registration are controlled by <see cref="RegisterDefaultWorkflow"/>, not
    /// by this flag. To suppress the default workflow and session client, set
    /// <see cref="RegisterDefaultWorkflow"/> to <see langword="false"/> instead.
    /// </remarks>
    public bool EnableSessionManagement { get; set; }

    /// <summary>
    /// Gets or sets the activity heartbeat timeout. Defaults to 2 minutes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The library does not currently send periodic heartbeats during long-running LLM or
    /// embedding inference. Set this value to be safely longer than the worst-case LLM response
    /// time for your workload, or leave it at its default to rely on the
    /// <see cref="ActivityTimeout"/> start-to-close deadline alone.
    /// </para>
    /// <para>
    /// Setting a heartbeat timeout that is shorter than the LLM call latency will cause the
    /// activity to be force-failed mid-execution by the Temporal server, even if the LLM
    /// eventually responds successfully.
    /// </para>
    /// <para>
    /// Per-request overrides are available via
    /// <see cref="TemporalChatOptionsExtensions.WithHeartbeatTimeout"/>.
    /// </para>
    /// </remarks>
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
    /// Gets or sets a reducer applied to conversation history before a continue-as-new transition.
    /// When null (default), the full history is carried forward.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Use this to trim or summarize history when the workflow is about to continue-as-new,
    /// preventing the carried history from growing unbounded across runs.
    /// </para>
    /// <para>
    /// <b>Workflow determinism:</b> the reducer runs inside the workflow task scheduler.
    /// <see cref="IChatReducer.ReduceAsync"/> must complete synchronously — do not perform
    /// async I/O, call LLM APIs, or use <c>Task.Delay</c>. Return <c>Task.FromResult(...)</c>
    /// from your implementation. To adapt an existing lambda, use
    /// <c>new FuncChatReducer(msgs => ...)</c>.
    /// </para>
    /// </remarks>
    public IChatReducer? HistoryReducer { get; set; }

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

    /// <summary>
    /// Gets or sets the maximum number of messages in the conversation history before a
    /// continue-as-new transition is triggered. Defaults to 1000.
    /// </summary>
    /// <remarks>
    /// The workflow also continues-as-new when the Temporal SDK's internal event history
    /// threshold is reached (<c>Workflow.ContinueAsNewSuggested</c>), whichever comes first.
    /// Reduce this value to limit payload size on long-running sessions.
    /// </remarks>
    public int MaxHistorySize { get; set; } = 1000;

    internal void Validate()
    {
        if (string.IsNullOrEmpty(TaskQueue))
        {
            throw new InvalidOperationException(
                $"{nameof(TaskQueue)} must be set in {nameof(DurableExecutionOptions)}.");
        }

        if (ActivityTimeout <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("ActivityTimeout must be a positive duration.");
        }

        if (HeartbeatTimeout <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("HeartbeatTimeout must be a positive duration.");
        }

        if (SessionTimeToLive <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("SessionTimeToLive must be a positive duration.");
        }

        if (ApprovalTimeout <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("ApprovalTimeout must be a positive duration.");
        }

        if (MaxHistorySize <= 0)
        {
            throw new InvalidOperationException(
                $"{nameof(MaxHistorySize)} must be greater than zero in {nameof(DurableExecutionOptions)}.");
        }
    }
}
