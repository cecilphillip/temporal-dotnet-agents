using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Temporalio.Client;
using Temporalio.Extensions.AI;
using Temporalio.Extensions.Agents.HistoryStore;
using Temporalio.Extensions.Agents.IntegrationTests.Helpers;
using Temporalio.Extensions.Agents.Session;
using Temporalio.Extensions.Agents.Tests.StepMode; // shared scaffolding (linked via .csproj)
using Temporalio.Extensions.Hosting;
using Temporalio.Testing;
using Xunit;

namespace Temporalio.Extensions.Agents.IntegrationTests;

/// <summary>
/// Phase 4 (v0.3): integration coverage for the new <c>opts.HistoryStore</c> +
/// <c>agent.HistoryStore</c> path replacing the legacy
/// <c>opts.UseExternalHistory = true</c> opt-in. Verifies that
/// <list type="bullet">
///   <item>worker-level <see cref="TemporalAgentsOptions.HistoryStore"/> applies to durable agents that don't override</item>
///   <item>per-agent <see cref="DurableAgentBuilder.HistoryStore"/> wins over the worker default</item>
///   <item>without any store, the workflow's in-memory history continues to drive multi-turn dispatch</item>
///   <item>when a store is configured, the activity-scheduled event payload omits prior turns' messages (PII / O(n²) mitigation)</item>
/// </list>
/// </summary>
[Trait("Category", "Integration")]
public class DurableAgentHistoryStoreTests
{
    private const string RunDurableAgentStepActivity = "Temporalio.Extensions.Agents.RunDurableAgentStep";

