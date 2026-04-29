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

    // ─── ApprovalTimeout ─────────────────────────────────────────────────────

    [Fact]
    public void ApprovalTimeout_DefaultIs7Days()
    {
        var options = new TemporalAgentsOptions();
        Assert.Equal(TimeSpan.FromDays(7), options.ApprovalTimeout);
    }

    [Fact]
    public void ApprovalTimeout_CanBeCustomized()
    {
        var options = new TemporalAgentsOptions();
        options.ApprovalTimeout = TimeSpan.FromHours(1);
        Assert.Equal(TimeSpan.FromHours(1), options.ApprovalTimeout);
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

    // ─── Agent Registry (read-only introspection) ───────────────────────────

    [Fact]
    public void GetRegisteredAgentNames_ReturnsAllNames()
    {
        var options = new TemporalAgentsOptions();
        options.AddAIAgent(new StubAIAgent("Alpha"));
        options.AddAIAgent(new StubAIAgent("Beta"));
        options.AddAIAgentFactory("Gamma", _ => new StubAIAgent("Gamma"));

        var names = options.GetRegisteredAgentNames();
        Assert.Equal(3, names.Count);
        Assert.Contains("Alpha", names);
        Assert.Contains("Beta", names);
        Assert.Contains("Gamma", names);
    }

    [Fact]
    public void GetRegisteredAgentNames_Empty_ReturnsEmpty()
    {
        var options = new TemporalAgentsOptions();
        Assert.Empty(options.GetRegisteredAgentNames());
    }

    [Fact]
    public void IsAgentRegistered_RegisteredName_ReturnsTrue()
    {
        var options = new TemporalAgentsOptions();
        options.AddAIAgent(new StubAIAgent("MyAgent"));

        Assert.True(options.IsAgentRegistered("MyAgent"));
    }

    [Fact]
    public void IsAgentRegistered_CaseInsensitive()
    {
        var options = new TemporalAgentsOptions();
        options.AddAIAgent(new StubAIAgent("MyAgent"));

        Assert.True(options.IsAgentRegistered("myagent"));
        Assert.True(options.IsAgentRegistered("MYAGENT"));
    }

    [Fact]
    public void IsAgentRegistered_UnknownName_ReturnsFalse()
    {
        var options = new TemporalAgentsOptions();
        Assert.False(options.IsAgentRegistered("DoesNotExist"));
    }

    [Fact]
    public void IsAgentRegistered_NullOrEmpty_ReturnsFalse()
    {
        var options = new TemporalAgentsOptions();
        Assert.False(options.IsAgentRegistered(null!));
        Assert.False(options.IsAgentRegistered(""));
    }

    [Fact]
    public void GetRegisteredDescriptors_ReturnsDescriptors()
    {
        var options = new TemporalAgentsOptions();
        options.AddAgentDescriptor("Agent1", "Handles billing.");
        options.AddAgentDescriptor("Agent2", "Handles weather.");

        var descriptors = options.GetRegisteredDescriptors();
        Assert.Equal(2, descriptors.Count);
        Assert.Contains(descriptors, d => d.Name == "Agent1" && d.Description == "Handles billing.");
        Assert.Contains(descriptors, d => d.Name == "Agent2" && d.Description == "Handles weather.");
    }

    [Fact]
    public void GetRegisteredDescriptors_Empty_ReturnsEmpty()
    {
        var options = new TemporalAgentsOptions();
        Assert.Empty(options.GetRegisteredDescriptors());
    }

    // ─── Auto-extracted descriptors ─────────────────────────────────────────

    [Fact]
    public void AddAIAgent_WithDescription_AutoPopulatesDescriptor()
    {
        var options = new TemporalAgentsOptions();
        var agent = new StubAIAgent("WeatherAgent", description: "Handles weather questions.");

        options.AddAIAgent(agent);

        var descriptors = options.GetRegisteredDescriptors();
        Assert.Single(descriptors);
        Assert.Equal("WeatherAgent", descriptors[0].Name);
        Assert.Equal("Handles weather questions.", descriptors[0].Description);
    }

    [Fact]
    public void AddAIAgent_WithNullDescription_DoesNotPopulateDescriptor()
    {
        var options = new TemporalAgentsOptions();
        var agent = new StubAIAgent("WeatherAgent"); // Description defaults to null

        options.AddAIAgent(agent);

        Assert.Empty(options.GetRegisteredDescriptors());
    }

    [Fact]
    public void AddAIAgent_ExplicitDescriptorOverridesAutoExtracted()
    {
        var options = new TemporalAgentsOptions();
        var agent = new StubAIAgent("WeatherAgent", description: "Auto description.");

        options.AddAIAgent(agent);
        options.AddAgentDescriptor("WeatherAgent", "Explicit override.");

        var descriptors = options.GetRegisteredDescriptors();
        Assert.Single(descriptors);
        Assert.Equal("Explicit override.", descriptors[0].Description);
    }

    [Fact]
    public void AddAIAgent_ExplicitDescriptorBeforeAgent_PreservesExplicit()
    {
        var options = new TemporalAgentsOptions();

        // Explicit descriptor registered first
        options.AddAgentDescriptor("WeatherAgent", "Pre-registered description.");

        // Agent added later — auto-extraction should NOT overwrite
        var agent = new StubAIAgent("WeatherAgent", description: "Agent description.");
        options.AddAIAgent(agent);

        var descriptors = options.GetRegisteredDescriptors();
        Assert.Single(descriptors);
        Assert.Equal("Pre-registered description.", descriptors[0].Description);
    }

    // ─── EnableSearchAttributes ─────────────────────────────────────────────

    [Fact]
    public void EnableSearchAttributes_DefaultsFalse()
    {
        var options = new TemporalAgentsOptions();
        Assert.False(options.EnableSearchAttributes);
    }

    [Fact]
    public void EnableSearchAttributes_CanBeSetToTrue()
    {
        var options = new TemporalAgentsOptions();
        options.EnableSearchAttributes = true;
        Assert.True(options.EnableSearchAttributes);
    }

    // ─── MaxHistorySize ─────────────────────────────────────────────────────

    [Fact]
    public void MaxHistorySize_DefaultIs1000()
    {
        var options = new TemporalAgentsOptions();
        Assert.Equal(1000, options.MaxHistorySize);
    }

    [Fact]
    public void MaxHistorySize_CanBeCustomized()
    {
        var options = new TemporalAgentsOptions();
        options.MaxHistorySize = 500;
        Assert.Equal(500, options.MaxHistorySize);
    }

    [Fact]
    public void HistoryReducer_DefaultsToNull()
    {
        var options = new TemporalAgentsOptions();
        Assert.Null(options.HistoryReducer);
    }
}
