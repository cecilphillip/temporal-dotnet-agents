using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Temporalio.Client;
using Temporalio.Common;
using Temporalio.Extensions.AI;
using Temporalio.Extensions.Agents.IntegrationTests.Helpers;
using Temporalio.Extensions.Agents.Session;
using Temporalio.Extensions.Agents.Tests.StepMode; // shared scaffolding (linked via .csproj)
using Temporalio.Extensions.Hosting;
using Temporalio.Testing;
using Xunit;

namespace Temporalio.Extensions.Agents.IntegrationTests;

/// <summary>
/// Crash-safety coverage for v0.3 durable agents (registered via <c>AddDurableAgent</c>).
/// Pins the load-bearing behavioral guarantee: a write tool registered with
/// <c>opts.NoRetry()</c> never double-fires even across worker restarts, and a transient
/// read-tool failure is retried independently of write-tool retries.
/// </summary>
[Trait("Category", "Integration")]
public class DurableAgentCrashSafetyTests
{
    private const string InvokeAgentToolActivity = "Temporalio.Extensions.Agents.InvokeAgentTool";

    /// <summary>
    /// Write tool with <c>NoRetry()</c> never double-fires across a worker restart.
    /// </summary>
    [Fact]
    public async Task DurableAgent_WriteToolNoRetry_DoesNotDoubleFire()
    {
        await using var env = await TestEnvironmentHelper.StartLocalAsync();
        env.Client.Options.DataConverter = TemporalAgentDataConverter.Instance;
        var taskQueue = $"durable-crash-{Guid.NewGuid():N}";

        // Single recorder shared across both hosts (same process — CallCount survives restart).
        var recorder = new RecordingTool
        {
            Name = "send_email",
            Behavior = RecordingToolBehavior.AlwaysSucceed,
        };
        var aiFunction = recorder.Build();

        // Two-turn script: turn 1 → tool call → final text; turn 2 → tool call → final text.
        var scriptedResponses = new List<ChatResponse>
        {
            new(new ChatMessage(ChatRole.Assistant, [
                new FunctionCallContent("call-A", "send_email",
                    new Dictionary<string, object?> { ["input"] = "turn1" })
            ])),
            new(new ChatMessage(ChatRole.Assistant, "Done turn 1.")),
            new(new ChatMessage(ChatRole.Assistant, [
                new FunctionCallContent("call-B", "send_email",
                    new Dictionary<string, object?> { ["input"] = "turn2" })
            ])),
            new(new ChatMessage(ChatRole.Assistant, "Done turn 2.")),
        };
        var scripted = new ScriptedChatClient(scriptedResponses);

        // Host #1 — drive turn 1.
        var host1 = BuildHostOnTaskQueue(env.Client, taskQueue, scripted, aiFunction);
        await host1.StartAsync();

        TemporalAgentSession session;
        try
        {
            var proxy1 = host1.Services.GetTemporalAgentProxy("DurableAgent");
            session = (TemporalAgentSession)await proxy1.CreateSessionAsync();

            var r1 = await proxy1.RunAsync("first turn", session);
            Assert.NotNull(r1);
            Assert.Equal(1, recorder.CallCount);
        }
        finally
        {
            await host1.StopAsync();
            host1.Dispose();
        }

        // After turn 1: exactly 1 InvokeAgentTool schedule + 1 complete.
        var handle = env.Client.GetWorkflowHandle(session.SessionId.WorkflowId);
        var (preRestartSchedules, preRestartCompletes) = await CountActivityAsync(handle, InvokeAgentToolActivity);
        Assert.Equal(1, preRestartSchedules);
        Assert.Equal(1, preRestartCompletes);

        // Host #2 — same task queue, same recorder/scripted-client instances. Workflow replay
        // must NOT re-fire the historical InvokeAgentTool activity from turn 1.
        var host2 = BuildHostOnTaskQueue(env.Client, taskQueue, scripted, aiFunction);
        await host2.StartAsync();
        try
        {
            var proxy2 = host2.Services.GetTemporalAgentProxy("DurableAgent");
            var resumedSession = new TemporalAgentSession(session.SessionId);
            var r2 = await proxy2.RunAsync("second turn", resumedSession);
            Assert.NotNull(r2);

            // Total CallCount = 2 — one per turn, no double-fire on replay.
            Assert.Equal(2, recorder.CallCount);

            var (totalSchedules, totalCompletes) = await CountActivityAsync(handle, InvokeAgentToolActivity);
            Assert.Equal(2, totalSchedules);
            Assert.Equal(2, totalCompletes);
        }
        finally
        {
            await host2.StopAsync();
            host2.Dispose();
        }
    }

