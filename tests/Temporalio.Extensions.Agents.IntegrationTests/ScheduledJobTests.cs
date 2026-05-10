using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Temporalio.Client;
using Temporalio.Client.Schedules;
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
/// Integration coverage for scheduled / deferred agent jobs (<see cref="AgentJobWorkflow"/>).
/// </summary>
[Trait("Category", "Integration")]
public class ScheduledJobTests : IClassFixture<ScheduledJobEnvironmentFixture>
{
    private readonly ScheduledJobEnvironmentFixture _fixture;
    private WorkflowEnvironment _env => _fixture.Environment;

    private const string InvokeAgentToolActivity = "Temporalio.Extensions.Agents.InvokeAgentTool";

    public ScheduledJobTests(ScheduledJobEnvironmentFixture fixture)
    {
        _fixture = fixture;
    }

    /// <summary>
    /// P1-4: A write tool registered with <c>opts.NoRetry()</c> must produce an
    /// <c>InvokeAgentTool</c> <c>ActivityTaskScheduled</c> event with
    /// <c>RetryPolicy.MaximumAttempts == 1</c> when dispatched from <see cref="AgentJobWorkflow"/>.
    /// </summary>
    [Fact]
    public async Task ScheduledJob_WriteToolWithNoRetry_UsesMaximumAttempts1()
    {
        var recorder = new RecordingTool
        {
            Name = "write_record",
            Behavior = RecordingToolBehavior.AlwaysFail,
        };
        var aiFunction = recorder.Build();

        var fc = new FunctionCallContent("call-1", "write_record",
            new Dictionary<string, object?> { ["input"] = "data" });
        var scripted = ScriptedChatClient.WithToolCallsThenFinal([fc], "Done.");

        var taskQueue = $"scheduled-job-noretry-{Guid.NewGuid():N}";

        using var workerHost = BuildWorkerHost(scripted, taskQueue,
            registerToolsViaBuilder: builder =>
            {
                builder.AddTool(aiFunction, opts => opts.NoRetry());
            });
        await workerHost.StartAsync();

        try
        {
            var workflowId = $"ta-write-record-scheduled-{Guid.NewGuid():N}";
            var request = new RunRequest("Process this record.");

            var agentsOptions = workerHost.Services.GetRequiredService<TemporalAgentsOptions>();
            var defaultTimeout = agentsOptions.DefaultActivityTimeout;
            var defaultHeartbeat = agentsOptions.DefaultHeartbeatTimeout;
            var defaultRetry = agentsOptions.DefaultRetryPolicy;

            Dictionary<string, ActivityOptions>? toolActivityOptions = null;
            if (agentsOptions.DurableAgentRegistrations.TryGetValue("DurableAgent", out var reg))
            {
                toolActivityOptions = DefaultTemporalAgentClient.BuildDurableAgentToolActivityOptions(
                    reg,
                    reg.ActivityTimeout ?? defaultTimeout,
                    reg.HeartbeatTimeout ?? defaultHeartbeat,
                    reg.RetryPolicy ?? defaultRetry);
            }

            var jobInput = new AgentJobInput
            {
                AgentName = "DurableAgent",
                TaskQueue = taskQueue,
                Request = request,
                ActivityTimeout = defaultTimeout,
                HeartbeatTimeout = defaultHeartbeat,
                RetryPolicy = defaultRetry,
                DurableAgentToolActivityOptions = toolActivityOptions,
            };

            var handle = await _env.Client.StartWorkflowAsync(
                (AgentJobWorkflow wf) => wf.RunAsync(jobInput),
                new WorkflowOptions(workflowId, taskQueue));

            try
            {
                await handle.GetResultAsync();
            }
            catch
            {
                // Expected: tool always fails, workflow errors.
            }

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

            Assert.True(foundToolSchedule,
                "Expected at least one InvokeAgentTool ActivityTaskScheduled event in the job workflow.");
        }
        finally
        {
            await workerHost.StopAsync();
        }
    }

    /// <summary>
    /// Verifies that <see cref="AgentJobWorkflow"/> stops dispatching
    /// <c>InvokeAgentTool</c> activities once it has run <c>MaxToolCallsPerTurn</c> iterations.
    /// </summary>
    [Fact]
    public async Task AgentJobWorkflow_RespectsMaxToolCallsPerTurnFromInput()
    {
        const int maxToolCalls = 2;

        var responses = new List<ChatResponse>();
        for (var i = 0; i < 50; i++)
        {
            var fc = new FunctionCallContent($"call-{i}", "cap_tool",
                new Dictionary<string, object?> { ["input"] = "go" });
            responses.Add(new ChatResponse(new ChatMessage(ChatRole.Assistant, [fc])));
        }

        var scripted = new ScriptedChatClient(responses);
        var recorder = new RecordingTool { Name = "cap_tool" };
        var aiFunction = recorder.Build();

        var taskQueue = $"scheduled-job-{Guid.NewGuid():N}";
        using var host = BuildWorkerHost(scripted, taskQueue,
            registerToolsViaBuilder: builder => builder.AddTool(aiFunction),
            agentName: "CapAgent");
        await host.StartAsync();

        var workflowId = $"ta-capagent-captest{Guid.NewGuid():N}";
        var jobInput = new AgentJobInput
        {
            AgentName = "CapAgent",
            TaskQueue = taskQueue,
            Request = new RunRequest("Run until cap."),
            ActivityTimeout = TimeSpan.FromSeconds(30),
            HeartbeatTimeout = TimeSpan.FromSeconds(10),
            MaxToolCallsPerTurn = maxToolCalls,
        };

        var handle = await _env.Client.StartWorkflowAsync(
            (AgentJobWorkflow wf) => wf.RunAsync(jobInput),
            new WorkflowOptions(workflowId, taskQueue));

        await handle.GetResultAsync();

        var toolScheduleCount = 0;
        await foreach (var ev in handle.FetchHistoryEventsAsync())
        {
            if (ev.ActivityTaskScheduledEventAttributes is { } a &&
                a.ActivityType.Name == InvokeAgentToolActivity)
            {
                toolScheduleCount++;
            }
        }

        Assert.Equal(maxToolCalls, toolScheduleCount);

        await host.StopAsync();
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private IHost BuildWorkerHost(
        ScriptedChatClient scripted,
        string taskQueue,
        Action<DurableAgentBuilder>? registerToolsViaBuilder = null,
        string agentName = "DurableAgent")
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<ITemporalClient>(_env.Client);
        builder.Services.AddSingleton<IChatClient>(scripted);

        builder.Services
            .AddHostedTemporalWorker(taskQueue)
            .AddTemporalAgents(opts =>
            {
                opts.AddDurableAgent(agentName, agent =>
                {
                    agent.Instructions = "You are a helpful agent.";
                    agent.ChatClient = sp => sp.GetRequiredService<IChatClient>();
                    registerToolsViaBuilder?.Invoke(agent);
                });
            });

        return builder.Build();
    }
}

/// <summary>
/// Shared embedded Temporal server fixture for <see cref="ScheduledJobTests"/>.
/// </summary>
public sealed class ScheduledJobEnvironmentFixture : IAsyncLifetime
{
    public WorkflowEnvironment Environment { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        Environment = await TestEnvironmentHelper.StartLocalAsync();
        Environment.Client.Options.DataConverter = TemporalAgentDataConverter.Instance;
    }

    public Task DisposeAsync() => Environment.ShutdownAsync();
}