    [Fact]
    public async Task DurableAgent_WithWorkerHistoryStore_LoadsAndAppendsHistory()
    {
        await using var env = await TestEnvironmentHelper.StartLocalAsync();
        env.Client.Options.DataConverter = TemporalAgentDataConverter.Instance;

        var store = new IntegrationInMemoryHistoryStore();
        var scripted = new ScriptedChatClient(new[]
        {
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "Turn 1.")),
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "Turn 2.")),
        });

        using var host = BuildHost(env.Client, scripted, configureOpts: opts =>
        {
            opts.HistoryStore = _ => store;
        }, configureAgent: null);
        await host.StartAsync();
        try
        {
            var proxy = host.Services.GetTemporalAgentProxy("DurableAgent");
            var session = (TemporalAgentSession)await proxy.CreateSessionAsync();

            var r1 = await proxy.RunAsync("First", session);
            Assert.Contains("Turn 1.", r1.Messages[^1].Text);

            var r2 = await proxy.RunAsync("Second", session);
            Assert.Contains("Turn 2.", r2.Messages[^1].Text);

            // Two turns: each turn loads (once at start) and appends (once at end).
            Assert.Equal(2, store.LoadCount);
            Assert.Equal(2, store.AppendCount);

            // The store now contains 4 entries: req1, resp1, req2, resp2 — paired in append order.
            var entries = store.Snapshot(session.SessionId.WorkflowId);
            Assert.Equal(4, entries.Count);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task DurableAgent_WithPerAgentHistoryStore_OverridesWorker()
    {
        await using var env = await TestEnvironmentHelper.StartLocalAsync();
        env.Client.Options.DataConverter = TemporalAgentDataConverter.Instance;

        var workerStore = new IntegrationInMemoryHistoryStore();
        var perAgentStore = new IntegrationInMemoryHistoryStore();
        var scripted = new ScriptedChatClient(new[]
        {
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "Done.")),
        });

        using var host = BuildHost(env.Client, scripted, configureOpts: opts =>
        {
            opts.HistoryStore = _ => workerStore;
        }, configureAgent: agent =>
        {
            agent.HistoryStore = _ => perAgentStore;
        });
        await host.StartAsync();
        try
        {
            var proxy = host.Services.GetTemporalAgentProxy("DurableAgent");
            var session = (TemporalAgentSession)await proxy.CreateSessionAsync();
            await proxy.RunAsync("Hi", session);

            // Per-agent store wins; worker default never sees the call.
            Assert.True(perAgentStore.LoadCount >= 1);
            Assert.True(perAgentStore.AppendCount >= 1);
            Assert.Equal(0, workerStore.LoadCount);
            Assert.Equal(0, workerStore.AppendCount);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task DurableAgent_WithoutAnyHistoryStore_UsesWorkflowState()
    {
        await using var env = await TestEnvironmentHelper.StartLocalAsync();
        env.Client.Options.DataConverter = TemporalAgentDataConverter.Instance;

        var scripted = new ScriptedChatClient(new[]
        {
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "Turn 1.")),
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "Turn 2.")),
        });

        using var host = BuildHost(env.Client, scripted, configureOpts: null, configureAgent: null);
        await host.StartAsync();
        try
        {
            var proxy = host.Services.GetTemporalAgentProxy("DurableAgent");
            var session = (TemporalAgentSession)await proxy.CreateSessionAsync();

            await proxy.RunAsync("First", session);
            await proxy.RunAsync("Second", session);

            // Without an external store, the workflow's GetHistory query should expose the
            // in-memory entries — request, response, request, response.
            var handle = env.Client.GetWorkflowHandle<Workflows.AgentWorkflow>(session.SessionId.WorkflowId);
            var history = await handle.QueryAsync(wf => wf.GetHistory());
            Assert.NotNull(history);
            Assert.True(history.Count >= 4,
                $"expected at least 4 history entries (req+resp per turn); got {history.Count}");
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task DurableAgent_HistoryStoreConfigured_PayloadOmitsPriorMessages()
    {
        // Phase 4 canary: when an external history store is configured for a durable agent,
        // turn-2's RunDurableAgentStep ActivityScheduled event payload must NOT contain turn-1's
        // user-message marker — proves history really left the Temporal event log.
        await using var env = await TestEnvironmentHelper.StartLocalAsync();
        env.Client.Options.DataConverter = TemporalAgentDataConverter.Instance;

        var store = new IntegrationInMemoryHistoryStore();
        var scripted = new ScriptedChatClient(new[]
        {
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "Turn 1 done.")),
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "Turn 2 done.")),
        });

        using var host = BuildHost(env.Client, scripted, configureOpts: opts =>
        {
            opts.HistoryStore = _ => store;
        }, configureAgent: null);
        await host.StartAsync();
        try
        {
            var proxy = host.Services.GetTemporalAgentProxy("DurableAgent");
            var session = (TemporalAgentSession)await proxy.CreateSessionAsync();

            const string turn1Marker = "PHASE4_TURN1_ALPHA";
            const string turn2Marker = "PHASE4_TURN2_BETA";

            await proxy.RunAsync(turn1Marker, session);
            await proxy.RunAsync(turn2Marker, session);

            var handle = env.Client.GetWorkflowHandle(session.SessionId.WorkflowId);

            // Collect the very first scheduled RunDurableAgentStep payload of turn 2 (steps after
            // the first turn). To find "turn 2's first step", we'll look at every scheduled event
            // for the durable-step activity and pick the one whose payload contains the turn-2
            // marker; assert that same payload does NOT contain the turn-1 marker.
            string? turn2StepPayload = null;
            await foreach (var ev in handle.FetchHistoryEventsAsync())
            {
                if (ev.ActivityTaskScheduledEventAttributes is { } a &&
                    a.ActivityType.Name == RunDurableAgentStepActivity &&
                    a.Input.Payloads_.Count > 0)
                {
                    var payloadJson = Encoding.UTF8.GetString(a.Input.Payloads_[0].Data.ToByteArray());
                    if (payloadJson.Contains(turn2Marker, StringComparison.Ordinal))
                    {
                        turn2StepPayload = payloadJson;
                        break;
                    }
                }
            }

            Assert.NotNull(turn2StepPayload);
            Assert.Contains(turn2Marker, turn2StepPayload!, StringComparison.Ordinal);
            // Load-bearing: turn-1's marker must NOT live in the activity-scheduled event for
            // turn 2 — it sits in the external store instead.
            Assert.DoesNotContain(turn1Marker, turn2StepPayload!, StringComparison.Ordinal);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    private static IHost BuildHost(
        ITemporalClient client,
        ScriptedChatClient scripted,
        Action<TemporalAgentsOptions>? configureOpts,
        Action<DurableAgentBuilder>? configureAgent)
    {
        var taskQueue = $"durable-agent-store-{Guid.NewGuid():N}";
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton(client);
        builder.Services.AddSingleton<IChatClient>(scripted);

        builder.Services
            .AddHostedTemporalWorker(taskQueue)
            .AddTemporalAgents(opts =>
            {
                configureOpts?.Invoke(opts);
                opts.AddDurableAgent("DurableAgent", agent =>
                {
                    agent.Instructions = "You are a helpful agent.";
                    agent.ChatClient = sp => sp.GetRequiredService<IChatClient>();
                    configureAgent?.Invoke(agent);
                });
            });

        return builder.Build();
    }

    /// <summary>
    /// Concurrent-safe in-memory <see cref="IAgentHistoryStore"/> with call-count counters.
    /// Mirrors the pattern in <see cref="ExternalHistoryStorePayloadTests"/> — kept local to
    /// the integration test project to avoid cross-project test wiring.
    /// </summary>
    private sealed class IntegrationInMemoryHistoryStore : IAgentHistoryStore
    {
        private readonly Dictionary<string, List<DurableSessionEntry>> _store = new(StringComparer.Ordinal);
        private readonly object _gate = new();
        private int _loadCount;
        private int _appendCount;
        private int _replaceCount;

        public int LoadCount => Volatile.Read(ref _loadCount);
        public int AppendCount => Volatile.Read(ref _appendCount);
        public int ReplaceCount => Volatile.Read(ref _replaceCount);

        public Task<IReadOnlyList<DurableSessionEntry>> LoadAsync(
            string sessionId, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _loadCount);
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
            Interlocked.Increment(ref _appendCount);
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
            Interlocked.Increment(ref _replaceCount);
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
