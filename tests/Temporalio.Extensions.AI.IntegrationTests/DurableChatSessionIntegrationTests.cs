using Microsoft.Extensions.AI;
using Temporalio.Extensions.AI.IntegrationTests.Helpers;
using Xunit;

namespace Temporalio.Extensions.AI.IntegrationTests;

public class DurableChatSessionIntegrationTests : IClassFixture<IntegrationTestFixture>
{
    private readonly IntegrationTestFixture _fixture;

    public DurableChatSessionIntegrationTests(IntegrationTestFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task SingleTurn_SendsMessageAndReceivesResponse()
    {
        var conversationId = $"single-turn-{Guid.NewGuid():N}";
        var messages = new List<ChatMessage> { new(ChatRole.User, "Hello AI!") };

        var response = await _fixture.SessionClient.ChatAsync(conversationId, messages);

        Assert.NotNull(response);
        Assert.Single(response.Messages);
        Assert.Contains("Hello AI!", response.Messages[0].Text);
        Assert.Equal("test-model", response.ModelId);
    }

    [Fact]
    public async Task MultiTurn_AccumulatesHistory()
    {
        var conversationId = $"multi-turn-{Guid.NewGuid():N}";

        // Turn 1
        var response1 = await _fixture.SessionClient.ChatAsync(
            conversationId,
            [new ChatMessage(ChatRole.User, "First message")]);

        Assert.NotNull(response1);

        // Turn 2
        var response2 = await _fixture.SessionClient.ChatAsync(
            conversationId,
            [new ChatMessage(ChatRole.User, "Second message")]);

        Assert.NotNull(response2);

        // Query history
        var history = await _fixture.SessionClient.GetHistoryAsync(conversationId);

        // Should have: user1 + assistant1 + user2 + assistant2 = 4 messages
        Assert.Equal(4, history.Count);
    }

    [Fact]
    public async Task SameConversationId_ReusesSameWorkflow()
    {
        var conversationId = $"reuse-{Guid.NewGuid():N}";

        await _fixture.SessionClient.ChatAsync(
            conversationId,
            [new ChatMessage(ChatRole.User, "First")]);

        await _fixture.SessionClient.ChatAsync(
            conversationId,
            [new ChatMessage(ChatRole.User, "Second")]);

        // Both should be in the same workflow — verify via history
        var history = await _fixture.SessionClient.GetHistoryAsync(conversationId);
        Assert.Equal(4, history.Count);
    }

    [Fact]
    public async Task TokenUsage_IsReported()
    {
        var conversationId = $"usage-{Guid.NewGuid():N}";
        var response = await _fixture.SessionClient.ChatAsync(
            conversationId,
            [new ChatMessage(ChatRole.User, "test")]);

        Assert.NotNull(response.Usage);
        Assert.True(response.Usage!.InputTokenCount > 0);
        Assert.True(response.Usage!.OutputTokenCount > 0);
    }
}
