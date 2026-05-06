using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Temporalio.Client;
using Temporalio.Extensions.AI;
using Temporalio.Extensions.Agents.HistoryStore;
using Temporalio.Extensions.Agents.IntegrationTests.Helpers;
using Temporalio.Extensions.Agents.Session;
using Temporalio.Extensions.Agents.Workflows;
using Temporalio.Extensions.Hosting;
using Temporalio.Testing;
using Xunit;

namespace Temporalio.Extensions.Agents.IntegrationTests;

/// <summary>
/// Production-level "canary" coverage that the opt-in external history store path
/// (<see cref="TemporalAgentsOptions.UseExternalHistory"/>) genuinely keeps conversation
/// history out of Temporal's <c>ActivityTaskScheduled</c> event payload — the actual PII /
/// O(n^2) event-log mitigation Feature 1 ships.
/// </summary>
/// <remarks>
/// <para>
/// A unit test (<see cref="Tests.HistoryStore.ExternalHistoryStoreTests"/>) already verifies
/// this at the CLR/payload-converter layer using <c>CapturingPayloadConverter</c>. That's
/// good but proves only that the configured converter omits the field. This integration
/// test reaches the load-bearing guarantee: Temporal's persisted event-log bytes — exactly
/// what an operator with <c>tctl workflow show</c> or a history-export pipeline would see —
/// contain no <c>ConversationHistory</c> key.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
public class ExternalHistoryStorePayloadTests
{
    /// <summary>
    /// End-to-end canary: with <see cref="TemporalAgentsOptions.UseExternalHistory"/>
    /// enabled, the raw bytes of the <c>ActivityTaskScheduled</c> event for the SECOND
    /// turn's <c>ExecuteAgent</c> activity contain neither <c>"ConversationHistory"</c>
    /// nor turn-1's user message — the load-bearing PII / O(n²) event-log mitigation.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The current turn's <see cref="RunRequest"/> always travels in the activity payload
    /// (the activity needs the new user prompt to call the LLM). What must NOT travel is
    /// the accumulating history of prior turns — without external storage that history
    /// grows quadratically in the event log. So we drive two turns and assert that turn-2's
    /// payload does not contain turn-1's marker.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ExternalStoreEnabled_TurnTwoPayload_DoesNotContainPriorTurnHistory()
    {
        await using var env = await WorkflowEnvironment.StartLocalAsync();
        env.Client.Options.DataConverter = TemporalAgentDataConverter.Instance;

        var store = new IntegrationInMemoryHistoryStore();
        var taskQueue = $"ext-history-payload-{Guid.NewGuid():N}";

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

            // Use distinctive markers per turn — the canary string we grep for in turn-2's
            // event payload is turn-1's marker (proves prior history is genuinely outside
            // the event log).
            const string turn1Marker = "TRINITY_TURN1_MARKER_ALPHA";
            const string turn2Marker = "TRINITY_TURN2_MARKER_BETA";

            await proxy.RunAsync(turn1Marker, session);
            var r2 = await proxy.RunAsync(turn2Marker, session);
            Assert.Contains("Echo [2]", r2.Messages[0].Text);

            // Walk the workflow's Temporal history events. Collect ALL ExecuteAgent
            // schedules — there should be one per turn.
            var handle = env.Client.GetWorkflowHandle(session.SessionId.WorkflowId);
            var executeAgentPayloads = new List<string>();
            await foreach (var ev in handle.FetchHistoryEventsAsync())
            {
                if (ev.ActivityTaskScheduledEventAttributes is { } a &&
                    a.ActivityType.Name == "Temporalio.Extensions.Agents.ExecuteAgent")
                {
                    Assert.NotNull(a.Input);
                    Assert.True(a.Input.Payloads_.Count >= 1, "ExecuteAgent activity input has no payloads");
                    executeAgentPayloads.Add(
                        Encoding.UTF8.GetString(a.Input.Payloads_[0].Data.ToByteArray()));
                }
            }

            Assert.Equal(2, executeAgentPayloads.Count);
            var turn1Json = executeAgentPayloads[0];
            var turn2Json = executeAgentPayloads[1];

            // Turn-1 payload contains turn-1's prompt (current request always travels) and
            // signals external-store mode. It does NOT contain a ConversationHistory key
            // and does NOT contain turn-2's marker.
            Assert.Contains(turn1Marker, turn1Json, StringComparison.Ordinal);
            Assert.DoesNotContain(turn2Marker, turn1Json, StringComparison.Ordinal);
            Assert.DoesNotContain("ConversationHistory", turn1Json, StringComparison.Ordinal);
            Assert.DoesNotContain("conversationHistory", turn1Json, StringComparison.Ordinal);
            Assert.Contains("UseExternalStore", turn1Json, StringComparison.OrdinalIgnoreCase);

            // Turn-2 payload — the load-bearing assertion. Turn-1's user message must NOT
            // be present in this event (the whole point of external storage).
            Assert.Contains(turn2Marker, turn2Json, StringComparison.Ordinal);
            Assert.DoesNotContain(turn1Marker, turn2Json, StringComparison.Ordinal);
            Assert.DoesNotContain("ConversationHistory", turn2Json, StringComparison.Ordinal);
            Assert.DoesNotContain("conversationHistory", turn2Json, StringComparison.Ordinal);
            Assert.Contains("UseExternalStore", turn2Json, StringComparison.OrdinalIgnoreCase);

            // Ground truth: BOTH turns' user messages live in the external store — they
            // weren't lost, just routed away from the event log.
            var stored = store.Snapshot(session.SessionId.WorkflowId);
            Assert.NotEmpty(stored);
            var allMessageText = string.Join(
                "\n",
                stored.SelectMany(e => e.Messages).Select(m => m.Text ?? string.Empty));
            Assert.Contains(turn1Marker, allMessageText, StringComparison.Ordinal);
            Assert.Contains(turn2Marker, allMessageText, StringComparison.Ordinal);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    /// <summary>
    /// Negative control: with <see cref="TemporalAgentsOptions.UseExternalHistory"/> off
    /// (default), turn-2's <c>ExecuteAgent</c> event payload DOES contain turn-1's user
    /// message via the inline <c>ConversationHistory</c>. Pins the "byte-identical to
    /// today" promise for non-opted-in callers and makes the canary above meaningful
    /// (i.e. the absence is genuinely caused by external-store mode, not by an unrelated
    /// payload-trimming optimization).
    /// </summary>
    [Fact]
    public async Task ExternalStoreDisabled_TurnTwoPayload_ContainsPriorTurnHistory()
    {
        await using var env = await WorkflowEnvironment.StartLocalAsync();
        env.Client.Options.DataConverter = TemporalAgentDataConverter.Instance;

        var taskQueue = $"ext-history-payload-off-{Guid.NewGuid():N}";

        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<ITemporalClient>(env.Client);

        builder.Services
            .AddHostedTemporalWorker(taskQueue)
            .AddTemporalAgents(opts =>
            {
                // UseExternalHistory intentionally NOT set — default false.
                opts.AddAIAgent(new EchoAIAgent("EchoAgent"));
            });

        using var host = builder.Build();
        await host.StartAsync();
        try
        {
            var proxy = host.Services.GetTemporalAgentProxy("EchoAgent");
            var session = (TemporalAgentSession)await proxy.CreateSessionAsync();

            const string turn1Marker = "TRINITY_INLINE_T1_99";
            const string turn2Marker = "TRINITY_INLINE_T2_99";

            await proxy.RunAsync(turn1Marker, session);
            await proxy.RunAsync(turn2Marker, session);

            var handle = env.Client.GetWorkflowHandle(session.SessionId.WorkflowId);
            var executeAgentPayloads = new List<string>();
            await foreach (var ev in handle.FetchHistoryEventsAsync())
            {
                if (ev.ActivityTaskScheduledEventAttributes is { } a &&
                    a.ActivityType.Name == "Temporalio.Extensions.Agents.ExecuteAgent")
                {
                    executeAgentPayloads.Add(
                        Encoding.UTF8.GetString(a.Input.Payloads_[0].Data.ToByteArray()));
                }
            }

            Assert.Equal(2, executeAgentPayloads.Count);
            var turn2Json = executeAgentPayloads[1];

            // With external storage off, turn-2's payload includes ConversationHistory and
            // turn-1's user message — exactly the behavior the opt-in feature mitigates.
            Assert.Contains("ConversationHistory", turn2Json, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(turn1Marker, turn2Json, StringComparison.Ordinal);
            Assert.Contains(turn2Marker, turn2Json, StringComparison.Ordinal);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    /// <summary>
    /// Minimal in-memory store for this test. Mirrors the pattern in
    /// <see cref="ExternalHistoryStoreIntegrationTests"/> — kept local so the integration
    /// test project doesn't reference the unit-test project's <c>FakeAgentHistoryStore</c>.
    /// </summary>
    private sealed class IntegrationInMemoryHistoryStore : IAgentHistoryStore
    {
        private readonly Dictionary<string, List<DurableSessionEntry>> _store = new(StringComparer.Ordinal);
        private readonly object _gate = new();

        public Task<IReadOnlyList<DurableSessionEntry>> LoadAsync(
            string sessionId, CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                return Task.FromResult<IReadOnlyList<DurableSessionEntry>>(
                    _store.TryGetValue(sessionId, out var bucket) ? bucket.ToArray() : []);
            }
        }

        public Task AppendAsync(
            string sessionId,
            IReadOnlyList<DurableSessionEntry> entries,
            CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                if (!_store.TryGetValue(sessionId, out var bucket))
                {
                    bucket = [];
                    _store[sessionId] = bucket;
                }
                bucket.AddRange(entries);
            }
            return Task.CompletedTask;
        }

        public Task ReplaceAsync(
            string sessionId,
            IReadOnlyList<DurableSessionEntry> reducedEntries,
            CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                _store[sessionId] = [.. reducedEntries];
            }
            return Task.CompletedTask;
        }

        public IReadOnlyList<DurableSessionEntry> Snapshot(string sessionId)
        {
            lock (_gate)
            {
                return _store.TryGetValue(sessionId, out var bucket) ? bucket.ToArray() : [];
            }
        }
    }
}
