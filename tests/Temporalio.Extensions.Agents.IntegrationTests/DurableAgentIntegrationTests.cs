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
/// End-to-end integration coverage for v0.3 durable agents registered via
/// <c>TemporalAgentsOptions.AddDurableAgent</c>. Exercises the new workflow loop:
/// <c>Temporalio.Extensions.Agents.RunDurableAgentStep</c> for the LLM call and
/// <c>Temporalio.Extensions.Agents.InvokeAgentTool</c> per tool dispatch (parallel fan-out via
/// <c>Workflow.WhenAllAsync</c>).
/// </summary>
[Trait("Category", "Integration")]
public class DurableAgentIntegrationTests : IClassFixture<DurableAgentEnvironmentFixture>
{
    private readonly DurableAgentEnvironmentFixture _fixture;
    private WorkflowEnvironment _env => _fixture.Environment;

    private const string RunDurableAgentStepActivity = "Temporalio.Extensions.Agents.RunDurableAgentStep";
    private const string InvokeAgentToolActivity = "Temporalio.Extensions.Agents.InvokeAgentTool";

    public DurableAgentIntegrationTests(DurableAgentEnvironmentFixture fixture)
    {
        _fixture = fixture;
    }

    // ── Single turn, no tool calls ──────────────────────────────────────────────

