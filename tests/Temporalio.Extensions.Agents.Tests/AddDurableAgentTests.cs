using Microsoft.Extensions.AI;
using Temporalio.Extensions.Agents.Tests.Helpers;
using Xunit;

namespace Temporalio.Extensions.Agents.Tests;

/// <summary>
/// Phase 2 (v0.3 API redesign): coverage for <see cref="TemporalAgentsOptions.AddDurableAgent"/>
/// registration semantics. Validates the four checkpoints listed in Q9 of the plan, plus the
/// introspection wiring that makes durable agents visible to <c>GetRegisteredAgentNames</c> and
/// <c>GetAgentDescriptors</c>.
/// </summary>
public class AddDurableAgentTests
{
    private static IChatClient NewChatClient() => new TestChatClient();

    [Fact]
    public void AddDurableAgent_WithName_RunsConfigureDelegate()
    {
        var options = new TemporalAgentsOptions();
        var invocationCount = 0;
        DurableAgentBuilder? observed = null;

        options.AddDurableAgent("MyAgent", agent =>
        {
            invocationCount++;
            observed = agent;
            agent.ChatClient = _ => NewChatClient();
        });

        Assert.Equal(1, invocationCount);
        Assert.NotNull(observed);
        Assert.Equal("MyAgent", observed!.Name);
    }

    [Fact]
    public void AddDurableAgent_WithEmptyName_ThrowsArgumentException()
    {
        var options = new TemporalAgentsOptions();
        Assert.Throws<ArgumentException>(() =>
            options.AddDurableAgent(string.Empty, _ => { }));
    }

    [Fact]
    public void AddDurableAgent_WithWhitespaceName_ThrowsArgumentException()
    {
        var options = new TemporalAgentsOptions();
        Assert.Throws<ArgumentException>(() =>
            options.AddDurableAgent("   ", _ => { }));
    }

    [Fact]
    public void AddDurableAgent_WithNullName_ThrowsArgumentNullException()
    {
        var options = new TemporalAgentsOptions();
        Assert.Throws<ArgumentNullException>(() =>
            options.AddDurableAgent(null!, _ => { }));
    }

    [Fact]
    public void AddDurableAgent_WithNullConfigure_ThrowsArgumentNullException()
    {
        var options = new TemporalAgentsOptions();
        Assert.Throws<ArgumentNullException>(() =>
            options.AddDurableAgent("X", null!));
    }

    [Fact]
    public void AddDurableAgent_WithoutChatClient_ThrowsInvalidOperationException()
    {
        var options = new TemporalAgentsOptions();
        var ex = Assert.Throws<InvalidOperationException>(() =>
            options.AddDurableAgent("NoClient", _ => { /* no ChatClient assignment */ }));
        Assert.Contains("NoClient", ex.Message);
        Assert.Contains("ChatClient", ex.Message);
    }

    [Fact]
    public void AddDurableAgent_DuplicateName_ThrowsInvalidOperationException()
    {
        var options = new TemporalAgentsOptions();
        options.AddDurableAgent("Dup", agent => agent.ChatClient = _ => NewChatClient());

        var ex = Assert.Throws<InvalidOperationException>(() =>
            options.AddDurableAgent("Dup", agent => agent.ChatClient = _ => NewChatClient()));
        Assert.Contains("Dup", ex.Message);
    }

    [Fact]
    public void AddDurableAgent_DuplicatesProxy_ThrowsInvalidOperationException()
    {
        var options = new TemporalAgentsOptions();
        options.AddAgentProxy("Shared");

        Assert.Throws<InvalidOperationException>(() =>
            options.AddDurableAgent("Shared", agent => agent.ChatClient = _ => NewChatClient()));
    }

    [Fact]
    public void AddDurableAgent_DuplicateNameIsCaseInsensitive()
    {
        var options = new TemporalAgentsOptions();
        options.AddDurableAgent("MyAgent", agent => agent.ChatClient = _ => NewChatClient());

        Assert.Throws<InvalidOperationException>(() =>
            options.AddDurableAgent("MYAGENT", agent => agent.ChatClient = _ => NewChatClient()));
    }

    [Fact]
    public void AddDurableAgent_RegistrationStoresFlattenedState()
    {
        var options = new TemporalAgentsOptions();
        var chatClient = NewChatClient();
        var chatOptions = new ChatOptions { Temperature = 0.42f };

        options.AddDurableAgent("Stored", agent =>
        {
            agent.Description = "a stored agent";
            agent.Instructions = "be helpful";
            agent.ChatClient = _ => chatClient;
            agent.ChatOptions = chatOptions;
            agent.MaxToolCallsPerTurn = 9;
        });

        var registration = Assert.Single(options.DurableAgentRegistrations);
        Assert.Equal("Stored", registration.Key);
        Assert.Equal("Stored", registration.Value.Name);
        Assert.Equal("a stored agent", registration.Value.Description);
        Assert.Equal("be helpful", registration.Value.Instructions);
        Assert.Same(chatClient, registration.Value.ChatClient(null!));
        Assert.Equal(0.42f, registration.Value.ChatOptions!.Temperature);
        Assert.Equal(9, registration.Value.MaxToolCallsPerTurn);
    }

    [Fact]
    public void AddDurableAgent_GetRegisteredAgentNames_IncludesDurableAgent()
    {
        var options = new TemporalAgentsOptions();
        options.AddDurableAgent("Listed", agent => agent.ChatClient = _ => NewChatClient());

        var names = options.GetRegisteredAgentNames();
        Assert.Contains("Listed", names);
    }

    [Fact]
    public void AddDurableAgent_IsAgentRegistered_ReturnsTrue()
    {
        var options = new TemporalAgentsOptions();
        options.AddDurableAgent("Looked", agent => agent.ChatClient = _ => NewChatClient());

        Assert.True(options.IsAgentRegistered("Looked"));
    }

    [Fact]
    public void AddDurableAgent_GetAgentDescriptors_IncludesDurableAgentWithDescription()
    {
        var options = new TemporalAgentsOptions();
        options.AddDurableAgent("Described", agent =>
        {
            agent.Description = "specialist agent";
            agent.ChatClient = _ => NewChatClient();
        });

        var descriptors = options.GetAgentDescriptors();
        var descriptor = Assert.Single(descriptors);
        Assert.Equal("Described", descriptor.Name);
        Assert.Equal("specialist agent", descriptor.Description);
    }

    [Fact]
    public void AddDurableAgent_DescriptionlessAgent_OmittedFromDescriptors()
    {
        // Mirrors the legacy behavior — an agent with no description is excluded from the
        // routing prompt list. This keeps classifier/utility agents out of dispatch prompts.
        var options = new TemporalAgentsOptions();
        options.AddDurableAgent("Anonymous", agent => agent.ChatClient = _ => NewChatClient());

        Assert.Empty(options.GetAgentDescriptors());
    }

    [Fact]
    public void AddDurableAgent_ReturnsSameOptionsInstance()
    {
        var options = new TemporalAgentsOptions();
        var returned = options.AddDurableAgent("Chained", agent => agent.ChatClient = _ => NewChatClient());
        Assert.Same(options, returned);
    }

    private sealed class TestChatClient : IChatClient
    {
        public void Dispose()
        {
        }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
    }
}
