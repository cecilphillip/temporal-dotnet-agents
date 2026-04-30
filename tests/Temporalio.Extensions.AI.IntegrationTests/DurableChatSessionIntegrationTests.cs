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
        // The response carries the model's last assistant message.
        Assert.Contains("Hello AI!", response.Text);
        // Per-turn usage details flow through the entry.
        Assert.NotNull(response.Usage);
        // CorrelationId is auto-generated when not supplied.
        Assert.False(string.IsNullOrEmpty(response.CorrelationId));
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

        // Each turn produces a request entry + a response entry → 4 entries for 2 turns.
        Assert.Equal(4, history.Count);
        Assert.IsType<DurableSessionRequest>(history[0]);
        Assert.IsType<DurableSessionResponse>(history[1]);
        Assert.IsType<DurableSessionRequest>(history[2]);
        Assert.IsType<DurableSessionResponse>(history[3]);

        // Request and response of the same turn share a correlation ID.
        Assert.Equal(history[0].CorrelationId, history[1].CorrelationId);
        Assert.Equal(history[2].CorrelationId, history[3].CorrelationId);
        // Different turns produce different correlation IDs.
        Assert.NotEqual(history[0].CorrelationId, history[2].CorrelationId);
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

    [Fact]
    public async Task UsageDetails_AreQueryablePerTurn_ViaGetHistory()
    {
        var conversationId = $"usage-history-{Guid.NewGuid():N}";

        await _fixture.SessionClient.ChatAsync(
            conversationId,
            [new ChatMessage(ChatRole.User, "First")]);

        await _fixture.SessionClient.ChatAsync(
            conversationId,
            [new ChatMessage(ChatRole.User, "Second")]);

        var history = await _fixture.SessionClient.GetHistoryAsync(conversationId);

        // Each response entry carries the per-turn UsageDetails.
        var responseEntries = history.OfType<DurableSessionResponse>().ToList();
        Assert.Equal(2, responseEntries.Count);
        foreach (var entry in responseEntries)
        {
            Assert.NotNull(entry.Usage);
            Assert.True(entry.Usage!.InputTokenCount > 0);
            Assert.True(entry.Usage!.OutputTokenCount > 0);
        }
    }

    [Fact]
    public async Task UserSuppliedCorrelationId_IsPreserved_OnRequestAndResponseEntries()
    {
        var conversationId = $"correlation-{Guid.NewGuid():N}";
        var customCorrelationId = "trace-abc-123";

        var response = await _fixture.SessionClient.ChatAsync(
            conversationId,
            [new ChatMessage(ChatRole.User, "hello")],
            options: null,
            correlationId: customCorrelationId);

        Assert.Equal(customCorrelationId, response.CorrelationId);

        var history = await _fixture.SessionClient.GetHistoryAsync(conversationId);
        Assert.Equal(2, history.Count);
        Assert.Equal(customCorrelationId, history[0].CorrelationId);
        Assert.Equal(customCorrelationId, history[1].CorrelationId);
    }

    [Fact]
    public async Task NullCorrelationId_AutoGeneratesGuid()
    {
        var conversationId = $"correlation-auto-{Guid.NewGuid():N}";

        var response = await _fixture.SessionClient.ChatAsync(
            conversationId,
            [new ChatMessage(ChatRole.User, "hello")]);

        // Auto-generated correlation IDs are 32-char hex (Guid "N" format).
        Assert.False(string.IsNullOrEmpty(response.CorrelationId));
        Assert.Equal(32, response.CorrelationId.Length);
    }
}
