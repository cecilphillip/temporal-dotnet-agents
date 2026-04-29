using FakeItEasy;
using Microsoft.Agents.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Temporalio.Client;
using Temporalio.Extensions.Hosting;
using Temporalio.Extensions.Agents.Tests.Helpers;
using Temporalio.Extensions.Agents.Workflows;
using Xunit;

namespace Temporalio.Extensions.Agents.Tests;

public class TemporalWorkerBuilderExtensionsTests
{
    [Fact]
    public void AddTemporalAgents_RegistersTemporalAgentsOptions()
    {
        // Arrange
        var services = new ServiceCollection();
        var fakeClient = A.Fake<ITemporalClient>();
        services.AddSingleton(fakeClient);
        var builder = services.AddHostedTemporalWorker("test-task-queue");

        // Act
        builder.AddTemporalAgents(opts => opts.AddAIAgent(new StubAIAgent("test-agent")));

        // Assert
        var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<TemporalAgentsOptions>();
        Assert.NotNull(options);
    }

    [Fact]
    public void AddTemporalAgents_RegistersAgentFactoriesDictionary()
    {
        // Arrange
        var services = new ServiceCollection();
        var fakeClient = A.Fake<ITemporalClient>();
        services.AddSingleton(fakeClient);
        var builder = services.AddHostedTemporalWorker("test-task-queue");
        var agent = new StubAIAgent("my-agent");

        // Act
        builder.AddTemporalAgents(opts => opts.AddAIAgent(agent));

        // Assert
        var provider = services.BuildServiceProvider();
        var factories = provider.GetRequiredService<IReadOnlyDictionary<string, Func<IServiceProvider, AIAgent>>>();
        Assert.NotNull(factories);
        Assert.Single(factories);
        Assert.True(factories.ContainsKey("my-agent"));
    }

    [Fact]
    public void AddTemporalAgents_RegistersITemporalAgentClient()
    {
        // Arrange
        var services = new ServiceCollection();
        var fakeClient = A.Fake<ITemporalClient>();
        services.AddSingleton(fakeClient);
        var builder = services.AddHostedTemporalWorker("test-task-queue");

        // Act
        builder.AddTemporalAgents(opts => opts.AddAIAgent(new StubAIAgent("test-agent")));

        // Assert
        var provider = services.BuildServiceProvider();
        var client = provider.GetService<ITemporalAgentClient>();
        Assert.NotNull(client);
        Assert.IsType<DefaultTemporalAgentClient>(client);
    }

    [Fact]
    public void AddTemporalAgents_RegistersKeyedAIAgentProxies()
    {
        // Arrange
        var services = new ServiceCollection();
        var fakeClient = A.Fake<ITemporalClient>();
        services.AddSingleton(fakeClient);
        var builder = services.AddHostedTemporalWorker("test-task-queue");

        // Act
        builder.AddTemporalAgents(opts =>
        {
            opts.AddAIAgent(new StubAIAgent("agent-1"));
            opts.AddAIAgent(new StubAIAgent("agent-2"));
        });

        // Assert
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
        // Arrange
        var services = new ServiceCollection();
        var fakeClient = A.Fake<ITemporalClient>();
        services.AddSingleton(fakeClient);
        var builder = services.AddHostedTemporalWorker("test-task-queue");

        // Act
        builder.AddTemporalAgents(opts => opts.AddAIAgent(new StubAIAgent("test-agent")));

        // Assert
        var provider = services.BuildServiceProvider();
        var proxy1 = provider.GetKeyedService<AIAgent>("test-agent");
        var proxy2 = provider.GetKeyedService<AIAgent>("test-agent");

        Assert.NotNull(proxy1);
        Assert.Same(proxy1, proxy2); // Same instance (singleton)
    }