    /// <summary>
    /// Read tool with default retry policy retries independently of a write tool registered
    /// with <c>NoRetry()</c>. Uses <c>FailOnceThenSucceed</c> so the first attempt of the read
    /// tool fails and Temporal retries it.
    /// </summary>
    [Fact]
    public async Task DurableAgent_TransientReadToolFailure_RetriesIndependently()
    {
        await using var env = await TestEnvironmentHelper.StartLocalAsync();
        env.Client.Options.DataConverter = TemporalAgentDataConverter.Instance;
        var taskQueue = $"durable-crash-retry-{Guid.NewGuid():N}";

        // Read tool fails once, then succeeds — exercises the default retry policy.
        var readTool = new RecordingTool
        {
            Name = "lookup_data",
            Behavior = RecordingToolBehavior.FailOnceThenSucceed,
        };
        var readAiFunction = readTool.Build();

        // Write tool always succeeds, but is registered with NoRetry — it must run exactly once.
        var writeTool = new RecordingTool
        {
            Name = "send_email",
            Behavior = RecordingToolBehavior.AlwaysSucceed,
        };
        var writeAiFunction = writeTool.Build();

        // Single turn: LLM calls both tools in parallel, then returns final answer.
        var responses = new List<ChatResponse>
        {
            new(new ChatMessage(ChatRole.Assistant, [
                new FunctionCallContent("call-read", "lookup_data",
                    new Dictionary<string, object?> { ["input"] = "q" }),
                new FunctionCallContent("call-write", "send_email",
                    new Dictionary<string, object?> { ["input"] = "draft" }),
            ])),
            new(new ChatMessage(ChatRole.Assistant, "All done.")),
        };
        var scripted = new ScriptedChatClient(responses);

        using var host = BuildHostOnTaskQueueWithTools(env.Client, taskQueue, scripted,
            agent =>
            {
                agent.AddTool(readAiFunction);
                agent.AddTool(writeAiFunction, opts => opts.NoRetry());
            });
        await host.StartAsync();
        try
        {
            var proxy = host.Services.GetTemporalAgentProxy("DurableAgent");
            var session = (TemporalAgentSession)await proxy.CreateSessionAsync();
            var response = await proxy.RunAsync("Go", session);
            Assert.NotNull(response);

            // Read tool was called twice (1 fail + 1 retry succeed); write tool was called once.
            Assert.Equal(2, readTool.CallCount);
            Assert.Equal(1, writeTool.CallCount);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    private static async Task<(int Schedules, int Completes)> CountActivityAsync(
        WorkflowHandle handle, string activityTypeName)
    {
        var schedules = 0;
        var completes = 0;
        var scheduledIdToType = new Dictionary<long, string>();
        await foreach (var ev in handle.FetchHistoryEventsAsync())
        {
            if (ev.ActivityTaskScheduledEventAttributes is { } a)
            {
                scheduledIdToType[ev.EventId] = a.ActivityType.Name;
                if (a.ActivityType.Name == activityTypeName)
                {
                    schedules++;
                }
            }
            else if (ev.ActivityTaskCompletedEventAttributes is { } c &&
                     scheduledIdToType.TryGetValue(c.ScheduledEventId, out var typeName) &&
                     typeName == activityTypeName)
            {
                completes++;
            }
        }
        return (schedules, completes);
    }

    private static IHost BuildHostOnTaskQueue(
        ITemporalClient client,
        string taskQueue,
        ScriptedChatClient scripted,
        AIFunction tool) =>
        BuildHostOnTaskQueueWithTools(client, taskQueue, scripted, agent =>
        {
            agent.AddTool(tool, opts => opts.NoRetry());
        });

    private static IHost BuildHostOnTaskQueueWithTools(
        ITemporalClient client,
        string taskQueue,
        ScriptedChatClient scripted,
        Action<DurableAgentBuilder> registerTools)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<ITemporalClient>(client);
        builder.Services.AddSingleton<IChatClient>(scripted);

        var workerBuilder = builder.Services.AddHostedTemporalWorker(taskQueue);
        workerBuilder.AddTemporalAgents(opts =>
        {
            opts.AddDurableAgent("DurableAgent", agent =>
            {
                agent.Instructions = "You are a helpful agent.";
                agent.ChatClient = sp => sp.GetRequiredService<IChatClient>();
                registerTools(agent);
            });
        });

        return builder.Build();
    }
}
