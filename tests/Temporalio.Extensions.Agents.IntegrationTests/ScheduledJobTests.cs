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
using Xunit;

namespace Temporalio.Extensions.Agents.IntegrationTests;

/// <summary>
/// Integration coverage for scheduled / deferred agent jobs (<see cref="AgentJobWorkflow"/>).
/// Verifies bug P1-4: write tools registered with <c>opts.NoRetry()</c> must use
/// <c>MaximumAttempts = 1</c> in <c>InvokeAgentTool</c> activities dispatched from
/// <see cref="AgentJobWorkflow"/>. Before the fix, <see cref="AgentJobInput"/> had no
/// <c>DurableAgentToolActivityOptions</c> field and always used the flat job-level
/// <c>RetryPolicy</c> (unbounded retries) for every tool.
/// </summary>
[Trait("Category", "Integration")]
public class ScheduledJobTests
{
    private const string InvokeAgentToolActivity = "Temporalio.Extensions.Agents.InvokeAgentTool";

    /// <summary>
    /// P1-4: A write tool registered with <c>opts.NoRetry()</c> must produce an
    /// <c>InvokeAgentTool</c> <c>ActivityTaskScheduled</c> event with
    /// <c>RetryPolicy.MaximumAttempts == 1</c> when dispatched from <see cref="AgentJobWorkflow"/>.
    /// </summary>
    [Fact]
    public async Task ScheduledJob_WriteToolWithNoRetry_UsesMaximumAttempts1()
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

        var taskQueue = $"scheduled-job-noretry-{Guid.NewGuid():N}";

        using var workerHost = BuildWorkerHost(env.Client, scripted, taskQueue,
            registerToolsViaBuilder: builder =>
            {
                builder.AddTool(aiFunction, opts => opts.NoRetry());
            });
        await workerHost.StartAsync();

        try
        {
            var agentClient = workerHost.Services.GetRequiredService<ITemporalAgentClient>();

            // Build the AgentJobInput directly by starting an AgentJobWorkflow.
            // We use a direct workflow start rather than ScheduleAgentAsync because test
            // environments lack a schedule service; a direct workflow start exercises the
            // same AgentJobWorkflow code path and lets us inspect the history.
            var workflowId = $"ta-write-record-scheduled-{Guid.NewGuid():N}";
            var request = new RunRequest("Process this record.");

            // Build AgentJobInput manually with the per-tool options populated.
            // DefaultTemporalAgentClient.BuildDurableAgentToolActivityOptions is now internal static
            // and accessible from within the same assembly. For the integration test we replicate
            // what ScheduleAgentAsync does: look up the registration and build the dict.
            var agentsOptions = workerHost.Services.GetRequiredService<TemporalAgentsOptions>();
            var defaultTimeout = agentsOptions.DefaultActivityTimeout;
            var defaultHeartbeat = agentsOptions.DefaultHeartbeatTimeout;
            var defaultRetry = agentsOptions.DefaultRetryPolicy;

            Dictionary<string, Temporalio.Workflows.ActivityOptions>? toolActivityOptions = null;
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

            // Start the AgentJobWorkflow directly.
            var handle = await env.Client.StartWorkflowAsync(
                (AgentJobWorkflow wf) => wf.RunAsync(jobInput),
                new Temporalio.Client.WorkflowOptions(workflowId, taskQueue));

            // Tool always fails — the workflow will error.
            try
            {
                await handle.GetResultAsync();
            }
            catch
            {
                // Expected: tool fails and the workflow errors.
            }

            // Inspect history: InvokeAgentTool must have MaximumAttempts=1.
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

    private static IHost BuildWorkerHost(
        ITemporalClient client,
        ScriptedChatClient scripted,
        string taskQueue,
        Action<DurableAgentBuilder>? registerToolsViaBuilder = null)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton(client);
        builder.Services.AddSingleton<IChatClient>(scripted);

        builder.Services
            .AddHostedTemporalWorker(taskQueue)
            .AddTemporalAgents(opts =>
            {
                opts.AddDurableAgent("DurableAgent", agent =>
                {
                    agent.Instructions = "You are a helpful agent.";
                    agent.ChatClient = sp => sp.GetRequiredService<IChatClient>();
                    registerToolsViaBuilder?.Invoke(agent);
                });
            });

        return builder.Build();
    }
}