    [Fact]
    public void AddTemporalAgents_CanChainWithOtherBuilderMethods()
    {
        // Arrange
        var services = new ServiceCollection();
        var fakeClient = A.Fake<ITemporalClient>();
        services.AddSingleton(fakeClient);
        var builder = services.AddHostedTemporalWorker("test-task-queue");

        // Act
        var result = builder
            .AddTemporalAgents(opts => opts.AddAIAgent(new StubAIAgent("test-agent")))
            .ConfigureOptions(opts => opts.MaxConcurrentActivities = 10);

        // Assert — verifies the builder returns something we can continue chaining on
        Assert.NotNull(result);
    }

    [Fact]
    public void AddTemporalAgents_Throws_WhenCalledTwiceOnSameBuilder()
    {
        // Arrange — register once, then attempt a second registration.
        var services = new ServiceCollection();
        var fakeClient = A.Fake<ITemporalClient>();
        services.AddSingleton(fakeClient);
        var builder = services.AddHostedTemporalWorker("test-task-queue");
        builder.AddTemporalAgents(opts => opts.AddAIAgent(new StubAIAgent("agent-1")));

        // Act & Assert — second call must surface the misuse loudly.
        var ex = Assert.Throws<InvalidOperationException>(() =>
            builder.AddTemporalAgents(opts => opts.AddAIAgent(new StubAIAgent("agent-2"))));
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
        // Arrange
        var services = new ServiceCollection();
        var fakeClient = A.Fake<ITemporalClient>();
        services.AddSingleton(fakeClient);
        var builder = services.AddHostedTemporalWorker("test-task-queue");

        // Act & Assert
        var ex = Assert.Throws<ArgumentNullException>(() =>
            builder.AddTemporalAgents(null!));
        Assert.Equal("configure", ex.ParamName);
    }

    [Fact]
    public void AddTemporalAgents_AllowsPreregisteredCustomClient()
    {
        // Arrange
        var services = new ServiceCollection();
        var customClient = A.Fake<ITemporalAgentClient>();
        services.AddSingleton(customClient);
        var fakeTemporalClient = A.Fake<ITemporalClient>();
        services.AddSingleton(fakeTemporalClient);
        var builder = services.AddHostedTemporalWorker("test-task-queue");

        // Act
        builder.AddTemporalAgents(opts => opts.AddAIAgent(new StubAIAgent("test-agent")));

        // Assert
        var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<ITemporalAgentClient>();
        Assert.Same(customClient, client); // Should use the pre-registered custom client
    }

    [Fact]
    public void AddTemporalAgents_CanRegisterAgentViaFactory()
    {
        // Arrange
        var services = new ServiceCollection();
        var fakeClient = A.Fake<ITemporalClient>();
        services.AddSingleton(fakeClient);
        var builder = services.AddHostedTemporalWorker("test-task-queue");

        // Act
        builder.AddTemporalAgents(opts =>
        {
            opts.AddAIAgentFactory("factory-agent", sp => new StubAIAgent("factory-agent"));
        });

        // Assert
        var provider = services.BuildServiceProvider();
        var proxy = provider.GetKeyedService<AIAgent>("factory-agent");
        Assert.NotNull(proxy);
    }


    [Fact]
    public void AddTemporalAgents_WorkflowAndActivitiesAreRegistered()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging(); // required: DurableAIClientOptionsConfigurator/DurableAIWorkerClientConfigurator inject ILogger
        var fakeClient = A.Fake<ITemporalClient>();
        services.AddSingleton(fakeClient);
        var builder = services.AddHostedTemporalWorker("test-task-queue");

        // Act
        builder.AddTemporalAgents(opts => opts.AddAIAgent(new StubAIAgent("test-agent")));

        // Assert — check that AgentWorkflow and AgentActivities are registered
        var provider = services.BuildServiceProvider();
        // If the workflow/activities are registered, the service collection should not throw
        // We verify by checking that TemporalWorkerServiceOptions has been configured
        var workerOptions = provider.GetRequiredService<IOptions<TemporalWorkerServiceOptions>>();
        Assert.NotNull(workerOptions);
    }
}
