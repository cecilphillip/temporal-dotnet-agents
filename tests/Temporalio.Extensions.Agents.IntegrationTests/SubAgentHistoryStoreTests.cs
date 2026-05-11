using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Temporalio.Client;
using Temporalio.Extensions.AI;
using Temporalio.Extensions.Agents.HistoryStore;
using Temporalio.Extensions.Agents.IntegrationTests.Helpers;
using Temporalio.Extensions.Agents.Session;
using Temporalio.Extensions.Agents.State;
using Temporalio.Extensions.Agents.Tests.StepMode; // shared scaffolding (linked via .csproj)
using Temporalio.Extensions.Hosting;
using Temporalio.Testing;
using Temporalio.Workflows;
using Xunit;
using static Temporalio.Extensions.Agents.TemporalWorkflowExtensions;

namespace Temporalio.Extensions.Agents.IntegrationTests;

/// <summary>
/// Regression tests for the sub-agent execution path (<see cref="TemporalAIAgent"/> via
/// <see cref="TemporalWorkflowExtensions.GetAgent"/>) combined with an external
/// <see cref="IAgentHistoryStore"/>.
/// <para>
/// The bug fixed in commit f0da244: <c>TemporalAIAgent.RunCoreAsync</c> drove the durable
/// loop correctly but never dispatched <c>AppendAgentTurnAsync</c> after each turn, so the
/// store's <c>AppendAsync</c> was never called. <c>LoadAsync</c> ran on IsFirstStep=true
/// but the store remained empty.
/// </para>
/// </summary>
[Trait("Category", "Integration")]
public class SubAgentHistoryStoreTests
{
    private const string RunDurableAgentStepActivity = "Temporalio.Extensions.Agents.RunDurableAgentStep";

    // ── Test 1: PRIMARY REGRESSION TEST ────────────────────────────────────────

