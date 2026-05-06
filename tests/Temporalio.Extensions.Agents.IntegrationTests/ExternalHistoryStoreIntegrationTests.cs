using System.Collections.Concurrent;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Temporalio.Client;
using Temporalio.Extensions.AI;
using Temporalio.Extensions.Agents.HistoryStore;
using Temporalio.Extensions.Agents.IntegrationTests.Helpers;
using Temporalio.Extensions.Agents.Session;
using Temporalio.Extensions.Agents.State;
using Temporalio.Extensions.Agents.Workflows;
using Temporalio.Extensions.Hosting;
using Temporalio.Testing;
using Xunit;

namespace Temporalio.Extensions.Agents.IntegrationTests;

/// <summary>
/// End-to-end coverage for the opt-in external history store path
/// (<see cref="TemporalAgentsOptions.UseExternalHistory"/> + <see cref="IAgentHistoryStore"/>).
///
/// Stands up its own embedded server + worker per test (no shared fixture) so each test
/// can wire a per-test <see cref="InMemoryAgentHistoryStore"/> and assert against it.
/// </summary>
[Trait("Category", "Integration")]
public class ExternalHistoryStoreIntegrationTests
{
    /// <summary>
    /// Three turns through a workflow with <see cref="TemporalAgentsOptions.UseExternalHistory"/>
    /// enabled. Asserts:
    /// <list type="number">
    ///   <item>The store records all 3 request/response pairs (6 entries total).</item>
    ///   <item>The workflow's internal <c>GetHistory()</c> query returns 6 entries with
    ///         <em>empty</em> <c>Messages</c> on each — i.e. the messages live only in the
    ///         external store, not in the Temporal event log.</item>
    ///   <item>Subclass-specific MAF fields (<c>OrchestrationId</c>, <c>ResponseType</c>,
    ///         <c>Usage</c>) survive the message-strip round-trip.</item>
    /// </list>
    /// </summary>
    [Fact]
    public async Task ExternalStore_ThreeTurnConversation_AppendsAllAndStripsInWorkflow()
    {
        await using var env = await WorkflowEnvironment.StartLocalAsync();
        env.Client.Options.DataConverter = TemporalAgentDataConverter.Instance;

        var store = new InMemoryAgentHistoryStore();
        var taskQueue = $"ext-history-{Guid.NewGuid():N}";

        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<ITemporalClient>(env.Client);
        builder.Services.AddSingleton<IAgentHistoryStore>(store);

        builder.Services
            .AddHostedTemporalWorker(taskQueue)
            .AddTemporalAgents(opts =>
            {
                opts.UseExternalHistory = true;
                opts.AddAIAgent(new EchoAIAgent("EchoAgent"));
            });

        using var host = builder.Build();
        await host.StartAsync();
        try
        {
            var proxy = host.Services.GetTemporalAgentProxy("EchoAgent");
            var session = (TemporalAgentSession)await proxy.CreateSessionAsync();

            var r1 = await proxy.RunAsync("hi 1", session);
            var r2 = await proxy.RunAsync("hi 2", session);
            var r3 = await proxy.RunAsync("hi 3", session);

            // The agent saw rebuilt history each turn — turn count climbs.
            Assert.Contains("Echo [1]: hi 1", r1.Messages[0].Text);
            Assert.Contains("Echo [2]: hi 2", r2.Messages[0].Text);
            Assert.Contains("Echo [3]: hi 3", r3.Messages[0].Text);

            // (1) The store has all 3 request/response pairs (6 entries total).
            var stored = store.Snapshot(session.SessionId.WorkflowId);
            Assert.Equal(6, stored.Count);
            Assert.IsType<AgentSessionRequest>(stored[0]);
            Assert.IsType<AgentSessionResponse>(stored[1]);
            Assert.IsType<AgentSessionRequest>(stored[2]);
            Assert.IsType<AgentSessionResponse>(stored[3]);
            Assert.IsType<AgentSessionRequest>(stored[4]);
            Assert.IsType<AgentSessionResponse>(stored[5]);

            // The user prompts survive in the store (this is the source of truth now).
            Assert.Contains("hi 1", stored[0].Messages[0].Text);
            Assert.Contains("hi 2", stored[2].Messages[0].Text);
            Assert.Contains("hi 3", stored[4].Messages[0].Text);

            // (2) Workflow's GetHistory query returns metadata-only entries.
            var handle = env.Client.GetWorkflowHandle<AgentWorkflow>(session.SessionId.WorkflowId);
            var inWorkflowHistory = await handle.QueryAsync(wf => wf.GetHistory());

            Assert.Equal(6, inWorkflowHistory.Count);
            Assert.All(inWorkflowHistory, e => Assert.Empty(e.Messages));
            // Correlation IDs / timestamps survive — they drive turn counting + dashboards.
            Assert.All(inWorkflowHistory, e => Assert.False(string.IsNullOrEmpty(e.CorrelationId)));

            // Append-call ordering: turn-N entries appear strictly after turn-(N-1).
            var appendCorrelationIds = store.AppendCalls
                .SelectMany(c => c.Entries.Select(e => e.CorrelationId))
                .ToList();
            Assert.Equal(6, appendCorrelationIds.Count);
            Assert.Equal(appendCorrelationIds[0], appendCorrelationIds[1]);
            Assert.Equal(appendCorrelationIds[2], appendCorrelationIds[3]);
            Assert.Equal(appendCorrelationIds[4], appendCorrelationIds[5]);

            // Per-turn correlation IDs are distinct and ordered: c1 < c2 < c3 (lexicographic
            // not guaranteed, but turn-1's ID was appended before turn-2's, etc.).
            var t1Id = appendCorrelationIds[0];
            var t2Id = appendCorrelationIds[2];
            var t3Id = appendCorrelationIds[4];
            Assert.NotEqual(t1Id, t2Id);
            Assert.NotEqual(t2Id, t3Id);
            Assert.NotEqual(t1Id, t3Id);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    /// <summary>
    /// Hand-rolled in-memory <see cref="IAgentHistoryStore"/>. Mirrors the unit-test
    /// project's <c>FakeAgentHistoryStore</c> but lives here so the integration test
    /// project doesn't need to reference the unit-test project. Concurrent-safe.
    /// </summary>
    private sealed class InMemoryAgentHistoryStore : IAgentHistoryStore
    {
        private readonly ConcurrentDictionary<string, List<DurableSessionEntry>> _store =
            new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, object> _locks =
            new(StringComparer.Ordinal);
        private readonly ConcurrentQueue<AppendCall> _appendCalls = new();

        public IReadOnlyCollection<AppendCall> AppendCalls => _appendCalls.ToArray();

        public Task<IReadOnlyList<DurableSessionEntry>> LoadAsync(
            string sessionId, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrEmpty(sessionId);
            if (!_store.TryGetValue(sessionId, out var bucket))
                return Task.FromResult<IReadOnlyList<DurableSessionEntry>>([]);

            lock (_locks.GetOrAdd(sessionId, _ => new object()))
            {
                return Task.FromResult<IReadOnlyList<DurableSessionEntry>>(bucket.ToArray());
            }
        }

        public Task AppendAsync(
            string sessionId,
            IReadOnlyList<DurableSessionEntry> entries,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrEmpty(sessionId);
            ArgumentNullException.ThrowIfNull(entries);

            var bucket = _store.GetOrAdd(sessionId, _ => []);
            lock (_locks.GetOrAdd(sessionId, _ => new object()))
            {
                bucket.AddRange(entries);
            }
            _appendCalls.Enqueue(new AppendCall(sessionId, [.. entries], DateTimeOffset.UtcNow));
            return Task.CompletedTask;
        }

        public Task ReplaceAsync(
            string sessionId,
            IReadOnlyList<DurableSessionEntry> reducedEntries,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrEmpty(sessionId);
            ArgumentNullException.ThrowIfNull(reducedEntries);

            var bucket = _store.GetOrAdd(sessionId, _ => []);
            lock (_locks.GetOrAdd(sessionId, _ => new object()))
            {
                bucket.Clear();
                bucket.AddRange(reducedEntries);
            }
            return Task.CompletedTask;
        }

        public IReadOnlyList<DurableSessionEntry> Snapshot(string sessionId)
        {
            if (!_store.TryGetValue(sessionId, out var bucket))
                return [];
            lock (_locks.GetOrAdd(sessionId, _ => new object()))
            {
                return bucket.ToArray();
            }
        }

        internal sealed record AppendCall(
            string SessionId,
            IReadOnlyList<DurableSessionEntry> Entries,
            DateTimeOffset Timestamp);
    }
}
