using Microsoft.Agents.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Temporalio.Client;
using Temporalio.Extensions.Agents.IntegrationTests.Helpers;
using Temporalio.Testing;
using Xunit;

namespace Temporalio.Extensions.Agents.IntegrationTests;

/// <summary>
/// Shared xunit fixture that manages the lifecycle of:
/// <list type="bullet">
///   <item>A local Temporal test server (<see cref="WorkflowEnvironment.StartLocalAsync"/>)</item>
///   <item>A .NET Generic Host with the Temporal worker and agent registered</item>
/// </list>
///
/// The fixture is shared across all tests in a class via <see cref="IClassFixture{T}"/>,
/// so the server and worker are started once and reused — each test creates a unique session.
/// </summary>
public sealed class IntegrationTestFixture : IAsyncLifetime
{
    private IHost? _host;

    /// <summary>The task queue used by the worker and all proxies.</summary>
    public const string TaskQueue = "integration-test-agents";

    /// <summary>The running Temporal test server environment.</summary>
    public WorkflowEnvironment Environment { get; private set; } = null!;

    /// <summary>The Temporal client connected to the local test server.</summary>
    public ITemporalClient Client => Environment.Client;

    /// <summary>The <see cref="AIAgent"/> proxy for the registered <c>EchoAgent</c>.</summary>
    public AIAgent AgentProxy { get; private set; } = null!;

    /// <summary>Starts the local Temporal server and the hosted Temporal worker.</summary>
    public async Task InitializeAsync()
    {
        Environment = await WorkflowEnvironment.StartLocalAsync();

        _host = BuildHost();
        await _host.StartAsync();

        AgentProxy = _host.Services.GetTemporalAgentProxy("EchoAgent");
    }

    /// <summary>
    /// Builds a new <see cref="IHost"/> configured with the Temporal worker and EchoAgent.
    /// Useful for tests that need to stop and restart the worker (e.g., replay tests).
    /// </summary>
    public IHost BuildHost()
    {
        var builder = Host.CreateApplicationBuilder();

        // Register the test server's ITemporalClient directly — no targetHost needed.
        builder.Services.AddSingleton<ITemporalClient>(Environment.Client);

        builder.Services.ConfigureTemporalAgents(
            configure: options => options.AddAIAgent(new EchoAIAgent("EchoAgent")),
            taskQueue: TaskQueue);

        return builder.Build();
    }

    /// <summary>Stops the worker and shuts down the Temporal test server.</summary>
    public async Task DisposeAsync()
    {
        if (_host is not null)
        {
            await _host.StopAsync();
            _host.Dispose();
        }

        await Environment.ShutdownAsync();
    }
}
