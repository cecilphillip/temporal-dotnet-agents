using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Temporalio.Client;
using Temporalio.Common;
using Temporalio.Extensions.AI;
using Temporalio.Extensions.Agents.IntegrationTests.Helpers;
using Temporalio.Extensions.Agents.Session;
using Temporalio.Extensions.Agents.Tests.StepMode; // shared scaffolding (linked via .csproj)
using Temporalio.Extensions.Agents.Workflows;
using Temporalio.Extensions.Hosting;
using Temporalio.Testing;
using Temporalio.Workflows;
using Xunit;

namespace Temporalio.Extensions.Agents.IntegrationTests;

/// <summary>
/// Crash-safety coverage for step mode (per-tool Temporal activities).
/// Pins the load-bearing behavioral guarantee of Feature 2: a write tool with
/// <c>RetryPolicy.MaximumAttempts = 1</c> does not double-fire when something goes wrong
/// mid-turn — and a tool that completed before a worker restart is not re-invoked when
/// the workflow replays history on the new worker.
/// </summary>
/// <remarks>
/// <para>
/// Two complementary tests:
/// <list type="number">
///   <item>
///     <c>MaxAttemptsOne_FailingWriteTool_FiresExactlyOnce</c> — the "alternative" approach
///     from the gap brief. Configures a recording tool to throw on every call with
///     <c>MaximumAttempts = 1</c>. Asserts the tool is invoked exactly once and the workflow
///     surfaces the failure rather than retrying. Distinct from existing test 2.5 (which
///     also uses <c>AlwaysFail</c> + <c>MaxAttempts = 1</c>) because the framing here is
///     "this is the no-double-fire guarantee" — the file's name and assertion comments
///     pin the value-prop that this test protects.
///   </item>
///   <item>
///     <c>WorkerRestart_AfterCompletedToolCall_DoesNotReinvokeToolOnReplay</c> — the
///     restart variant. Drives a turn through host #1 (tool fires once, workflow records
///     the result in Temporal history), stops host #1 abruptly, starts host #2 on the
///     same task queue with the SAME <see cref="RecordingTool"/> instance (same process —
///     CallCount survives), and drives a fresh turn. Asserts:
///     (a) host #2 replays history WITHOUT re-invoking the previously-completed tool;
///     (b) host #2's new turn DOES invoke the tool once.
///     Total <c>CallCount</c> across the two turns is exactly 2.
///   </item>
/// </list>
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
public class StepModeCrashSafetyTests
{
    /// <summary>
    /// Step-mode write tool with <c>MaximumAttempts = 1</c> never double-fires.
    /// </summary>
    [Fact]
    public async Task MaxAttemptsOne_FailingWriteTool_FiresExactlyOnce()
    {
        await using var env = await TestEnvironmentHelper.StartLocalAsync();
        env.Client.Options.DataConverter = TemporalAgentDataConverter.Instance;

        // Recording tool fails on EVERY call. With MaxAttempts=1 the workflow must surface
        // the failure to the caller after exactly one tool invocation — no retry, no
        // double-fire, no silent recovery.
        var recorder = new RecordingTool
        {
            Name = "send_email",
            Behavior = RecordingToolBehavior.AlwaysFail,
        };
        var aiFunction = recorder.Build();
        var fc = new FunctionCallContent("call-1", "send_email",
            new Dictionary<string, object?> { ["input"] = "draft" });
        var scripted = ScriptedChatClient.WithToolCallsThenFinal([fc], "Final.");

        var perToolOpts = new Dictionary<string, ActivityOptions>
        {
            ["send_email"] = new ActivityOptions
            {
                StartToCloseTimeout = TimeSpan.FromSeconds(10),
                RetryPolicy = new RetryPolicy { MaximumAttempts = 1 },
            },
        };

        using var host = BuildHost(env.Client, scripted, [aiFunction], perToolOpts);
        await host.StartAsync();
        try
        {
            var proxy = host.Services.GetTemporalAgentProxy("StepAgent");
            var session = await proxy.CreateSessionAsync();

            // The workflow propagates the tool failure to the caller. We don't pin the
            // exact exception type — the load-bearing assertion is the call count below.
            await Assert.ThrowsAnyAsync<Exception>(async () =>
                await proxy.RunAsync("Hi", session));

            // EXACTLY ONE invocation — the no-double-fire contract for write tools.
            Assert.Equal(1, recorder.CallCount);

            // Defensive sanity check: the InvokeFunction activity scheduled exactly once.
            // If MaxAttempts=1 was silently overridden, we'd see retries reflected as
            // additional schedules in workflow history.
            var sessionId = ((TemporalAgentSession)session).SessionId;
            var handle = env.Client.GetWorkflowHandle(sessionId.WorkflowId);
            var invokeFunctionScheduleCount = 0;
            await foreach (var ev in handle.FetchHistoryEventsAsync())
            {
                if (ev.ActivityTaskScheduledEventAttributes is { } a &&
                    a.ActivityType.Name == "Temporalio.Extensions.AI.InvokeFunction")
                {
                    invokeFunctionScheduleCount++;
                }
            }
            Assert.Equal(1, invokeFunctionScheduleCount);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    /// <summary>
    /// Worker restart in the middle of a step-mode session does not cause completed tool
    /// activities to re-fire when the new worker replays history.
    /// </summary>
    [Fact]
    public async Task WorkerRestart_AfterCompletedToolCall_DoesNotReinvokeToolOnReplay()
    {
        await using var env = await TestEnvironmentHelper.StartLocalAsync();
        env.Client.Options.DataConverter = TemporalAgentDataConverter.Instance;
        var taskQueue = $"step-crash-{Guid.NewGuid():N}";

        // Single RecordingTool instance shared across both hosts in this process.
        // CallCount survives the host restart so we can assert exactly-once across runs.
        var recorder = new RecordingTool
        {
            Name = "log_event",
            Behavior = RecordingToolBehavior.AlwaysSucceed,
        };
        var aiFunction = recorder.Build();

        // Each turn scripts: turn-N → 1 tool call → final text. So a 2-turn session
        // hits the LLM 4 times total (2 per turn) and the tool 2 times total.
        var scriptedResponses = new List<ChatResponse>
        {
            new(new ChatMessage(ChatRole.Assistant, [
                new FunctionCallContent("call-A", "log_event",
                    new Dictionary<string, object?> { ["input"] = "turn1" })
            ])),
            new(new ChatMessage(ChatRole.Assistant, "Done turn 1.")),
            new(new ChatMessage(ChatRole.Assistant, [
                new FunctionCallContent("call-B", "log_event",
                    new Dictionary<string, object?> { ["input"] = "turn2" })
            ])),
            new(new ChatMessage(ChatRole.Assistant, "Done turn 2.")),
        };
        var scripted = new ScriptedChatClient(scriptedResponses);

        // Host #1 — drive turn 1.
        var host1 = BuildHostOnTaskQueue(env.Client, taskQueue, scripted, [aiFunction]);
        await host1.StartAsync();

        TemporalAgentSession session;
        try
        {
            var proxy1 = host1.Services.GetTemporalAgentProxy("StepAgent");
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

        // Snapshot: number of InvokeFunction schedules + completes after turn 1.
        // After turn 1: exactly 1 schedule, 1 complete.
        var handle = env.Client.GetWorkflowHandle(session.SessionId.WorkflowId);
        var (preRestartSchedules, preRestartCompletes) = await CountInvokeFunctionAsync(handle);
        Assert.Equal(1, preRestartSchedules);
        Assert.Equal(1, preRestartCompletes);

        // Host #2 — same task queue, same recorder/scripted-client instances. The new
        // worker replays workflow history; the completed tool activity from turn 1 must
        // NOT re-fire (Temporal returns the cached completion from history).
        var host2 = BuildHostOnTaskQueue(env.Client, taskQueue, scripted, [aiFunction]);
        await host2.StartAsync();
        try
        {
            // Wait for replay to settle. Send turn 2 via a fresh proxy bound to the same
            // session. If host #2 re-invoked the historical tool during replay, CallCount
            // would jump from 1 → 2 BEFORE we send the second turn.
            //
            // The cleanest assertion is end-to-end: after turn 2 completes, CallCount
            // should be EXACTLY 2 (1 from turn 1, 1 from turn 2). If replay re-fired the
            // tool, we'd see CallCount >= 3.
            var proxy2 = host2.Services.GetTemporalAgentProxy("StepAgent");
            var resumedSession = new TemporalAgentSession(session.SessionId);
            var r2 = await proxy2.RunAsync("second turn", resumedSession);
            Assert.NotNull(r2);

            // The load-bearing assertion: ONE call from turn 1 + ONE call from turn 2 = 2.
            // Replay did not re-invoke the historical tool.
            Assert.Equal(2, recorder.CallCount);

            // Cross-check via Temporal history: exactly two InvokeFunction schedules total
            // across the workflow's history (one per turn). If replay had re-fired, we'd
            // see additional schedules.
            var (totalSchedules, totalCompletes) = await CountInvokeFunctionAsync(handle);
            Assert.Equal(2, totalSchedules);
            Assert.Equal(2, totalCompletes);
        }
        finally
        {
            await host2.StopAsync();
            host2.Dispose();
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    private static async Task<(int Schedules, int Completes)> CountInvokeFunctionAsync(WorkflowHandle handle)
    {
        const string invokeFunction = "Temporalio.Extensions.AI.InvokeFunction";
        var schedules = 0;
        var completes = 0;
        var scheduledIdToType = new Dictionary<long, string>();
        await foreach (var ev in handle.FetchHistoryEventsAsync())
        {
            if (ev.ActivityTaskScheduledEventAttributes is { } a)
            {
                scheduledIdToType[ev.EventId] = a.ActivityType.Name;
                if (a.ActivityType.Name == invokeFunction)
                {
                    schedules++;
                }
            }
            else if (ev.ActivityTaskCompletedEventAttributes is { } c &&
                     scheduledIdToType.TryGetValue(c.ScheduledEventId, out var typeName) &&
                     typeName == invokeFunction)
            {
                completes++;
            }
        }
        return (schedules, completes);
    }

    private static IHost BuildHost(
        ITemporalClient client,
        ScriptedChatClient scriptedResponses,
        IEnumerable<AIFunction> tools,
        Dictionary<string, ActivityOptions>? perToolActivityOptions = null) =>
        BuildHostOnTaskQueue(client, $"step-crash-{Guid.NewGuid():N}", scriptedResponses, tools, perToolActivityOptions);

    private static IHost BuildHostOnTaskQueue(
        ITemporalClient client,
        string taskQueue,
        ScriptedChatClient scriptedResponses,
        IEnumerable<AIFunction> tools,
        Dictionary<string, ActivityOptions>? perToolActivityOptions = null)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<ITemporalClient>(client);

        // Worker resolves IChatClient from DI in step mode (RunAgentStepAsync calls
        // services.GetRequiredService<IChatClient>()).
        builder.Services.AddSingleton<IChatClient>(scriptedResponses);

        var workerBuilder = builder.Services.AddHostedTemporalWorker(taskQueue);
        workerBuilder.AddDurableAI();
        workerBuilder.AddDurableTools([.. tools]);

        workerBuilder.AddTemporalAgents(opts =>
        {
            opts.EnablePerToolActivities = true;
            if (perToolActivityOptions is not null)
            {
                opts.PerToolActivityOptions = perToolActivityOptions;
            }

            opts.AddAIAgentFactory("StepAgent", _ =>
                new ChatClientAgent(
                    scriptedResponses,
                    new ChatClientAgentOptions
                    {
                        Name = "StepAgent",
                        ChatOptions = new ChatOptions { Instructions = "You are a helpful agent." },
                        UseProvidedChatClientAsIs = true,
                    }));
        });

        return builder.Build();
    }
}
