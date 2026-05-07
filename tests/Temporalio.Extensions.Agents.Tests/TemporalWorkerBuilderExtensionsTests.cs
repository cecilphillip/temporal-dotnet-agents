using FakeItEasy;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Temporalio.Client;
using Temporalio.Extensions.Hosting;
using Temporalio.Extensions.Agents.Workflows;
using Xunit;

namespace Temporalio.Extensions.Agents.Tests;

public class TemporalWorkerBuilderExtensionsTests
{
    private static Action<DurableAgentBuilder> ConfigureWithChatClient =>
        agent => agent.ChatClient = _ => A.Fake<IChatClient>();

    [Fact]
    public void AddTemporalAgents_RegistersTemporalAgentsOptions()
    {
        var services = new ServiceCollection();
        services.AddSingleton(A.Fake<ITemporalClient>());
        var builder = services.AddHostedTemporalWorker("test-task-queue");

        builder.AddTemporalAgents(opts => opts.AddDurableAgent("test-agent", ConfigureWithChatClient));

        var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<TemporalAgentsOptions>();
        Assert.NotNull(options);
    }

    [Fact]
    public void AddTemporalAgents_RegistersDurableAgentRegistrations()
    {
        var services = new ServiceCollection();
        services.AddSingleton(A.Fake<ITemporalClient>());
        var builder = services.AddHostedTemporalWorker("test-task-queue");

        builder.AddTemporalAgents(opts => opts.AddDurableAgent("my-agent", ConfigureWithChatClient));

        var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<TemporalAgentsOptions>();
        Assert.True(options.IsAgentRegistered("my-agent"));
    }

    [Fact]
    public void AddTemporalAgents_RegistersITemporalAgentClient()
    {
        var services = new ServiceCollection();
        services.AddSingleton(A.Fake<ITemporalClient>());
        var builder = services.AddHostedTemporalWorker("test-task-queue");

        builder.AddTemporalAgents(opts => opts.AddDurableAgent("test-agent", ConfigureWithChatClient));

        var provider = services.BuildServiceProvider();
        var client = provider.GetService<ITemporalAgentClient>();
        Assert.NotNull(client);
        Assert.IsType<DefaultTemporalAgentClient>(client);
    }

    [Fact]
    public void AddTemporalAgents_RegistersKeyedAIAgentProxies()
    {
        var services = new ServiceCollection();
        services.AddSingleton(A.Fake<ITemporalClient>());
        var builder = services.AddHostedTemporalWorker("test-task-queue");

        builder.AddTemporalAgents(opts =>
        {
            opts.AddDurableAgent("agent-1", ConfigureWithChatClient);
            opts.AddDurableAgent("agent-2", ConfigureWithChatClient);
        });

        var provider = services.BuildServiceProvider();
        var agent1 = provider.GetKeyedService<AIAgent>("agent-1");
        var agent2 = provider.GetKeyedService<AIAgent>("agent-2");

        Assert.NotNull(agent1);
        Assert.NotNull(agent2);
        Assert.IsType<TemporalAIAgentProxy>(agent1);
        Assert.IsType<TemporalAIAgentProxy>(agent2);
    }

    [Fact]
    public void AddTemporalAgents_KeyedProxiesAreSingletons()
    {
        var services = new ServiceCollection();
        services.AddSingleton(A.Fake<ITemporalClient>());
        var builder = services.AddHostedTemporalWorker("test-task-queue");

        builder.AddTemporalAgents(opts => opts.AddDurableAgent("test-agent", ConfigureWithChatClient));

        var provider = services.BuildServiceProvider();
        var proxy1 = provider.GetKeyedService<AIAgent>("test-agent");
        var proxy2 = provider.GetKeyedService<AIAgent>("test-agent");

        Assert.NotNull(proxy1);
        Assert.Same(proxy1, proxy2);
    }

