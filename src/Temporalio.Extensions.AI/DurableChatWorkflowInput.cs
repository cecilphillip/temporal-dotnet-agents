using System.Text.Json.Serialization;

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
    public List<DurableSessionEntry>? CarriedHistory { get; init; }

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
    /// Maximum number of <see cref="DurableSessionEntry"/> instances retained in the
    /// conversation history before a continue-as-new transition is triggered. Defaults to 1000.
    /// </summary>
    public int MaxEntryCount { get; init; } = 1000;

    /// <summary>
    /// Optional reducer applied to conversation history before a continue-as-new transition.
    /// Not serialized — the session client re-supplies this on each workflow start.
    /// </summary>
    [JsonIgnore]
    public Func<IList<DurableSessionEntry>, IList<DurableSessionEntry>>? HistoryReducer { get; init; }

    /// <summary>
    /// The UTC timestamp at which the session was originally created.
    /// Populated on the first run and carried forward through continue-as-new transitions
    /// so that <c>SessionCreatedAt</c> always reflects the true session start time.
    /// </summary>
    public DateTimeOffset? OriginalCreatedAt { get; init; }
}
