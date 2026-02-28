using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Temporalio.Client;
using Temporalio.Extensions.Agents.IntegrationTests.Helpers;
using Xunit;
using Xunit.Abstractions;

namespace Temporalio.Extensions.Agents.IntegrationTests;

/// <summary>
/// Tests for error handling, activity timeouts, heartbeat timeouts, and retry behaviour.
///
/// These tests use specialized agents (<see cref="SlowThenFastAIAgent"/>,
/// <see cref="FailThenSucceedAIAgent"/>) that intentionally fail or delay on
/// the first call and succeed on retry. This lets us verify that Temporal's
/// retry mechanism works correctly through the full library stack.
/// </summary>
[Trait("Category", "Integration")]
public class ErrorHandlingTests : IClassFixture<IntegrationTestFixture>
{
    private readonly IntegrationTestFixture _fixture;
    private readonly ITestOutputHelper _output;

    public ErrorHandlingTests(IntegrationTestFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    // ── Activity StartToClose Timeout ────────────────────────────────────────

    [Fact]
    public async Task ActivityStartToCloseTimeout_RetriesAndEventuallySucceeds()
    {
        // Agent delays 60s on first call; activity timeout is 2s.
        // First attempt: agent starts delaying → 2s timeout fires → activity cancelled → retry.
        // Second attempt: agent returns immediately → success.
        var taskQueue = $"start-to-close-test-{Guid.NewGuid():N}";
        var agent = new SlowThenFastAIAgent("SlowAgent", TimeSpan.FromSeconds(60));

        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<ITemporalClient>(_fixture.Client);
        builder.Services.ConfigureTemporalAgents(
            configure: options =>
            {
                options.AddAIAgent(agent);
                options.ActivityStartToCloseTimeout = TimeSpan.FromSeconds(2);
            },
            taskQueue: taskQueue);

        using var host = builder.Build();
        await host.StartAsync();

        try
        {
            var proxy = host.Services.GetTemporalAgentProxy("SlowAgent");
            var session = await proxy.CreateSessionAsync();

            var sw = Stopwatch.StartNew();
            var response = await proxy.RunAsync("Hello after timeout", session);
            sw.Stop();

            // Response should be valid — the retry succeeded.
            Assert.NotNull(response);
            Assert.NotEmpty(response.Messages);
            Assert.Contains("Echo [1]:", response.Messages[0].Text);

            // Total time should be well under the 60s delay (proving first attempt was cancelled).
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(30),
                $"Expected completion in under 30s but took {sw.Elapsed}");

            // Agent was called at least twice: first attempt timed out, second succeeded.
            Assert.True(agent.CallCount >= 2,
                $"Expected at least 2 agent calls but got {agent.CallCount}");

            _output.WriteLine(
                $"Completed in {sw.Elapsed.TotalSeconds:F1}s with {agent.CallCount} agent call(s). " +
                $"StartToCloseTimeout enforcement confirmed.");
        }
        finally
        {
            await host.StopAsync();
        }
    }

    // ── Activity Heartbeat Timeout ───────────────────────────────────────────

    [Fact]
    public async Task HeartbeatTimeout_RetriesWhenNoHeartbeatSent()
    {
        // Agent delays 60s without sending heartbeats; heartbeat timeout is 2s.
        // Since the non-streaming path doesn't heartbeat, Temporal detects the
        // missing heartbeat and retries the activity.
        var taskQueue = $"heartbeat-test-{Guid.NewGuid():N}";
        var agent = new SlowThenFastAIAgent("HeartbeatAgent", TimeSpan.FromSeconds(60));

        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<ITemporalClient>(_fixture.Client);
        builder.Services.ConfigureTemporalAgents(
            configure: options =>
            {
                options.AddAIAgent(agent);
                // Leave StartToClose at a generous value so it doesn't interfere.
                options.ActivityStartToCloseTimeout = TimeSpan.FromMinutes(5);
                options.ActivityHeartbeatTimeout = TimeSpan.FromSeconds(2);
            },
            taskQueue: taskQueue);

        using var host = builder.Build();
        await host.StartAsync();

        try
        {
            var proxy = host.Services.GetTemporalAgentProxy("HeartbeatAgent");
            var session = await proxy.CreateSessionAsync();

            var sw = Stopwatch.StartNew();
            var response = await proxy.RunAsync("Hello after heartbeat timeout", session);
            sw.Stop();

            Assert.NotNull(response);
            Assert.NotEmpty(response.Messages);
            Assert.Contains("Echo [1]:", response.Messages[0].Text);

            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(30),
                $"Expected completion in under 30s but took {sw.Elapsed}");

            Assert.True(agent.CallCount >= 2,
                $"Expected at least 2 agent calls but got {agent.CallCount}");

            _output.WriteLine(
                $"Completed in {sw.Elapsed.TotalSeconds:F1}s with {agent.CallCount} agent call(s). " +
                $"HeartbeatTimeout enforcement confirmed.");
        }
        finally
        {
            await host.StopAsync();
        }
    }

    // ── Activity Failure & Retry ─────────────────────────────────────────────

    [Fact]
    public async Task ActivityFailure_RetriesAndEventuallySucceeds()
    {
        // Agent throws InvalidOperationException on first call; succeeds on retry.
        var taskQueue = $"failure-retry-{Guid.NewGuid():N}";
        var agent = new FailThenSucceedAIAgent("FailAgent", failCount: 1);

        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<ITemporalClient>(_fixture.Client);
        builder.Services.ConfigureTemporalAgents(
            configure: options => options.AddAIAgent(agent),
            taskQueue: taskQueue);

        using var host = builder.Build();
        await host.StartAsync();

        try
        {
            var proxy = host.Services.GetTemporalAgentProxy("FailAgent");
            var session = await proxy.CreateSessionAsync();

            // Despite the first attempt throwing, Temporal retries and the second attempt succeeds.
            var response = await proxy.RunAsync("Hello after failure", session);

            Assert.NotNull(response);
            Assert.NotEmpty(response.Messages);
            Assert.Contains("Echo [1]:", response.Messages[0].Text);

            Assert.True(agent.CallCount >= 2,
                $"Expected at least 2 agent calls but got {agent.CallCount}");

            _output.WriteLine($"Agent succeeded after {agent.CallCount} call(s) — retry confirmed.");
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task ActivityFailure_WorkflowRemainsOperationalAfterRetry()
    {
        // After a failed-then-retried first turn, the workflow should remain healthy
        // for subsequent turns.
        var taskQueue = $"failure-recovery-{Guid.NewGuid():N}";
        var agent = new FailThenSucceedAIAgent("RecoveryAgent", failCount: 1);

        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<ITemporalClient>(_fixture.Client);
        builder.Services.ConfigureTemporalAgents(
            configure: options => options.AddAIAgent(agent),
            taskQueue: taskQueue);

        using var host = builder.Build();
        await host.StartAsync();

        try
        {
            var proxy = host.Services.GetTemporalAgentProxy("RecoveryAgent");
            var session = await proxy.CreateSessionAsync();

            // Turn 1: fails on first attempt, succeeds on retry.
            var r1 = await proxy.RunAsync("Turn 1", session);
            Assert.Contains("Echo [1]:", r1.Messages[0].Text);

            // Turn 2: should succeed without any failures (only first call ever fails).
            var r2 = await proxy.RunAsync("Turn 2", session);
            Assert.Contains("Echo [2]:", r2.Messages[0].Text);

            _output.WriteLine(
                $"Workflow remained operational after initial failure. " +
                $"Total agent calls: {agent.CallCount}");
        }
        finally
        {
            await host.StopAsync();
        }
    }
}
