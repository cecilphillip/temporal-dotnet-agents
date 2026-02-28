using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Temporalio.Extensions.Agents.Tests.Helpers;
using Xunit;

namespace Temporalio.Extensions.Agents.Tests;

public class LlmAgentRouterTests
{
    private static readonly List<AgentDescriptor> Descriptors =
    [
        new("WeatherAgent", "Answers questions about weather and forecasts."),
        new("BillingAgent", "Handles billing inquiries and invoice requests."),
        new("TechSupport", "Troubleshoots technical issues and bugs."),
    ];

    /// <summary>Creates an AgentResponse whose Text property returns <paramref name="text"/>.</summary>
    private static AgentResponse ResponseWithText(string text) =>
        new()
        {
            Messages = [new ChatMessage(ChatRole.Assistant, text)],
            CreatedAt = DateTimeOffset.UtcNow
        };

    [Fact]
    public async Task RouteAsync_ExactMatch_ReturnsAgentName()
    {
        var routerAgent = new StubAIAgent("__router__", ResponseWithText("WeatherAgent"));
        var router = new LlmAgentRouter(routerAgent);

        var messages = new List<ChatMessage> { new(ChatRole.User, "What is the weather today?") };
        var result = await router.RouteAsync(messages, Descriptors);

        Assert.Equal("WeatherAgent", result);
    }

    [Fact]
    public async Task RouteAsync_FuzzyMatch_ReturnsAgentName()
    {
        var routerAgent = new StubAIAgent("__router__",
            ResponseWithText("I think BillingAgent would be best for this."));
        var router = new LlmAgentRouter(routerAgent);

        var messages = new List<ChatMessage> { new(ChatRole.User, "I have a question about my invoice.") };
        var result = await router.RouteAsync(messages, Descriptors);

        Assert.Equal("BillingAgent", result);
    }

    [Fact]
    public async Task RouteAsync_UnknownName_ThrowsInvalidOperationException()
    {
        var routerAgent = new StubAIAgent("__router__", ResponseWithText("HallucinatedAgent"));
        var router = new LlmAgentRouter(routerAgent);
        var messages = new List<ChatMessage> { new(ChatRole.User, "Help me.") };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            router.RouteAsync(messages, Descriptors));
    }

    [Fact]
    public async Task RouteAsync_EmptyDescriptors_ThrowsInvalidOperationException()
    {
        var routerAgent = new StubAIAgent("__router__", ResponseWithText("WeatherAgent"));
        var router = new LlmAgentRouter(routerAgent);
        var messages = new List<ChatMessage> { new(ChatRole.User, "Test") };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            router.RouteAsync(messages, []));
    }

    [Fact]
    public async Task RouteAsync_CaseInsensitiveMatch_Works()
    {
        // Router returns name in lower case — fuzzy match is case-insensitive.
        var routerAgent = new StubAIAgent("__router__", ResponseWithText("techsupport"));
        var router = new LlmAgentRouter(routerAgent);
        var messages = new List<ChatMessage> { new(ChatRole.User, "My app crashed.") };

        var result = await router.RouteAsync(messages, Descriptors);

        Assert.Equal("TechSupport", result, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RouteAsync_NullMessages_Throws()
    {
        var routerAgent = new StubAIAgent("__router__", ResponseWithText("WeatherAgent"));
        var router = new LlmAgentRouter(routerAgent);

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            router.RouteAsync(null!, Descriptors));
    }

    [Fact]
    public async Task RouteAsync_NullAgents_Throws()
    {
        var routerAgent = new StubAIAgent("__router__", ResponseWithText("WeatherAgent"));
        var router = new LlmAgentRouter(routerAgent);
        var messages = new List<ChatMessage> { new(ChatRole.User, "Test") };

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            router.RouteAsync(messages, null!));
    }
}