    [Fact]
    public async Task DurableAgent_SingleTurnNoTools_ReturnsResponse()
    {
        var scripted = new ScriptedChatClient([
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "Final answer.")),
        ]);

        using var host = BuildHost(scripted, tools: [], configureAgent: null);
        await host.StartAsync();

        var proxy = host.Services.GetTemporalAgentProxy("DurableAgent");
        var session = await proxy.CreateSessionAsync();
        var response = await proxy.RunAsync("Hi", session);

        Assert.NotNull(response);
        Assert.Contains("Final answer.", response.Messages[^1].Text);

        var sessionId = ((TemporalAgentSession)session).SessionId;
        var handle = _env.Client.GetWorkflowHandle(sessionId.WorkflowId);
        var activityNames = await CollectActivityNamesAsync(handle);

        Assert.Contains(RunDurableAgentStepActivity, activityNames);
        Assert.DoesNotContain(InvokeAgentToolActivity, activityNames);

        await host.StopAsync();
    }

    // ── Single tool call → final answer ─────────────────────────────────────────

    [Fact]
    public async Task DurableAgent_SingleToolCall_DispatchesToolActivity()
    {
        var recorder = new RecordingTool { Name = "echo_tool" };
        var aiFunction = recorder.Build();
        var fc = new FunctionCallContent("call-1", "echo_tool",
            new Dictionary<string, object?> { ["input"] = "hello" });
        var scripted = ScriptedChatClient.WithToolCallsThenFinal([fc], "All done.");

        using var host = BuildHost(scripted, tools: [aiFunction], configureAgent: null);
        await host.StartAsync();

        var proxy = host.Services.GetTemporalAgentProxy("DurableAgent");
        var session = await proxy.CreateSessionAsync();
        var response = await proxy.RunAsync("Hi", session);

        Assert.NotNull(response);
        Assert.Equal(1, recorder.CallCount);

        var sessionId = ((TemporalAgentSession)session).SessionId;
        var handle = _env.Client.GetWorkflowHandle(sessionId.WorkflowId);
        var activityNames = await CollectActivityNamesAsync(handle);

        var stepCount = activityNames.Count(n => n == RunDurableAgentStepActivity);
        var toolCount = activityNames.Count(n => n == InvokeAgentToolActivity);

        Assert.True(stepCount >= 2, $"expected >= 2 RunDurableAgentStep activities; got {stepCount}");
        Assert.Equal(1, toolCount);
        // Sanity: legacy step-mode path should NOT have run.
        Assert.DoesNotContain("Temporalio.Extensions.Agents.RunAgentStep", activityNames);

        await host.StopAsync();
    }

    // ── Three parallel tool calls dispatched concurrently ───────────────────────

    [Fact]
    public async Task DurableAgent_ParallelToolCalls_DispatchesConcurrently()
    {
        var recorder = new RecordingTool { Name = "echo_tool" };
        var aiFunction = recorder.Build();

        var toolCalls = new List<FunctionCallContent>
        {
            new("call-A", "echo_tool", new Dictionary<string, object?> { ["input"] = "A" }),
            new("call-B", "echo_tool", new Dictionary<string, object?> { ["input"] = "B" }),
            new("call-C", "echo_tool", new Dictionary<string, object?> { ["input"] = "C" }),
        };
        var scripted = ScriptedChatClient.WithToolCallsThenFinal(toolCalls, "Final.");

        using var host = BuildHost(scripted, tools: [aiFunction], configureAgent: null);
        await host.StartAsync();

        var proxy = host.Services.GetTemporalAgentProxy("DurableAgent");
        var session = await proxy.CreateSessionAsync();
        var response = await proxy.RunAsync("Hi", session);
        Assert.NotNull(response);

        Assert.Equal(3, recorder.CallCount);

        // Inspect history: assert all three InvokeAgentTool ActivityTaskScheduled events precede
        // the first ActivityTaskCompleted of the same type — proving Workflow.WhenAllAsync
        // dispatched them in parallel.
        var sessionId = ((TemporalAgentSession)session).SessionId;
        var handle = _env.Client.GetWorkflowHandle(sessionId.WorkflowId);
        var (scheduleIndices, firstCompleteIndex) =
            await CollectScheduleVsCompleteAsync(handle, InvokeAgentToolActivity);

        Assert.Equal(3, scheduleIndices.Count);
        Assert.True(firstCompleteIndex >= 0, "expected at least one ActivityTaskCompleted event");
        foreach (var idx in scheduleIndices)
        {
            Assert.True(idx < firstCompleteIndex,
                $"schedule event at {idx} did not precede first complete at {firstCompleteIndex} — fan-out is sequential");
        }

        await host.StopAsync();
    }

    // ── Iteration cap returns structured error response ─────────────────────────

    [Fact]
    public async Task DurableAgent_HitsIterationCap_ReturnsStructuredError()
    {
        var recorder = new RecordingTool { Name = "loop_tool" };
        var aiFunction = recorder.Build();

        // Scripted client always returns a tool call — never converges on a final answer.
        var responses = new List<ChatResponse>();
        for (var i = 0; i < 50; i++)
        {
            var fc = new FunctionCallContent($"call-{i}", "loop_tool",
                new Dictionary<string, object?> { ["input"] = "go" });
            responses.Add(new ChatResponse(new ChatMessage(ChatRole.Assistant, [fc])));
        }
        var scripted = new ScriptedChatClient(responses);

        const int cap = 3;
        using var host = BuildHost(scripted, tools: [aiFunction], configureAgent: agent =>
        {
            agent.MaxToolCallsPerTurn = cap;
        });
        await host.StartAsync();

        var proxy = host.Services.GetTemporalAgentProxy("DurableAgent");
        var session = await proxy.CreateSessionAsync();
        var response = await proxy.RunAsync("Hi", session);

        Assert.Equal(cap, recorder.CallCount);

        var finalText = response.Messages[^1].Text;
        Assert.Contains("Maximum tool-call iterations", finalText, StringComparison.Ordinal);

        await host.StopAsync();
    }

    // ── Write tool with NoRetry never re-fires on activity failure ──────────────

    [Fact]
    public async Task DurableAgent_WriteToolWithNoRetry_DoesNotRetry()
    {
        var recorder = new RecordingTool
        {
            Name = "send_email",
            Behavior = RecordingToolBehavior.AlwaysFail,
        };
        var aiFunction = recorder.Build();
        var fc = new FunctionCallContent("call-1", "send_email",
            new Dictionary<string, object?> { ["input"] = "draft" });
        // The final response is never reached because the tool fails. We still script it for the
        // case where the workflow would recover.
        var scripted = ScriptedChatClient.WithToolCallsThenFinal([fc], "Done.");

        using var host = BuildHost(scripted, tools: [aiFunction], configureAgent: agent =>
        {
            // Re-register the tool with NoRetry. We must remove + re-add since BuildHost
            // already added it without overrides. Easier path: provide a configure callback
            // that adds the tool through the builder directly instead of using the BuildHost
            // tools parameter. Re-routing through the configure callback below.
        }, registerToolsViaBuilder: builder =>
        {
            builder.AddTool(aiFunction, opts => opts.NoRetry());
        });
        await host.StartAsync();

        var proxy = host.Services.GetTemporalAgentProxy("DurableAgent");
        var session = await proxy.CreateSessionAsync();

        // Tool failure surfaces to caller; we don't pin the exact exception type.
        await Assert.ThrowsAnyAsync<Exception>(async () => await proxy.RunAsync("Hi", session));

        Assert.Equal(1, recorder.CallCount);

        // Defensive: confirm exactly one InvokeAgentTool ActivityTaskScheduled event.
        var sessionId = ((TemporalAgentSession)session).SessionId;
        var handle = _env.Client.GetWorkflowHandle(sessionId.WorkflowId);
        var invokeAgentToolScheduleCount = 0;
        await foreach (var ev in handle.FetchHistoryEventsAsync())
        {
            if (ev.ActivityTaskScheduledEventAttributes is { } a &&
                a.ActivityType.Name == InvokeAgentToolActivity)
            {
                invokeAgentToolScheduleCount++;
            }
        }
        Assert.Equal(1, invokeAgentToolScheduleCount);

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
        CollectScheduleVsCompleteAsync(WorkflowHandle handle, string activityTypeName)
    {
        var schedules = new List<int>();
        var firstComplete = -1;
        var scheduledIdToType = new Dictionary<long, string>();
        var index = 0;
        await foreach (var ev in handle.FetchHistoryEventsAsync())
        {
            if (ev.ActivityTaskScheduledEventAttributes is { } a)
            {
                scheduledIdToType[ev.EventId] = a.ActivityType.Name;
                if (a.ActivityType.Name == activityTypeName)
                {
                    schedules.Add(index);
                }
            }
            else if (firstComplete < 0 && ev.ActivityTaskCompletedEventAttributes is { } c)
            {
                if (scheduledIdToType.TryGetValue(c.ScheduledEventId, out var typeName)
                    && typeName == activityTypeName)
                {
                    firstComplete = index;
                }
            }
            index++;
        }
        return (schedules, firstComplete);
    }

    private IHost BuildHost(
        ScriptedChatClient scripted,
        IEnumerable<AIFunction> tools,
        Action<DurableAgentBuilder>? configureAgent,
        Action<DurableAgentBuilder>? registerToolsViaBuilder = null)
    {
        var taskQueue = $"durable-agent-{Guid.NewGuid():N}";
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<ITemporalClient>(_env.Client);
        builder.Services.AddSingleton<IChatClient>(scripted);

        var workerBuilder = builder.Services.AddHostedTemporalWorker(taskQueue);

        workerBuilder.AddTemporalAgents(opts =>
        {
            opts.AddDurableAgent("DurableAgent", agent =>
            {
                agent.Instructions = "You are a helpful agent.";
                agent.ChatClient = sp => sp.GetRequiredService<IChatClient>();

                if (registerToolsViaBuilder is not null)
                {
                    registerToolsViaBuilder(agent);
                }
                else
                {
                    foreach (var tool in tools)
                    {
                        agent.AddTool(tool);
                    }
                }

                configureAgent?.Invoke(agent);
            });
        });

        return builder.Build();
    }
}

/// <summary>
/// Class fixture: starts a single embedded Temporal server shared by all tests in
/// <see cref="DurableAgentIntegrationTests"/>.
/// </summary>
public sealed class DurableAgentEnvironmentFixture : IAsyncLifetime
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
