using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Temporalio.Extensions.AI.Tests;

public class DurableChatActivitiesStreamingTests
{
    private static DurableChatActivities BuildActivities(IChatClient client)
    {
        var provider = new ServiceCollection()
            .AddSingleton(client)
            .BuildServiceProvider();
        return new DurableChatActivities(provider, loggerFactory: null);
    }

    private static DurableChatInput SimpleInput() =>
        new() { Messages = [new ChatMessage(ChatRole.User, "ping")] };

    [Fact]
    public async Task GetResponseAsync_UsesStreamingPath()
    {
        var client = new TrackingChatClient();
        var activities = BuildActivities(client);

        await activities.GetResponseAsync(SimpleInput());

        Assert.True(client.WasStreamingCalled);
        Assert.False(client.WasNonStreamingCalled);
    }

    [Fact]
    public async Task GetResponseAsync_AssemblesResponseFromStreamingUpdates()
    {
        var client = new MultiChunkChatClient();
        var activities = BuildActivities(client);

        var response = await activities.GetResponseAsync(SimpleInput());

        Assert.Single(response.Messages);
        Assert.Equal(ChatRole.Assistant, response.Messages[0].Role);
        // ToChatResponse() coalesces consecutive same-role updates.
        // ChatMessage.Text concatenates all TextContent items in the message.
        Assert.Equal("Hello world!", response.Messages[0].Text);
    }

    [Fact]
    public async Task GetResponseAsync_PropagatesExceptionFromStream()
    {
        var client = new ThrowingChatClient();
        var activities = BuildActivities(client);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => activities.GetResponseAsync(SimpleInput()));
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private sealed class TrackingChatClient : IChatClient
    {
        public bool WasStreamingCalled { get; private set; }
        public bool WasNonStreamingCalled { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            WasNonStreamingCalled = true;
            return Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, "ok")]));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            WasStreamingCalled = true;
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, "ok");
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class MultiChunkChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, "Hello world!")]));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, "Hello");
            yield return new ChatResponseUpdate(ChatRole.Assistant, " world");
            yield return new ChatResponseUpdate(ChatRole.Assistant, "!");
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class ThrowingChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, "ok")]));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            throw new OperationCanceledException("simulated cancellation");
            yield break; // satisfies compiler — unreachable
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
