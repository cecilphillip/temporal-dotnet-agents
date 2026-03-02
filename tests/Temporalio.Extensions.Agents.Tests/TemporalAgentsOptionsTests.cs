using Temporalio.Extensions.Agents.Tests.Helpers;
using Xunit;

namespace Temporalio.Extensions.Agents.Tests;

public class TemporalAgentsOptionsTests
{
    [Fact]
    public void DefaultTimeToLive_Is14Days()
    {
        var options = new TemporalAgentsOptions();
        Assert.Equal(TimeSpan.FromDays(14), options.DefaultTimeToLive);
    }

    [Fact]
    public void AddAIAgent_StoresFactory()
    {
        var options = new TemporalAgentsOptions();
        var agent = new StubAIAgent("TestAgent");
        options.AddAIAgent(agent);

        var factories = options.GetAgentFactories();
        Assert.True(factories.ContainsKey("TestAgent"));
    }

    [Fact]
    public void AddAIAgent_FactoryReturnsTheAgent()
    {
        var agent = new StubAIAgent("TestAgent");
        var options = new TemporalAgentsOptions();
        options.AddAIAgent(agent);

        var factories = options.GetAgentFactories();
        var resolvedAgent = factories["TestAgent"](null!);
        Assert.Same(agent, resolvedAgent);
    }

    [Fact]
    public void AddAIAgent_NullAgent_ThrowsArgumentNullException()
    {
        var options = new TemporalAgentsOptions();
        Assert.Throws<ArgumentNullException>(() => options.AddAIAgent(null!));
    }

    [Fact]
    public void AddAIAgent_AgentWithNoName_ThrowsArgumentException()
    {
        var options = new TemporalAgentsOptions();
        var agent = new StubAIAgent(null);  // null name
        Assert.Throws<ArgumentException>(() => options.AddAIAgent(agent));
    }

    [Fact]
    public void AddAIAgent_DuplicateName_ThrowsArgumentException()
    {
        var options = new TemporalAgentsOptions();
        var agent1 = new StubAIAgent("MyAgent");
        var agent2 = new StubAIAgent("MyAgent");

        options.AddAIAgent(agent1);
        Assert.Throws<ArgumentException>(() => options.AddAIAgent(agent2));
    }

    [Fact]
    public void AddAIAgent_NameIsCaseInsensitive()
    {
        // Should fail with duplicate even when case differs
        var options = new TemporalAgentsOptions();
        options.AddAIAgent(new StubAIAgent("TestAgent"));
        Assert.Throws<ArgumentException>(() => options.AddAIAgent(new StubAIAgent("testagent")));
    }

    [Fact]
    public void AddAIAgentFactory_WithExplicitTtl_ReturnedByGetTimeToLive()
    {
        var options = new TemporalAgentsOptions();
        var expectedTtl = TimeSpan.FromHours(2);

        options.AddAIAgentFactory("SpecialAgent", _ => new StubAIAgent("SpecialAgent"), timeToLive: expectedTtl);

        Assert.Equal(expectedTtl, options.GetTimeToLive("SpecialAgent"));
    }

    [Fact]
    public void GetTimeToLive_NoPerAgentTtl_FallsBackToDefault()
    {
        var options = new TemporalAgentsOptions();
        options.DefaultTimeToLive = TimeSpan.FromDays(7);
        options.AddAIAgent(new StubAIAgent("Agent"));

        Assert.Equal(TimeSpan.FromDays(7), options.GetTimeToLive("Agent"));
    }

    [Fact]
    public void GetTimeToLive_PerAgentTtl_OverridesDefault()
    {
        var options = new TemporalAgentsOptions();
        options.DefaultTimeToLive = TimeSpan.FromDays(14);
        var perAgentTtl = TimeSpan.FromHours(1);

        options.AddAIAgentFactory("FastAgent", _ => new StubAIAgent("FastAgent"), timeToLive: perAgentTtl);

        Assert.Equal(perAgentTtl, options.GetTimeToLive("FastAgent"));
    }

