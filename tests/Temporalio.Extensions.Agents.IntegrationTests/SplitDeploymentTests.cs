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
/// Integration coverage for split worker/client deployments. Verifies bugs P1-1 and P1-2:
/// <list type="bullet">
///   <item>P1-2: Write tools registered with <c>NoRetry()</c> use <c>MaximumAttempts = 1</c>
///   even when the workflow was started by a proxy-only client.</item>
///   <item>P1-1: When an external history store is configured, turn-2's
///   <c>RunDurableAgentStep</c> payload does NOT contain turn-1's user message content
///   (i.e. the workflow correctly resolves <c>UseExternalStoreMode = true</c> from the
///   worker after the first step and strips prior-turn messages from accumulated context).</item>
/// </list>
/// </summary>
[Trait("Category", "Integration")]
public class SplitDeploymentTests
{
    private const string InvokeAgentToolActivity = "Temporalio.Extensions.Agents.InvokeAgentTool";
    private const string RunDurableAgentStepActivity = "Temporalio.Extensions.Agents.RunDurableAgentStep";

    /// <summary>
    /// P1-2: In a split deployment (client registers only an agent proxy, worker hosts the
    /// full durable agent), a write tool registered with <c>opts.NoRetry()</c> must use
    /// <c>MaximumAttempts = 1</c> for its <c>InvokeAgentTool</c> activity.
    ///
    /// Before the fix, <c>BuildProxyOnlyAgentWorkflowInput</c> left
    /// <c>DurableAgentToolActivityOptions = null</c> permanently, causing all tools to inherit
    /// the flat job-level <c>RetryPolicy</c> (unbounded retries) regardless of per-tool overrides.
    /// </summary>
    [Fact]
    public async Task SplitDeployment_WriteToolWithNoRetry_UsesMaximumAttempts1()
    {
        await using var env = await TestEnvironmentHelper.StartLocalAsync();
        env.Client.Options.DataConverter = TemporalAgentDataConverter.Instance;

        var recorder = new RecordingTool
        {
            Name = "write_record",
            Behavior = RecordingToolBehavior.AlwaysFail,
        };
        var aiFunction = recorder.Build();

        var fc = new FunctionCallContent("call-1", "write_record",
            new Dictionary<string, object?> { ["input"] = "data" });
        var scripted = ScriptedChatClient.WithToolCallsThenFinal([fc], "Done.");

        var taskQueue = $"split-deploy-noretry-{Guid.NewGuid():N}";

        // Build the worker host (full registration with NoRetry on write tool).
        using var workerHost = BuildWorkerHost(env.Client, scripted, taskQueue,
            configureAgent: agent =>
            {
                agent.AddTool(aiFunction, opts => opts.NoRetry());
            });
        await workerHost.StartAsync();

        try
        {
            // Build the client host (proxy-only, no AddDurableAgent, no IChatClient).
            using var clientHost = BuildClientHost(env.Client, taskQueue);

            var proxy = clientHost.Services.GetTemporalAgentProxy("DurableAgent");
            var session = (TemporalAgentSession)await proxy.CreateSessionAsync();

            // Tool always fails — exception surfaces to caller.
            await Assert.ThrowsAnyAsync<Exception>(async () =>
                await proxy.RunAsync("Hello", session));

            // Only one attempt should have been made (NoRetry = MaximumAttempts 1).
            Assert.Equal(1, recorder.CallCount);

            // Inspect history: the InvokeAgentTool scheduled event must carry MaximumAttempts=1.
            var handle = env.Client.GetWorkflowHandle(session.SessionId.WorkflowId);
            var foundToolSchedule = false;
            await foreach (var ev in handle.FetchHistoryEventsAsync())
            {
                if (ev.ActivityTaskScheduledEventAttributes is { } a &&
                    a.ActivityType.Name == InvokeAgentToolActivity)
                {
                    foundToolSchedule = true;
                    Assert.NotNull(a.RetryPolicy);
                    Assert.Equal(1, a.RetryPolicy.MaximumAttempts);
                    break;
                }
            }

            Assert.True(foundToolSchedule, "Expected at least one InvokeAgentTool ActivityTaskScheduled event.");
        }
        finally
        {
            await workerHost.StopAsync();
        }
    }

    /// <summary>
    /// P1-1: In a split deployment with an external history store, turn-2's
    /// <c>RunDurableAgentStep</c> activity-scheduled payload must NOT contain turn-1's
    /// user-message content. This proves that after the first step of the first turn, the
    /// workflow correctly resolves <c>UseExternalStoreMode = true</c> from the worker,
    /// and subsequent turns seed <c>AccumulatedMessages</c> from only the current request
    /// (not from in-workflow history carrying prior messages).
    /// </summary>
    [Fact]
    public async Task SplitDeployment_WithHistoryStore_Turn2PayloadOmitsTurn1Messages()
    {
        await using var env = await TestEnvironmentHelper.StartLocalAsync();
        env.Client.Options.DataConverter = TemporalAgentDataConverter.Instance;

        var store = new SplitDeploymentInMemoryStore();
        var scripted = new ScriptedChatClient(new[]
        {
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "Turn 1 done.")),
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "Turn 2 done.")),
        });

        var taskQueue = $"split-deploy-store-{Guid.NewGuid():N}";

        using var workerHost = BuildWorkerHost(env.Client, scripted, taskQueue,
            configureAgent: null,
            configureOpts: opts =>
            {
                opts.HistoryStore = _ => store;
            });
        await workerHost.StartAsync();

        try
        {
            using var clientHost = BuildClientHost(env.Client, taskQueue);

            var proxy = clientHost.Services.GetTemporalAgentProxy("DurableAgent");
            var session = (TemporalAgentSession)await proxy.CreateSessionAsync();

            const string turn1Marker = "SPLIT_TURN1_ALPHA";
            const string turn2Marker = "SPLIT_TURN2_BETA";

            await proxy.RunAsync(turn1Marker, session);
            await proxy.RunAsync(turn2Marker, session);

            var handle = env.Client.GetWorkflowHandle(session.SessionId.WorkflowId);

            // Find the RunDurableAgentStep scheduled event whose payload contains the
            // turn-2 marker, then assert it does NOT contain the turn-1 marker.
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
            // Load-bearing: turn-1's marker must NOT be in the turn-2 step payload —
            // it lives in the external store, not in AccumulatedMessages.
            Assert.DoesNotContain(turn1Marker, turn2StepPayload!, StringComparison.Ordinal);
        }
        finally
        {
            await workerHost.StopAsync();
        }
    }

    // ── Host builders ────────────────────────────────────────────────────────────

    private static IHost BuildWorkerHost(
        ITemporalClient client,
        ScriptedChatClient scripted,
        string taskQueue,
        Action<DurableAgentBuilder>? configureAgent,
        Action<TemporalAgentsOptions>? configureOpts = null)
    {
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
    /// Builds a client-only host that declares the agent proxy but hosts no worker.
    /// </summary>
    private static IHost BuildClientHost(ITemporalClient client, string taskQueue)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton(client);

        // AddTemporalAgentProxies: no worker, no IChatClient — proxy only.
        builder.Services.AddTemporalAgentProxies(
            configure: opts => opts.AddAgentProxy("DurableAgent"),
            taskQueue: taskQueue);

        return builder.Build();
    }

    private sealed class SplitDeploymentInMemoryStore : IAgentHistoryStore
    {
        private readonly Dictionary<string, List<DurableSessionEntry>> _store =
            new(StringComparer.Ordinal);
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
    }
}