    /// <summary>
    /// Regression test for commit f0da244: sub-agent with an external history store must call
    /// <c>AppendAsync</c> once per turn. Before the fix, <c>AppendCount</c> was always 0.
    /// </summary>
    [Fact]
    public async Task SubAgent_WithHistoryStore_AppendsAfterEachTurn()
    {
        await using var env = await TestEnvironmentHelper.StartLocalAsync();
        env.Client.Options.DataConverter = TemporalAgentDataConverter.Instance;

        var store = new LocalInMemoryHistoryStore();
        var scripted = new ScriptedChatClient(new[]
        {
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "Turn1Response")),
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "Turn2Response")),
        });

        var taskQueue = $"sub-agent-store-basic-{Guid.NewGuid():N}";
        using var host = BuildHost<TwoTurnOrchestrationWorkflow>(env.Client, taskQueue, scripted,
            configureOpts: opts => { opts.HistoryStore = _ => store; },
            configureAgent: null);
        await host.StartAsync();
        try
        {
            var wfId = $"sub-agent-store-basic-{Guid.NewGuid():N}";
            var handle = await env.Client.StartWorkflowAsync(
                (TwoTurnOrchestrationWorkflow wf) => wf.RunAsync(
                    "SUBAGENT_TURN1_MARKER", "SUBAGENT_TURN2_MARKER"),
                new WorkflowOptions(wfId, taskQueue));

            await handle.GetResultAsync();

            // Core regression assertion: AppendAsync must have been called once per turn.
            // Before the fix this would have been 0.
            Assert.Equal(2, store.TotalAppendCount);

            // One Load call per turn (on IsFirstStep=true of each turn).
            Assert.Equal(2, store.TotalLoadCount);

            // 4 entries total: req1 + resp1 + req2 + resp2 (appended as pairs per turn).
            Assert.Equal(4, store.TotalEntryCount);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    // ── Test 2: Second turn loads from store ────────────────────────────────────

    /// <summary>
    /// Verifies that turn 2's activity sees the data appended by turn 1 — the store's
    /// <c>LoadAsync</c> on IsFirstStep=true of turn 2 returns turn 1's entries.
    /// </summary>
    [Fact]
    public async Task SubAgent_WithHistoryStore_SecondTurnLoadsFromStore()
    {
        await using var env = await TestEnvironmentHelper.StartLocalAsync();
        env.Client.Options.DataConverter = TemporalAgentDataConverter.Instance;

        var store = new LocalInMemoryHistoryStore();
        var scripted = new ScriptedChatClient(new[]
        {
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "Turn1Response")),
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "Turn2Response")),
        });

        var taskQueue = $"sub-agent-store-load-{Guid.NewGuid():N}";
        using var host = BuildHost<TwoTurnOrchestrationWorkflow>(env.Client, taskQueue, scripted,
            configureOpts: opts => { opts.HistoryStore = _ => store; },
            configureAgent: null);
        await host.StartAsync();
        try
        {
            var wfId = $"sub-agent-store-load-{Guid.NewGuid():N}";
            var handle = await env.Client.StartWorkflowAsync(
                (TwoTurnOrchestrationWorkflow wf) => wf.RunAsync(
                    "SUBAGENT_TURN1_MARKER", "SUBAGENT_TURN2_MARKER"),
                new WorkflowOptions(wfId, taskQueue));

            await handle.GetResultAsync();

            // The store must contain exactly 4 entries (req1, resp1, req2, resp2).
            Assert.Equal(4, store.TotalEntryCount);

            // Snapshot all session entries. There should be exactly one session ID
            // (the sub-agent's session).
            var allEntries = store.AllEntries();
            Assert.Equal(4, allEntries.Count);

            // Entry 0: request from turn 1 — user message contains turn-1 marker.
            var req1 = allEntries[0] as AgentSessionRequest;
            Assert.NotNull(req1);
            Assert.True(req1!.Messages.Any(m => m.Text?.Contains("SUBAGENT_TURN1_MARKER") == true),
                "First entry should be the turn-1 request with SUBAGENT_TURN1_MARKER.");

            // Entry 1: response from turn 1 — assistant message contains "Turn1Response".
            var resp1 = allEntries[1] as AgentSessionResponse;
            Assert.NotNull(resp1);
            Assert.True(resp1!.Messages.Any(m => m.Text?.Contains("Turn1Response") == true),
                "Second entry should be the turn-1 response with Turn1Response.");

            // Entry 2: request from turn 2 — user message contains turn-2 marker.
            var req2 = allEntries[2] as AgentSessionRequest;
            Assert.NotNull(req2);
            Assert.True(req2!.Messages.Any(m => m.Text?.Contains("SUBAGENT_TURN2_MARKER") == true),
                "Third entry should be the turn-2 request with SUBAGENT_TURN2_MARKER.");

            // Entry 3: response from turn 2 — assistant message contains "Turn2Response".
            var resp3 = allEntries[3] as AgentSessionResponse;
            Assert.NotNull(resp3);
            Assert.True(resp3!.Messages.Any(m => m.Text?.Contains("Turn2Response") == true),
                "Fourth entry should be the turn-2 response with Turn2Response.");
        }
        finally
        {
            await host.StopAsync();
        }
    }

    // ── Test 3: No cross-turn duplication in activity payload ───────────────────

    /// <summary>
    /// Verifies that when the external history store is configured for a sub-agent,
    /// turn 2's <c>RunDurableAgentStep</c> activity-scheduled event does NOT contain
    /// turn 1's user-message marker — prior-turn content belongs to the store, not
    /// the Temporal event log.
    /// </summary>
    [Fact]
    public async Task SubAgent_WithHistoryStore_NoCrossTurnDuplication()
    {
        await using var env = await TestEnvironmentHelper.StartLocalAsync();
        env.Client.Options.DataConverter = TemporalAgentDataConverter.Instance;

        var store = new LocalInMemoryHistoryStore();
        var scripted = new ScriptedChatClient(new[]
        {
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "Turn 1 done.")),
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "Turn 2 done.")),
        });

        var taskQueue = $"sub-agent-store-payload-{Guid.NewGuid():N}";
        using var host = BuildHost<TwoTurnOrchestrationWorkflow>(env.Client, taskQueue, scripted,
            configureOpts: opts => { opts.HistoryStore = _ => store; },
            configureAgent: null);
        await host.StartAsync();
        try
        {
            const string turn1Marker = "SUBAGENT_TURN1_MARKER";
            const string turn2Marker = "SUBAGENT_TURN2_MARKER";

            var wfId = $"sub-agent-store-payload-{Guid.NewGuid():N}";
            var handle = await env.Client.StartWorkflowAsync(
                (TwoTurnOrchestrationWorkflow wf) => wf.RunAsync(turn1Marker, turn2Marker),
                new WorkflowOptions(wfId, taskQueue));

            await handle.GetResultAsync();

            // Walk the orchestrating workflow's history events to find the first
            // RunDurableAgentStep ActivityTaskScheduled whose payload contains the turn-2
            // marker. Assert that same payload does NOT contain the turn-1 marker.
            string? turn2StepPayload = null;
            await foreach (var ev in handle.FetchHistoryEventsAsync())
            {
                if (ev.ActivityTaskScheduledEventAttributes is { } a &&
                    a.ActivityType.Name == RunDurableAgentStepActivity &&
                    a.Input.Payloads_.Count > 0)
                {
                    var payloadJson = Encoding.UTF8.GetString(
                        a.Input.Payloads_[0].Data.ToByteArray());
                    if (payloadJson.Contains(turn2Marker, StringComparison.Ordinal))
                    {
                        turn2StepPayload = payloadJson;
                        break;
                    }
                }
            }

            Assert.NotNull(turn2StepPayload);
            Assert.Contains(turn2Marker, turn2StepPayload!, StringComparison.Ordinal);
            // Load-bearing: turn-1's marker must NOT appear in turn-2's scheduled event —
            // it belongs in the external store instead.
            Assert.DoesNotContain(turn1Marker, turn2StepPayload!, StringComparison.Ordinal);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    // ── Test 4: Iteration-cap path also appends ─────────────────────────────────

    /// <summary>
    /// Verifies that when the sub-agent hits the per-turn tool-call iteration cap,
    /// the iteration-cap exit path also dispatches <c>AppendAgentTurnAsync</c>.
    /// This covers the second store-dispatch site in <c>TemporalAIAgent.RunCoreAsync</c>.
    /// </summary>
    [Fact]
    public async Task SubAgent_WithHistoryStore_IterationCapPath_AlsoAppends()
    {
        await using var env = await TestEnvironmentHelper.StartLocalAsync();
        env.Client.Options.DataConverter = TemporalAgentDataConverter.Instance;

        var store = new LocalInMemoryHistoryStore();

        // Agent always returns a tool call — never converges on a final answer.
        var recorder = new RecordingTool { Name = "loop_tool" };
        var aiFunction = recorder.Build();

        const int cap = 2;
        // Provide more scripted tool-call responses than the cap to guarantee the cap fires.
        var loopResponses = Enumerable.Range(0, cap + 2)
            .Select(i => new ChatResponse(new ChatMessage(ChatRole.Assistant, [
                new FunctionCallContent(
                    $"call-{i}",
                    "loop_tool",
                    new Dictionary<string, object?> { ["input"] = "go" })
            ])))
            .ToList();
        var scripted = new ScriptedChatClient(loopResponses);

        var taskQueue = $"sub-agent-store-itercap-{Guid.NewGuid():N}";
        using var host = BuildHost<SingleTurnOrchestrationWorkflow>(env.Client, taskQueue, scripted,
            configureOpts: opts => { opts.HistoryStore = _ => store; },
            configureAgent: agent =>
            {
                agent.MaxToolCallsPerTurn = cap;
                agent.AddTool(aiFunction);
            });
        await host.StartAsync();
        try
        {
            var wfId = $"sub-agent-store-itercap-{Guid.NewGuid():N}";
            var handle = await env.Client.StartWorkflowAsync(
                (SingleTurnOrchestrationWorkflow wf) => wf.RunAsync("ITERCAP_USER_MSG"),
                new WorkflowOptions(wfId, taskQueue));

            // The orchestrating workflow returns the sub-agent's response text.
            // With the iteration cap hit the response messages are all tool-related.
            await handle.GetResultAsync();

            // Core assertion: even though the cap was hit (not a normal final-response exit),
            // AppendAsync must still have been called exactly once for the one turn.
            Assert.Equal(1, store.TotalAppendCount);

            // The store should contain 2 entries: the request and the iteration-cap response.
            Assert.Equal(2, store.TotalEntryCount);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    // ── Host builder ────────────────────────────────────────────────────────────

    private static IHost BuildHost<TWorkflow>(
        ITemporalClient client,
        string taskQueue,
        ScriptedChatClient scripted,
        Action<TemporalAgentsOptions>? configureOpts,
        Action<DurableAgentBuilder>? configureAgent)
        where TWorkflow : class
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<ITemporalClient>(client);
        builder.Services.AddSingleton<IChatClient>(scripted);

        builder.Services
            .AddHostedTemporalWorker(taskQueue)
            .AddWorkflow<TWorkflow>()
            .AddTemporalAgents(opts =>
            {
                configureOpts?.Invoke(opts);
                opts.AddDurableAgent("SubAgent", agent =>
                {
                    agent.Instructions = "You are a helpful sub-agent.";
                    agent.ChatClient = sp => sp.GetRequiredService<IChatClient>();
                    configureAgent?.Invoke(agent);
                });
            });

        return builder.Build();
    }

    // ── Orchestrating workflow: two turns ───────────────────────────────────────

    /// <summary>
    /// Orchestrating workflow that calls a sub-agent twice in a single execution,
    /// reusing the same session. Used for Tests 1, 2, and 3.
    /// </summary>
    [Workflow("SubAgentStoreTests.TwoTurnOrchestration")]
    internal class TwoTurnOrchestrationWorkflow
    {
        [WorkflowRun]
        public async Task<string[]> RunAsync(string turn1Msg, string turn2Msg)
        {
            var agent = GetAgent("SubAgent");
            var session = await agent.CreateSessionAsync().ConfigureAwait(true);
            var r1 = await agent.RunAsync(
                [new ChatMessage(ChatRole.User, turn1Msg)],
                session).ConfigureAwait(true);
            var r2 = await agent.RunAsync(
                [new ChatMessage(ChatRole.User, turn2Msg)],
                session).ConfigureAwait(true);
            return [r1.Text ?? "", r2.Text ?? ""];
        }
    }

    // ── Orchestrating workflow: single turn ──────────────────────────────────────

    /// <summary>
    /// Orchestrating workflow that calls a sub-agent exactly once. Used for Test 4
    /// where the iteration cap is expected to fire during that single turn.
    /// </summary>
    [Workflow("SubAgentStoreTests.SingleTurnOrchestration")]
    internal class SingleTurnOrchestrationWorkflow
    {
        [WorkflowRun]
        public async Task<string> RunAsync(string userMsg)
        {
            var agent = GetAgent("SubAgent");
            var session = await agent.CreateSessionAsync().ConfigureAwait(true);
            var response = await agent.RunAsync(
                [new ChatMessage(ChatRole.User, userMsg)],
                session).ConfigureAwait(true);
            // Return last message text (may be a Tool role message when cap is hit).
            return response.Messages.Count > 0
                ? response.Messages[^1].Text ?? string.Empty
                : string.Empty;
        }
    }

    // ── In-process history store ─────────────────────────────────────────────────

    /// <summary>
    /// Concurrent-safe in-memory <see cref="IAgentHistoryStore"/> with aggregate counters
    /// that span all session IDs. Allows assertions without needing to know the session ID
    /// generated inside the workflow via <c>TemporalAgentSessionId.WithDeterministicKey</c>.
    /// </summary>
    private sealed class LocalInMemoryHistoryStore : IAgentHistoryStore
    {
        private readonly Dictionary<string, List<DurableSessionEntry>> _store =
            new(StringComparer.Ordinal);
        private readonly object _gate = new();
        private int _loadCount;
        private int _appendCount;
        private int _replaceCount;

        public int TotalLoadCount => Volatile.Read(ref _loadCount);
        public int TotalAppendCount => Volatile.Read(ref _appendCount);
        public int TotalReplaceCount => Volatile.Read(ref _replaceCount);

        public int TotalEntryCount
        {
            get
            {
                lock (_gate)
                    return _store.Values.Sum(b => b.Count);
            }
        }

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

        /// <summary>
        /// Returns all entries across all session IDs, in the order they were appended
        /// within each session bucket. When exactly one session is present (the common
        /// case in these tests), this gives the full ordered history.
        /// </summary>
        public IReadOnlyList<DurableSessionEntry> AllEntries()
        {
            lock (_gate)
            {
                return _store.Values.SelectMany(b => b).ToArray();
            }
        }

        public IReadOnlyList<DurableSessionEntry> Snapshot(string sessionId)
        {
            lock (_gate)
            {
                return _store.TryGetValue(sessionId, out var bucket)
                    ? bucket.ToArray()
                    : [];
            }
        }
    }
}
