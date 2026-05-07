using FakeItEasy;
using Microsoft.Extensions.AI;
using Xunit;

namespace Temporalio.Extensions.Agents.Tests;

public class TemporalAgentsOptionsTests
{
    private static Func<IServiceProvider, IChatClient> NewChatClient() =>
        _ => A.Fake<IChatClient>();

    [Fact]
    public void DefaultTimeToLive_Is14Days()
    {
        var options = new TemporalAgentsOptions();
        Assert.Equal(TimeSpan.FromDays(14), options.DefaultTimeToLive);
    }

    [Fact]
    public void DefaultActivityTimeout_Is5Minutes()
    {
        var options = new TemporalAgentsOptions();
        Assert.Equal(TimeSpan.FromMinutes(5), options.DefaultActivityTimeout);
    }

    [Fact]
    public void DefaultHeartbeatTimeout_Is2Minutes()
    {
        var options = new TemporalAgentsOptions();
        Assert.Equal(TimeSpan.FromMinutes(2), options.DefaultHeartbeatTimeout);
    }

    [Fact]
    public void DefaultApprovalTimeout_Is7Days()
    {
        var options = new TemporalAgentsOptions();
        Assert.Equal(TimeSpan.FromDays(7), options.DefaultApprovalTimeout);
    }

    [Fact]
    public void DefaultMaxEntryCount_Is1000()
    {
        var options = new TemporalAgentsOptions();
        Assert.Equal(1000, options.DefaultMaxEntryCount);
    }

    [Fact]
    public void DefaultRetryPolicy_DefaultsToNull()
    {
        var options = new TemporalAgentsOptions();
        Assert.Null(options.DefaultRetryPolicy);
    }

    [Fact]
    public void DefaultHistoryReducer_DefaultsToNull()
    {
        var options = new TemporalAgentsOptions();
        Assert.Null(options.DefaultHistoryReducer);
    }

    [Fact]
    public void EnableSearchAttributes_DefaultsFalse()
    {
        var options = new TemporalAgentsOptions();
        Assert.False(options.EnableSearchAttributes);
    }

    [Fact]
    public void GetTimeToLive_NoPerAgentTtl_FallsBackToDefault()
    {
        var options = new TemporalAgentsOptions { DefaultTimeToLive = TimeSpan.FromDays(7) };
        options.AddDurableAgent("Agent", agent => agent.ChatClient = NewChatClient());

        Assert.Equal(TimeSpan.FromDays(7), options.GetTimeToLive("Agent"));
    }

    [Fact]
    public void GetTimeToLive_PerAgentTtl_OverridesDefault()
    {
        var options = new TemporalAgentsOptions { DefaultTimeToLive = TimeSpan.FromDays(14) };
        var perAgentTtl = TimeSpan.FromHours(1);

        options.AddDurableAgent("FastAgent", agent =>
        {
            agent.ChatClient = NewChatClient();
            agent.TimeToLive = perAgentTtl;
        });

        Assert.Equal(perAgentTtl, options.GetTimeToLive("FastAgent"));
    }

    [Fact]
    public void GetTimeToLive_UnknownAgent_ReturnsDefault()
    {
        var options = new TemporalAgentsOptions { DefaultTimeToLive = TimeSpan.FromDays(3) };
        Assert.Equal(TimeSpan.FromDays(3), options.GetTimeToLive("NonExistentAgent"));
    }

    [Fact]
    public void GetRegisteredAgentNames_ReturnsAllNames()
    {
        var options = new TemporalAgentsOptions();
        options.AddDurableAgent("Alpha", agent => agent.ChatClient = NewChatClient());
        options.AddDurableAgent("Beta", agent => agent.ChatClient = NewChatClient());
        options.AddAgentProxy("Gamma");

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
    public void IsAgentRegistered_DurableAgent_ReturnsTrue()
    {
        var options = new TemporalAgentsOptions();
        options.AddDurableAgent("MyAgent", agent => agent.ChatClient = NewChatClient());
        Assert.True(options.IsAgentRegistered("MyAgent"));
    }

    [Fact]
    public void IsAgentRegistered_ProxyAgent_ReturnsTrue()
    {
        var options = new TemporalAgentsOptions();
        options.AddAgentProxy("ProxyAgent");
        Assert.True(options.IsAgentRegistered("ProxyAgent"));
    }

    [Fact]
    public void IsAgentRegistered_CaseInsensitive()
    {
        var options = new TemporalAgentsOptions();
        options.AddDurableAgent("MyAgent", agent => agent.ChatClient = NewChatClient());

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
    public void AddDurableAgent_WithDescription_AppearsInGetAgentDescriptors()
    {
        var options = new TemporalAgentsOptions();
        options.AddDurableAgent("SpecialistAgent", agent =>
        {
            agent.Description = "Handles specialist tasks.";
            agent.ChatClient = NewChatClient();
        });

        var descriptors = options.GetAgentDescriptors();
        Assert.Single(descriptors);
        Assert.Equal("SpecialistAgent", descriptors[0].Name);
        Assert.Equal("Handles specialist tasks.", descriptors[0].Description);
    }

    [Fact]
    public void AddDurableAgent_WithoutDescription_ExcludedFromGetAgentDescriptors()
    {
        var options = new TemporalAgentsOptions();
        options.AddDurableAgent("NoDescriptionAgent", agent => agent.ChatClient = NewChatClient());
        Assert.Empty(options.GetAgentDescriptors());
    }

    [Fact]
    public void GetAgentDescription_ReturnsStoredDescription()
    {
        var options = new TemporalAgentsOptions();
        options.AddDurableAgent("MyAgent", agent =>
        {
            agent.Description = "Does something useful.";
            agent.ChatClient = NewChatClient();
        });

        Assert.Equal("Does something useful.", options.GetAgentDescription("MyAgent"));
    }

    [Fact]
    public void GetAgentDescription_CaseInsensitive()
    {
        var options = new TemporalAgentsOptions();
        options.AddDurableAgent("MyAgent", agent =>
        {
            agent.Description = "Does something useful.";
            agent.ChatClient = NewChatClient();
        });

        Assert.Equal("Does something useful.", options.GetAgentDescription("myagent"));
    }

    [Fact]
    public void GetAgentDescription_UnknownAgent_ReturnsNull()
    {
        var options = new TemporalAgentsOptions();
        Assert.Null(options.GetAgentDescription("DoesNotExist"));
    }

    [Fact]
    public void GetAgentDescription_NullOrEmpty_ReturnsNull()
    {
        var options = new TemporalAgentsOptions();
        Assert.Null(options.GetAgentDescription(null!));
        Assert.Null(options.GetAgentDescription(""));
    }

    [Fact]
    public void AddAgentProxy_DuplicateName_Throws()
    {
        var options = new TemporalAgentsOptions();
        options.AddAgentProxy("Agent");

        Assert.Throws<ArgumentException>(() => options.AddAgentProxy("Agent"));
    }
}
