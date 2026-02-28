using Microsoft.Agents.AI;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Temporalio.Client;
using Temporalio.Extensions.Agents.Tests.Helpers;
using Xunit;

namespace Temporalio.Extensions.Agents.Tests;

/// <summary>
/// Tests for the routing additions to <see cref="TemporalAgentsOptions"/> and
/// the DI wiring in <see cref="TemporalWorkerBuilderExtensions"/>.
/// </summary>
public class RoutingOptionsTests
{
    [Fact]
    public void AddAgentDescriptor_StoresDescriptor()
    {
        var options = new TemporalAgentsOptions();
        options.AddAgentDescriptor("WeatherAgent", "Handles weather questions.");

        var descriptors = options.GetAgentDescriptors();
        Assert.Single(descriptors);
        Assert.Equal("WeatherAgent", descriptors[0].Name);
        Assert.Equal("Handles weather questions.", descriptors[0].Description);
    }

    [Fact]
    public void AddAgentDescriptor_MultipleAgents_AllStored()
    {
        var options = new TemporalAgentsOptions();
        options.AddAgentDescriptor("A", "desc A");
        options.AddAgentDescriptor("B", "desc B");
        options.AddAgentDescriptor("C", "desc C");

        var descriptors = options.GetAgentDescriptors();
        Assert.Equal(3, descriptors.Count);
    }

    [Fact]
    public void AddAgentDescriptor_SameNameOverwrites_LastWins()
    {
        var options = new TemporalAgentsOptions();
        options.AddAgentDescriptor("Agent", "first description");
        options.AddAgentDescriptor("Agent", "updated description");

        var descriptors = options.GetAgentDescriptors();
        Assert.Single(descriptors);
        Assert.Equal("updated description", descriptors[0].Description);
    }

    [Fact]
    public void SetRouterAgent_StoresAgent()
    {
        var options = new TemporalAgentsOptions();
        var routerAgent = new StubAIAgent("__router__");
        options.SetRouterAgent(routerAgent);

        Assert.Same(routerAgent, options.GetRouterAgent());
    }

    [Fact]
    public void SetRouterAgent_Null_Throws()
    {
        var options = new TemporalAgentsOptions();
        Assert.Throws<ArgumentNullException>(() => options.SetRouterAgent(null!));
    }

    [Fact]
    public void GetAgentDescriptors_NoDescriptors_ReturnsEmpty()
    {
        var options = new TemporalAgentsOptions();
        Assert.Empty(options.GetAgentDescriptors());
    }

    [Fact]
    public void AddTemporalAgents_WithRouterAgent_RegistersLlmAgentRouterAsSingleton()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddSingleton(new Mock<ITemporalClient>().Object);
        var builder = services.AddHostedTemporalWorker("test-queue");
        var routerAgent = new StubAIAgent("__router__");

        // Act
        builder.AddTemporalAgents(opts =>
        {
            opts.AddAIAgent(new StubAIAgent("AgentA"));
            opts.AddAgentDescriptor("AgentA", "Handles A requests.");
            opts.SetRouterAgent(routerAgent);
        });

        // Assert
        var provider = services.BuildServiceProvider();
        var router = provider.GetService<IAgentRouter>();
        Assert.NotNull(router);
        Assert.IsType<LlmAgentRouter>(router);
    }

    [Fact]
    public void AddTemporalAgents_WithoutRouterAgent_DoesNotRegisterIAgentRouter()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddSingleton(new Mock<ITemporalClient>().Object);
        var builder = services.AddHostedTemporalWorker("test-queue");

        // Act
        builder.AddTemporalAgents(opts => opts.AddAIAgent(new StubAIAgent("AgentA")));

        // Assert
        var provider = services.BuildServiceProvider();
        var router = provider.GetService<IAgentRouter>();
        Assert.Null(router);
    }

    [Fact]
    public void AddAIAgentFactory_AsyncOverload_RegistersAgent()
    {
        var options = new TemporalAgentsOptions();
        var expectedAgent = new StubAIAgent("AsyncAgent");

        options.AddAIAgentFactory("AsyncAgent",
            async sp =>
            {
                await Task.Delay(0); // simulate async setup
                return (AIAgent)expectedAgent;
            });

        var factories = options.GetAgentFactories();
        Assert.True(factories.ContainsKey("AsyncAgent"));
        var resolved = factories["AsyncAgent"](null!);
        Assert.Same(expectedAgent, resolved);
    }
}
