using System.Collections.Concurrent;
using Temporalio.Extensions.AI;
using Temporalio.Extensions.Agents.HistoryStore;

namespace ExternalHistoryStore;

/// <summary>
/// Reference <see cref="IAgentHistoryStore"/> implementation backed by an in-memory
/// <see cref="ConcurrentDictionary{TKey, TValue}"/>. Demonstrates the workflow-level
/// (Layer 1) durability + PII-out-of-Temporal abstraction without taking a database
/// dependency for the sample.
///
/// <para><b>Reduction strategy</b> — <see cref="LoadAsync"/> applies a recent-N window
/// (default <c>maxRecentEntries = 4</c>, i.e. 2 turns). The agent only ever sees the
/// most recent N entries while the full audit trail is retained internally. This is the
/// documented workaround for the in-process <c>HistoryReducer</c> not applying to
/// external storage — the reducer delegate is <c>[JsonIgnore]</c> and cannot cross the
/// activity boundary, so reduction lives in user code at the storage layer.</para>
///
/// <para>Three reduction patterns are interchangeable here:</para>
/// <list type="bullet">
///   <item><description><b>Recent-N truncation</b> (this sample): cheap, deterministic, loses earliest context.</description></item>
///   <item><description><b>Summarize-and-keep-recent</b>: invoke an LLM in <see cref="LoadAsync"/> to compress
///     older turns into a single summary entry, then prepend it to the recent window.</description></item>
///   <item><description><b>At-rest reduction in <see cref="ReplaceAsync"/></b>: collapse the on-disk view
///     when continue-as-new fires so subsequent loads start from the reduced set.</description></item>
/// </list>
/// </summary>
public sealed class InMemoryHistoryStore : IAgentHistoryStore
{
    private readonly ConcurrentDictionary<string, List<DurableSessionEntry>> _full = new();
    private readonly int _maxRecentEntries;
    private long _loadCalls;
    private long _reductionEvents;

    /// <summary>
    /// Creates a new in-memory store. <paramref name="maxRecentEntries"/> bounds the
    /// number of entries returned to the agent on each <see cref="LoadAsync"/> call.
    /// Default is <c>4</c> (≈ 2 turns).
    /// </summary>
    public InMemoryHistoryStore(int maxRecentEntries = 4)
    {
        if (maxRecentEntries <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxRecentEntries),
                "maxRecentEntries must be positive.");
        }

        _maxRecentEntries = maxRecentEntries;
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<DurableSessionEntry>> LoadAsync(
        string sessionId, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _loadCalls);

        if (!_full.TryGetValue(sessionId, out var all))
        {
            return Task.FromResult<IReadOnlyList<DurableSessionEntry>>([]);
        }

        lock (all)
        {
            if (all.Count <= _maxRecentEntries)
            {
                return Task.FromResult<IReadOnlyList<DurableSessionEntry>>(all.ToArray());
            }

            Interlocked.Increment(ref _reductionEvents);
            return Task.FromResult<IReadOnlyList<DurableSessionEntry>>(
                all.Skip(all.Count - _maxRecentEntries).ToArray());
        }
    }

    /// <inheritdoc/>
    public Task AppendAsync(
        string sessionId,
        IReadOnlyList<DurableSessionEntry> entries,
        CancellationToken cancellationToken = default)
    {
        var list = _full.GetOrAdd(sessionId, _ => []);
        lock (list)
        {
            list.AddRange(entries);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task ReplaceAsync(
        string sessionId,
        IReadOnlyList<DurableSessionEntry> reducedEntries,
        CancellationToken cancellationToken = default)
    {
        var list = _full.GetOrAdd(sessionId, _ => []);
        lock (list)
        {
            list.Clear();
            list.AddRange(reducedEntries);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Sample-only: returns the complete audit trail for the given session, untouched
    /// by the recent-N reduction window. Useful for compliance reporting and for the
    /// demo driver to print "store has 12 entries; agent saw 4".
    /// </summary>
    public IReadOnlyList<DurableSessionEntry> SnapshotFull(string sessionId) =>
        _full.TryGetValue(sessionId, out var all)
            ? all.ToArray()
            : [];

    /// <summary>Count of <see cref="LoadAsync"/> invocations across all sessions.</summary>
    public long LoadCalls => Interlocked.Read(ref _loadCalls);

    /// <summary>
    /// Count of times <see cref="LoadAsync"/> actually trimmed the returned set
    /// (i.e. full history exceeded the recent-N window). Useful for the demo printout.
    /// </summary>
    public long ReductionEvents => Interlocked.Read(ref _reductionEvents);
}
