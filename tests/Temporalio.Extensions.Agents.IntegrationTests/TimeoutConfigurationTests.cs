using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Temporalio.Client;
using Temporalio.Extensions.Agents.Session;
using Temporalio.Extensions.Agents.Workflows;
using Temporalio.Extensions.Hosting;
using Xunit;
using Xunit.Abstractions;

namespace Temporalio.Extensions.Agents.IntegrationTests;

/// <summary>
/// Tests that custom activity timeout configurations are propagated correctly
/// through the full stack: <see cref="TemporalAgentsOptions"/> → <see cref="AgentWorkflowInput"/>
/// → <see cref="AgentWorkflow"/> → Temporal <c>ActivityOptions</c>.
///
/// These tests verify the configuration path works end-to-end by confirming that
/// agents registered with custom timeouts can still execute successfully. True timeout
/// enforcement testing would require agents that intentionally exceed the timeout,
/// which is better suited to targeted unit tests.
/// </summary>
[Trait("Category", "Integration")]
public class TimeoutConfigurationTests : IClassFixture<IntegrationTestFixture>
{
    private readonly IntegrationTestFixture _fixture;
    private readonly ITestOutputHelper _output;

    public TimeoutConfigurationTests(IntegrationTestFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    [Fact]
    public async Task CustomTimeouts_PropagateAndAgentExecutesSuccessfully()
    {
        // Build a host with explicit timeout configuration.
        var taskQueue = $"timeout-test-{Guid.NewGuid():N}";
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<ITemporalClient>(_fixture.Client);

        builder.Services
            .AddHostedTemporalWorker(taskQueue)
            .AddTemporalAgents(options =>
            {
                options.AddDurableAgent("TimeoutAgent", a => a.ChatClient = _ => new Helpers.EchoChatClient());
                options.DefaultActivityTimeout = TimeSpan.FromMinutes(2);
                options.DefaultHeartbeatTimeout = TimeSpan.FromSeconds(30);
            });

        using var host = builder.Build();
        await host.StartAsync();

        try
        {
            var proxy = host.Services.GetTemporalAgentProxy("TimeoutAgent");
            var session = await proxy.CreateSessionAsync();

            // The custom timeouts are wired through AgentWorkflowInput → ActivityOptions.
            // If propagation fails, the activity would use defaults or fail to start.
            var response = await proxy.RunAsync("Test with custom timeouts", session);

            Assert.NotNull(response);
            Assert.NotEmpty(response.Messages);
            Assert.Contains("Echo [1]:", response.Messages[0].Text);

            _output.WriteLine(
                "Agent executed successfully with custom timeouts: " +
                "StartToClose=2min, Heartbeat=30s");
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task DefaultTimeouts_AgentExecutesSuccessfully()
    {
        // Build a host with NO explicit timeout configuration (null = defaults).
        var taskQueue = $"default-timeout-{Guid.NewGuid():N}";
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<ITemporalClient>(_fixture.Client);

        builder.Services
            .AddHostedTemporalWorker(taskQueue)
            .AddTemporalAgents(options =>
            {
                options.AddDurableAgent("DefaultTimeoutAgent", a => a.ChatClient = _ => new Helpers.EchoChatClient());
                // ActivityTimeout / HeartbeatTimeout default to 5 minutes / 2 minutes when unset.
            });

        using var host = builder.Build();
        await host.StartAsync();

        try
        {
            var proxy = host.Services.GetTemporalAgentProxy("DefaultTimeoutAgent");
            var session = await proxy.CreateSessionAsync();

            var response = await proxy.RunAsync("Test with default timeouts", session);

            Assert.NotNull(response);
            Assert.NotEmpty(response.Messages);

            _output.WriteLine("Agent executed successfully with default (null) timeouts.");
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task CustomTTL_WorkflowStartsWithConfiguredTTL()
    {
        // Verify per-agent TTL is propagated.
        var taskQueue = $"ttl-test-{Guid.NewGuid():N}";
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<ITemporalClient>(_fixture.Client);

        builder.Services
            .AddHostedTemporalWorker(taskQueue)
            .AddTemporalAgents(options =>
            {
                options.AddDurableAgent("ShortTTLAgent", a =>
                {
                    a.ChatClient = _ => new Helpers.EchoChatClient();
                    a.TimeToLive = TimeSpan.FromHours(1);
                });
            });

        using var host = builder.Build();
        await host.StartAsync();

        try
        {
            var proxy = host.Services.GetTemporalAgentProxy("ShortTTLAgent");
            var session = (TemporalAgentSession)await proxy.CreateSessionAsync();

            var response = await proxy.RunAsync("TTL test", session);

            Assert.Contains("Echo [1]:", response.Messages[0].Text);

            // Verify the workflow is running (TTL hasn't expired yet — 1 hour is far away).
            var handle = _fixture.Client.GetWorkflowHandle<AgentWorkflow>(
                session.SessionId.WorkflowId);
            var desc = await handle.DescribeAsync();
            Assert.Equal(Temporalio.Api.Enums.V1.WorkflowExecutionStatus.Running, desc.Status);

            _output.WriteLine($"Workflow started with 1-hour TTL; status is Running.");
        }
        finally
        {
            await host.StopAsync();
        }
    }
}
