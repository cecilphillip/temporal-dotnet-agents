using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Temporalio.Client;
using Temporalio.Extensions.Hosting;
using Temporalio.Testing;
using Xunit;

namespace Temporalio.Extensions.AI.IntegrationTests.Helpers;

/// <summary>
/// Shared xunit fixture that manages:
/// <list type="bullet">
///   <item>A local Temporal test server</item>
///   <item>A .NET Generic Host with the Temporal worker, durable chat workflow, and activities</item>
/// </list>
/// Shared across tests via <see cref="IClassFixture{T}"/>.
/// </summary>
public sealed class IntegrationTestFixture : IAsyncLifetime
{
    private IHost? _host;

    public const string TaskQueue = "integration-test-ai";

    public WorkflowEnvironment Environment { get; private set; } = null!;
    public ITemporalClient Client => Environment.Client;
    public TestChatClient TestChatClient { get; } = new();
    public DurableChatSessionClient SessionClient { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        Environment = await WorkflowEnvironment.StartLocalAsync();

        _host = BuildHost();
        await _host.StartAsync();

        SessionClient = _host.Services.GetRequiredService<DurableChatSessionClient>();
    }

    public IHost BuildHost()
    {
        var builder = Host.CreateApplicationBuilder();

        builder.Services.AddSingleton<ITemporalClient>(Environment.Client);
        builder.Services.AddSingleton<IChatClient>(TestChatClient);

        builder.Services
            .AddHostedTemporalWorker(TaskQueue)
            .AddDurableAI(opts =>
            {
                opts.ActivityTimeout = TimeSpan.FromSeconds(30);
                opts.HeartbeatTimeout = TimeSpan.FromSeconds(10);
                opts.SessionTimeToLive = TimeSpan.FromMinutes(5);
            });

        return builder.Build();
    }

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
