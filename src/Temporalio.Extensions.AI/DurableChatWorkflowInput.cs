using Microsoft.Extensions.AI;

namespace Temporalio.Extensions.AI;

/// <summary>
/// Input for the <see cref="DurableChatWorkflow"/>.
/// </summary>
public sealed class DurableChatWorkflowInput
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

    /// <summary>
    /// When non-null, enables upsert of <c>TurnCount</c> and <c>SessionCreatedAt</c>
    /// typed search attributes after workflow start and after each completed turn.
    /// Requires pre-registration of these attributes with the Temporal server.
    /// </summary>
    public DurableSessionAttributes? SearchAttributes { get; init; }

    /// <summary>
    /// Maximum number of messages in the conversation history before a continue-as-new
    /// transition is triggered. Defaults to 1000.
    /// </summary>
    public int MaxHistorySize { get; init; } = 1000;

    /// <summary>
    /// Optional strategy to reduce conversation history before a continue-as-new transition.
    /// When non-null, invoked with the current history before history is carried forward.
    /// Must be a pure, deterministic function — no async, no side effects, no wall-clock time.
    /// </summary>
    /// <remarks>
    /// Not serialized across the continue-as-new boundary (delegates are not JSON-serializable).
    /// The reducer is re-attached on each run by re-supplying the same options to
    /// <see cref="DurableChatSessionClient"/>. Callers using <see cref="DurableChatSessionClient"/>
    /// should set <see cref="DurableExecutionOptions.HistoryReducer"/> — it is threaded through
    /// to this property on each workflow start.
    /// </remarks>
    [System.Text.Json.Serialization.JsonIgnore]
    public Func<IList<ChatMessage>, IList<ChatMessage>>? HistoryReducer { get; init; }

    /// <summary>
    /// The UTC timestamp at which the session was originally created.
    /// Populated on the first run and carried forward through continue-as-new transitions
    /// so that <c>SessionCreatedAt</c> always reflects the true session start time.
    /// </summary>
    public DateTimeOffset? OriginalCreatedAt { get; init; }
}
