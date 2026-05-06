using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Temporalio.Client;
using Temporalio.Common;
using Temporalio.Extensions.AI;
using Temporalio.Extensions.Agents.IntegrationTests.Helpers;
using Temporalio.Extensions.Agents.Tests.StepMode; // shared scaffolding (linked via .csproj)
using Temporalio.Extensions.Agents.Workflows;
using Temporalio.Extensions.Hosting;
using Temporalio.Testing;
using Temporalio.Workflows;
using Xunit;

namespace Temporalio.Extensions.Agents.IntegrationTests;

/// <summary>
/// End-to-end integration coverage for step-mode (per-tool Temporal activities).
/// Each test stands up its own worker because step mode requires a different DI shape
/// (IChatClient + DurableTools + agent factory using the chat client) than the shared
/// EchoAgent fixture provides.
/// </summary>
[Trait("Category", "Integration")]
public class StepModeIntegrationTests : IClassFixture<StepModeEnvironmentFixture>
{
    private readonly StepModeEnvironmentFixture _fixture;
    private WorkflowEnvironment _env => _fixture.Environment;

    public StepModeIntegrationTests(StepModeEnvironmentFixture fixture)
    {
        _fixture = fixture;
    }

    // ── Test 2.1: default behavior unchanged ────────────────────────────────────

    [Fact]
    public async Task EnablePerToolActivities_False_UsesSingleActivityPath()
    {
        // When step mode is OFF, the EchoAgent path in the existing fixture stays unchanged:
        // a single ExecuteAgent activity per turn, no RunAgentStep activity scheduled.
        using var host = BuildHost(stepMode: false, scriptedResponses: null, tools: []);
        await host.StartAsync();

        var proxy = host.Services.GetTemporalAgentProxy("EchoAgent");
        var session = await proxy.CreateSessionAsync();
        var response = await proxy.RunAsync("Hello!", session);

        Assert.NotNull(response);
        Assert.Contains("Echo", response.Messages[0].Text);

        // Inspect workflow history: assert ExecuteAgent activity was scheduled, RunAgentStep was NOT.
        var sessionId = ((Temporalio.Extensions.Agents.Session.TemporalAgentSession)session).SessionId;
        var handle = _env.Client.GetWorkflowHandle(sessionId.WorkflowId);
        var activityNames = await CollectActivityNamesAsync(handle);

        Assert.Contains("Temporalio.Extensions.Agents.ExecuteAgent", activityNames);
        Assert.DoesNotContain("Temporalio.Extensions.Agents.RunAgentStep", activityNames);

        await host.StopAsync();
    }

    // ── Test 2.2: single tool call invokes step + tool + step ───────────────────

    [Fact]
    public async Task StepMode_SingleToolCall_InvokesStepThenToolThenStep()
    {
        var recorder = new RecordingTool { Name = "echo_tool" };
        var aiFunction = recorder.Build();
        var fc = new FunctionCallContent("call-1", "echo_tool",
            new Dictionary<string, object?> { ["input"] = "hello" });
        var scripted = ScriptedChatClient.WithToolCallsThenFinal([fc], "Final answer.");

        using var host = BuildHost(stepMode: true, scriptedResponses: scripted, tools: [aiFunction]);
        await host.StartAsync();

        var proxy = host.Services.GetTemporalAgentProxy("StepAgent");
        var session = await proxy.CreateSessionAsync();
        var response = await proxy.RunAsync("Hello!", session);

        Assert.NotNull(response);
        // Tool was called exactly once.
        Assert.Equal(1, recorder.CallCount);

        // Workflow history should show at least 2 RunAgentStep schedules and 1 InvokeFunction schedule.
        var sessionId = ((Temporalio.Extensions.Agents.Session.TemporalAgentSession)session).SessionId;
        var handle = _env.Client.GetWorkflowHandle(sessionId.WorkflowId);
        var activityNames = await CollectActivityNamesAsync(handle);

        var stepCount = activityNames.Count(n => n == "Temporalio.Extensions.Agents.RunAgentStep");
        var toolCount = activityNames.Count(n => n == "Temporalio.Extensions.AI.InvokeFunction");

        Assert.True(stepCount >= 2, $"expected >= 2 RunAgentStep activities; got {stepCount}");
        Assert.Equal(1, toolCount);

        await host.StopAsync();
    }

    // ── Test 2.4: three parallel tool calls scheduled before any completes ──────

