using Temporalio.Extensions.AI;

namespace Temporalio.Extensions.Agents.HistoryStore;

/// <summary>
/// Pluggable external store for durable agent conversation history.
/// </summary>
/// <remarks>
/// <para>
/// When configured via <see cref="TemporalAgentsOptions.HistoryStore"/> (worker default) or
/// <c>DurableAgentBuilder.HistoryStore</c> (per-agent override), the workflow stops carrying
/// <see cref="DurableSessionEntry"/> instances inside the activity input on every dispatch.
/// Instead, the durable-agent step activity loads prior turns from this store via
/// <see cref="LoadAsync"/> and appends the current turn's request/response entries via
/// <see cref="AppendAsync"/>.
/// </para>
/// <para>
/// This is a Temporal-level coordination interface — it exists so that PII and large
/// conversation graphs do not enter the Temporal <c>ActivityScheduled</c> event log.
/// It is complementary to MAF's <c>AIContextProvider</c>/<c>ChatHistoryProvider</c>,
/// which run inside the activity (after the event is already written) and address a
/// different concern.
/// </para>
/// <para>
/// Implementations should be safe to call concurrently from multiple activities. Each
/// session's entries are keyed by <paramref name="sessionId"/> (the agent workflow ID).
/// </para>
/// </remarks>
public interface IAgentHistoryStore
{
    /// <summary>
    /// Loads all session entries for the given session, in append order
    /// (request entry, response entry, request entry, response entry, ...).
    /// </summary>
    /// <param name="sessionId">
    /// The agent workflow ID. Always passed from the activity context — never resolved
    /// from DI. Use <see cref="Session.TemporalAgentSessionId.WorkflowId"/>.
    /// </param>
    /// <param name="cancellationToken">Activity cancellation token.</param>
    /// <returns>The session entries, oldest first. Empty when the session has no prior turns.</returns>
    Task<IReadOnlyList<DurableSessionEntry>> LoadAsync(
        string sessionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Appends new entries for the given session. Called once per turn with the
    /// just-constructed request entry followed by the just-built response entry.
    /// </summary>
    /// <param name="sessionId">The agent workflow ID.</param>
    /// <param name="entries">
    /// Entries to append in the supplied order. Implementations must preserve order
    /// across calls so subsequent <see cref="LoadAsync"/> calls return the full history
    /// in the order it was appended.
    /// </param>
    /// <param name="cancellationToken">Activity cancellation token.</param>
    Task AppendAsync(
        string sessionId,
        IReadOnlyList<DurableSessionEntry> entries,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces all entries for the given session with a reduced set. Called at
    /// continue-as-new time when an external history store is configured AND a history reducer
    /// is set, so that the externally stored history stays aligned with the workflow's reduced view.
    /// </summary>
    /// <param name="sessionId">The agent workflow ID.</param>
    /// <param name="reducedEntries">The new full history for the session.</param>
    /// <param name="cancellationToken">Activity cancellation token.</param>
    Task ReplaceAsync(
        string sessionId,
        IReadOnlyList<DurableSessionEntry> reducedEntries,
        CancellationToken cancellationToken = default);
}