    [Fact]
    public void GetTimeToLive_NameIsNotRegistered_ReturnsDefault()
    {
        var options = new TemporalAgentsOptions();
        options.DefaultTimeToLive = TimeSpan.FromDays(3);

        // Unknown agent name falls back to default TTL
        Assert.Equal(TimeSpan.FromDays(3), options.GetTimeToLive("NonExistentAgent"));
    }

    [Fact]
    public void GetAgentFactories_ContainsAllRegisteredAgents()
    {
        var options = new TemporalAgentsOptions();
        options.AddAIAgent(new StubAIAgent("Agent1"));
        options.AddAIAgent(new StubAIAgent("Agent2"));
        options.AddAIAgentFactory("Agent3", _ => new StubAIAgent("Agent3"));

        var factories = options.GetAgentFactories();
        Assert.Equal(3, factories.Count);
        Assert.True(factories.ContainsKey("Agent1"));
        Assert.True(factories.ContainsKey("Agent2"));
        Assert.True(factories.ContainsKey("Agent3"));
    }

    [Fact]
    public void AddAIAgents_Params_AddsAll()
    {
        var options = new TemporalAgentsOptions();
        options.AddAIAgents(new StubAIAgent("A"), new StubAIAgent("B"));

        var factories = options.GetAgentFactories();
        Assert.True(factories.ContainsKey("A"));
        Assert.True(factories.ContainsKey("B"));
    }

    // ─── AddAIAgentFactory null guards ────────────────────────────────────────

    [Fact]
    public void AddAIAgentFactory_NullName_ThrowsArgumentNullException()
    {
        var options = new TemporalAgentsOptions();
        Assert.Throws<ArgumentNullException>(() =>
            options.AddAIAgentFactory(null!, _ => new StubAIAgent("X")));
    }

    [Fact]
    public void AddAIAgentFactory_NullFactory_ThrowsArgumentNullException()
    {
        var options = new TemporalAgentsOptions();
        Assert.Throws<ArgumentNullException>(() =>
            options.AddAIAgentFactory("Agent", (Func<IServiceProvider, Microsoft.Agents.AI.AIAgent>)null!));
    }

    [Fact]
    public void AddAIAgentFactory_AsyncOverload_NullName_ThrowsArgumentNullException()
    {
        var options = new TemporalAgentsOptions();
        Assert.Throws<ArgumentNullException>(() =>
            options.AddAIAgentFactory(null!, async sp => { await Task.Delay(0); return new StubAIAgent("X"); }));
    }

    [Fact]
    public void AddAIAgentFactory_AsyncOverload_NullFactory_ThrowsArgumentNullException()
    {
        var options = new TemporalAgentsOptions();
        Assert.Throws<ArgumentNullException>(() =>
            options.AddAIAgentFactory("Agent", (Func<IServiceProvider, Task<Microsoft.Agents.AI.AIAgent>>)null!));
    }

    // ─── AddAIAgents null guard ───────────────────────────────────────────────

    [Fact]
    public void AddAIAgents_NullArray_ThrowsArgumentNullException()
    {
        var options = new TemporalAgentsOptions();
        Assert.Throws<ArgumentNullException>(() => options.AddAIAgents(null!));
    }

    // ─── AddAgentDescriptor null guards ───────────────────────────────────────

    [Fact]
    public void AddAgentDescriptor_NullName_ThrowsArgumentException()
    {
        var options = new TemporalAgentsOptions();
        Assert.Throws<ArgumentNullException>(() =>
            options.AddAgentDescriptor(null!, "Some description"));
    }

    [Fact]
    public void AddAgentDescriptor_EmptyDescription_ThrowsArgumentException()
    {
        var options = new TemporalAgentsOptions();
        Assert.Throws<ArgumentException>(() =>
            options.AddAgentDescriptor("Agent", ""));
    }
}