    [Fact]
    public async Task StepMode_ThreeParallelToolCalls_AllScheduledBeforeAnyCompletes()
    {
        // Three tool calls in one assistant turn must be dispatched in parallel via
        // Workflow.WhenAllAsync. Pin via Temporal history: assert all three
        // ActivityTaskScheduled events for InvokeFunction precede the first
        // ActivityTaskCompleted event for InvokeFunction.
        var recorder = new RecordingTool { Name = "echo_tool" };
        var aiFunction = recorder.Build();

        var toolCalls = new List<FunctionCallContent>
        {
            new("call-A", "echo_tool", new Dictionary<string, object?> { ["input"] = "A" }),
            new("call-B", "echo_tool", new Dictionary<string, object?> { ["input"] = "B" }),
            new("call-C", "echo_tool", new Dictionary<string, object?> { ["input"] = "C" }),
        };
        var scripted = ScriptedChatClient.WithToolCallsThenFinal(toolCalls, "Final.");

        using var host = BuildHost(stepMode: true, scriptedResponses: scripted, tools: [aiFunction]);
        await host.StartAsync();

        var proxy = host.Services.GetTemporalAgentProxy("StepAgent");
        var session = await proxy.CreateSessionAsync();
        var response = await proxy.RunAsync("Hello!", session);
        Assert.NotNull(response);

        Assert.Equal(3, recorder.CallCount);

        // History inspection: collect indices of InvokeFunction ActivityTaskScheduled and
        // the first ActivityTaskCompleted of the same type. Assert all three schedules
        // precede the first completion.
        var sessionId = ((Temporalio.Extensions.Agents.Session.TemporalAgentSession)session).SessionId;
        var handle = _env.Client.GetWorkflowHandle(sessionId.WorkflowId);
        var (scheduleIndices, firstCompleteIndex) = await CollectInvokeFunctionScheduleVsCompleteAsync(handle);

        Assert.Equal(3, scheduleIndices.Count);
        Assert.True(firstCompleteIndex >= 0, "expected at least one ActivityTaskCompleted event");
        foreach (var idx in scheduleIndices)
        {
            Assert.True(idx < firstCompleteIndex,
                $"schedule event at {idx} did not precede first complete at {firstCompleteIndex} — fan-out is sequential");
        }

        await host.StopAsync();
    }

    // ── Test 2.5: write-tool with MaximumAttempts=1 does not retry ──────────────

    [Fact]
    public async Task StepMode_WriteToolWithMaxAttemptsOne_DoesNotRetry()
    {
        var recorder = new RecordingTool { Name = "send_email", Behavior = RecordingToolBehavior.AlwaysFail };
        var aiFunction = recorder.Build();
        var fc = new FunctionCallContent("call-1", "send_email",
            new Dictionary<string, object?> { ["input"] = "hello" });
        // Even though the tool fails, we still script a final response in case the
        // workflow recovers. With MaxAttempts=1 the workflow propagates the failure.
        var scripted = ScriptedChatClient.WithToolCallsThenFinal([fc], "Final.");

        var perToolOpts = new Dictionary<string, ActivityOptions>
        {
            ["send_email"] = new ActivityOptions
            {
                StartToCloseTimeout = TimeSpan.FromSeconds(10),
                RetryPolicy = new RetryPolicy { MaximumAttempts = 1 },
            },
        };

        using var host = BuildHost(
            stepMode: true,
            scriptedResponses: scripted,
            tools: [aiFunction],
            perToolActivityOptions: perToolOpts);
        await host.StartAsync();

        var proxy = host.Services.GetTemporalAgentProxy("StepAgent");
        var session = await proxy.CreateSessionAsync();

        // The workflow should surface the failure to the caller; we don't care which exception
        // type — just that exactly one tool call happened and the call failed.
        await Assert.ThrowsAnyAsync<Exception>(async () =>
            await proxy.RunAsync("Hi", session));

        Assert.Equal(1, recorder.CallCount);
        await host.StopAsync();
    }

    // ── Test 2.8: loop iteration cap exits with structured error ────────────────