    [Fact]
    public void AddTemporalAgents_CanChainWithOtherBuilderMethods()
    {
        var services = new ServiceCollection();
        services.AddSingleton(A.Fake<ITemporalClient>());
        var builder = services.AddHostedTemporalWorker("test-task-queue");

        var result = builder
            .AddTemporalAgents(opts => opts.AddDurableAgent("test-agent", ConfigureWithChatClient))
            .ConfigureOptions(opts => opts.MaxConcurrentActivities = 10);

        Assert.NotNull(result);
    }

    [Fact]
    public void AddTemporalAgents_Throws_WhenCalledTwiceOnSameBuilder()
    {
        var services = new ServiceCollection();
        services.AddSingleton(A.Fake<ITemporalClient>());
        var builder = services.AddHostedTemporalWorker("test-task-queue");
        builder.AddTemporalAgents(opts => opts.AddDurableAgent("agent-1", ConfigureWithChatClient));

        var ex = Assert.Throws<InvalidOperationException>(() =>
            builder.AddTemporalAgents(opts => opts.AddDurableAgent("agent-2", ConfigureWithChatClient)));
        Assert.Contains("already been called", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AddTemporalAgents_ThrowsOnNullBuilder()
    {
        var ex = Assert.Throws<ArgumentNullException>(() =>
            ((ITemporalWorkerServiceOptionsBuilder)null!).AddTemporalAgents(_ => { }));
        Assert.Equal("builder", ex.ParamName);
    }

    [Fact]
    public void AddTemporalAgents_ThrowsOnNullConfigure()
    {
        var services = new ServiceCollection();
        services.AddSingleton(A.Fake<ITemporalClient>());
        var builder = services.AddHostedTemporalWorker("test-task-queue");

        var ex = Assert.Throws<ArgumentNullException>(() => builder.AddTemporalAgents(null!));
        Assert.Equal("configure", ex.ParamName);
    }

    [Fact]
    public void AddTemporalAgents_AllowsPreregisteredCustomClient()
    {
        var services = new ServiceCollection();
        var customClient = A.Fake<ITemporalAgentClient>();
        services.AddSingleton(customClient);
        services.AddSingleton(A.Fake<ITemporalClient>());
        var builder = services.AddHostedTemporalWorker("test-task-queue");

        builder.AddTemporalAgents(opts => opts.AddDurableAgent("test-agent", ConfigureWithChatClient));

        var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<ITemporalAgentClient>();
        Assert.Same(customClient, client);
    }

    [Fact]
    public void AddTemporalAgents_WorkflowAndActivitiesAreRegistered()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(A.Fake<ITemporalClient>());
        var builder = services.AddHostedTemporalWorker("test-task-queue");

        builder.AddTemporalAgents(opts => opts.AddDurableAgent("test-agent", ConfigureWithChatClient));

        var provider = services.BuildServiceProvider();
        var workerOptions = provider.GetRequiredService<IOptions<TemporalWorkerServiceOptions>>();
        Assert.NotNull(workerOptions);
    }

#pragma warning disable TA001
    [Fact]
    public void TemporalAgentsPlugin_NameMatchesPluginNameConstant()
    {
        var plugin = new TemporalAgentsPlugin();
        Assert.Equal(TemporalAgentsPlugin.PluginName, plugin.Name);
    }

    [Fact]
    public void AddWorkerPlugin_RegistersEquivalentServicesToAddTemporalAgents()
    {
        var services = new ServiceCollection();
        services.AddSingleton(A.Fake<ITemporalClient>());
        var builder = services.AddHostedTemporalWorker("test-task-queue");
        var plugin = new TemporalAgentsPlugin(opts => opts.AddDurableAgent("plugin-agent", ConfigureWithChatClient));

        builder.AddWorkerPlugin(plugin);

        var provider = services.BuildServiceProvider();
        var client = provider.GetService<ITemporalAgentClient>();
        Assert.NotNull(client);
        Assert.IsType<DefaultTemporalAgentClient>(client);
    }
#pragma warning restore TA001
}
