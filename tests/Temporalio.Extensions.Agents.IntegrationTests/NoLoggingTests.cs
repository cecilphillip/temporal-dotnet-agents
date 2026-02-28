using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Temporalio.Client;
using Temporalio.Extensions.Agents.IntegrationTests.Helpers;
using Xunit;
using Xunit.Abstractions;

namespace Temporalio.Extensions.Agents.IntegrationTests;

/// <summary>
/// Tests that the library operates correctly when no logging infrastructure is configured.
/// All internal components fall back to <see cref="NullLogger"/> — this test verifies
/// that no exceptions are thrown and agent operations succeed silently.
/// </summary>
[Trait("Category", "Integration")]
public class NoLoggingTests : IClassFixture<IntegrationTestFixture>
{
    private readonly IntegrationTestFixture _fixture;
    private readonly ITestOutputHelper _output;

    public NoLoggingTests(IntegrationTestFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    [Fact]
    public async Task NoLoggingConfigured_AgentExecutesSuccessfully()
    {
        // Build a host using the bare HostBuilder (no default logging providers).
        // Replace the ILoggerFactory with NullLoggerFactory to ensure no logging
        // infrastructure is available for the library to use.
        var taskQueue = $"no-logging-test-{Guid.NewGuid():N}";

        var builder = new HostBuilder();
        builder.ConfigureServices(services =>
        {
            // Only register what's strictly necessary — no logging.
            services.AddSingleton<ITemporalClient>(_fixture.Client);
            services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
            services.ConfigureTemporalAgents(
                configure: options => options.AddAIAgent(
                    new EchoAIAgent("NoLogAgent")),
                taskQueue: taskQueue);
        });

        using var host = builder.Build();
        await host.StartAsync();

        try
        {
            var proxy = host.Services.GetTemporalAgentProxy("NoLogAgent");
            var session = await proxy.CreateSessionAsync();

            // Single turn to verify the full stack works without logging.
            var r1 = await proxy.RunAsync("Without logging", session);
            Assert.Contains("Echo [1]: Without logging", r1.Messages[0].Text);

            // Multi-turn to verify state management.
            var r2 = await proxy.RunAsync("Still no logging", session);
            Assert.Contains("Echo [2]: Still no logging", r2.Messages[0].Text);

            _output.WriteLine("Agent executed 2 turns successfully with NullLoggerFactory.");
        }
        finally
        {
            await host.StopAsync();
        }
    }
}