    [Fact]
    public async Task StepMode_RunawayToolCalls_ExitsAtIterationCap()
    {
        var recorder = new RecordingTool { Name = "loop_tool" };
        var aiFunction = recorder.Build();

        // Build a scripted client that ALWAYS returns a tool call — never converges.
        var responses = new List<ChatResponse>();
        for (var i = 0; i < 50; i++)
        {
            var fc = new FunctionCallContent($"call-{i}", "loop_tool",
                new Dictionary<string, object?> { ["input"] = "go" });
            responses.Add(new ChatResponse(new ChatMessage(ChatRole.Assistant, [fc])));
        }
        var scripted = new ScriptedChatClient(responses);

        // Set a small iteration cap to keep the test fast.
        const int cap = 3;
        using var host = BuildHost(
            stepMode: true,
            scriptedResponses: scripted,
            tools: [aiFunction],
            maxToolCallsPerTurn: cap);
        await host.StartAsync();

        var proxy = host.Services.GetTemporalAgentProxy("StepAgent");
        var session = await proxy.CreateSessionAsync();
        var response = await proxy.RunAsync("Hi", session);

        // Tool was called the cap number of times (one per iteration before the LLM was about
        // to be called for the (cap+1)-th time and the loop exited).
        Assert.Equal(cap, recorder.CallCount);

        // The final assistant message contains the structured error message.
        var finalText = response.Messages[^1].Text;
        Assert.Contains("Maximum tool-call iterations", finalText, StringComparison.Ordinal);

        await host.StopAsync();
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    private static async Task<List<string>> CollectActivityNamesAsync(WorkflowHandle handle)
    {
        var activityNames = new List<string>();
        await foreach (var ev in handle.FetchHistoryEventsAsync())
        {
            if (ev.ActivityTaskScheduledEventAttributes is { } a)
            {
                activityNames.Add(a.ActivityType.Name);
            }
        }
        return activityNames;
    }

    private static async Task<(List<int> ScheduleIndices, int FirstCompleteIndex)>
        CollectInvokeFunctionScheduleVsCompleteAsync(WorkflowHandle handle)
    {
        const string invokeFunction = "Temporalio.Extensions.AI.InvokeFunction";
        var schedules = new List<int>();
        var firstComplete = -1;
        // Map ScheduledEventId → activity type so we can identify completion events.
        var scheduledIdToType = new Dictionary<long, string>();
        var index = 0;
        await foreach (var ev in handle.FetchHistoryEventsAsync())
        {
            if (ev.ActivityTaskScheduledEventAttributes is { } a)
            {
                scheduledIdToType[ev.EventId] = a.ActivityType.Name;
                if (a.ActivityType.Name == invokeFunction)
                {
                    schedules.Add(index);
                }
            }
            else if (firstComplete < 0 && ev.ActivityTaskCompletedEventAttributes is { } c)
            {
                if (scheduledIdToType.TryGetValue(c.ScheduledEventId, out var typeName)
                    && typeName == invokeFunction)
                {
                    firstComplete = index;
                }
            }
            index++;
        }
        return (schedules, firstComplete);
    }

    private IHost BuildHost(
        bool stepMode,
        ScriptedChatClient? scriptedResponses,
        IEnumerable<AIFunction> tools,
        Dictionary<string, ActivityOptions>? perToolActivityOptions = null,
        int? maxToolCallsPerTurn = null)
    {
        // Unique task queue per test so each host's worker only sees its own tools/agents.
        var taskQueue = $"step-mode-{Guid.NewGuid():N}";
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<ITemporalClient>(_env.Client);

        var workerBuilder = builder.Services.AddHostedTemporalWorker(taskQueue);

        if (stepMode)
        {
            // Register IChatClient (the scripted one) so RunAgentStepAsync can resolve it.
            // Using AddSingleton is sufficient — DI resolution path matches the agent activity.
            builder.Services.AddSingleton<IChatClient>(scriptedResponses!);

            workerBuilder.AddDurableAI();
            workerBuilder.AddDurableTools([.. tools]);

            workerBuilder.AddTemporalAgents(opts =>
            {
                opts.EnablePerToolActivities = true;
                if (perToolActivityOptions is not null)
                {
                    opts.PerToolActivityOptions = perToolActivityOptions;
                }
                if (maxToolCallsPerTurn is int max)
                {
                    opts.MaxToolCallsPerTurn = max;
                }

                // Register the agent. We use a ChatClientAgent factory that uses the same
                // scripted chat client so its Instructions are known to the step activity
                // (though the step activity resolves IChatClient from DI directly).
                opts.AddAIAgentFactory("StepAgent", _ =>
                    new ChatClientAgent(
                        scriptedResponses!,
                        new ChatClientAgentOptions
                        {
                            Name = "StepAgent",
                            ChatOptions = new ChatOptions { Instructions = "You are a helpful agent." },
                            UseProvidedChatClientAsIs = true,
                        }));
            });
        }
        else
        {
            workerBuilder.AddTemporalAgents(opts =>
                opts.AddAIAgent(new EchoAIAgent("EchoAgent")));
        }

        return builder.Build();
    }
}

/// <summary>
/// Class fixture: starts a single embedded Temporal server shared by all tests in
/// <see cref="StepModeIntegrationTests"/>. Mirrors the lifetime pattern of
/// <c>IntegrationTestFixture</c> so the suite avoids the per-test
/// <c>WorkflowEnvironment.StartLocalAsync</c> that contributes to flaky parallel-run
/// resource contention.
/// </summary>
public sealed class StepModeEnvironmentFixture : IAsyncLifetime
{
    public WorkflowEnvironment Environment { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        Environment = await TestEnvironmentHelper.StartLocalAsync();
        // The MAF-aware data converter is required so AgentSession{Request,Response}
        // discriminators round-trip through the workflow → activity payload boundary.
        Environment.Client.Options.DataConverter = TemporalAgentDataConverter.Instance;
    }

    public Task DisposeAsync() => Environment.ShutdownAsync();
}
