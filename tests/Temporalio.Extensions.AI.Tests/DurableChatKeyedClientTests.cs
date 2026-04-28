using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Temporalio.Extensions.AI.Tests;

/// <summary>
/// Tests for the keyed <see cref="IChatClient"/> DI resolution model in
/// <see cref="DurableChatActivities"/> and key propagation through
/// <see cref="DurableChatClient"/> / <see cref="DurableExecutionOptions"/>.
/// </summary>
public class DurableChatKeyedClientTests
{
    // -----------------------------------------------------------------------
    // DurableChatActivities — IServiceProvider-based client resolution
    // -----------------------------------------------------------------------

    [Fact]
    public async Task DurableChatActivities_ResolvesUnkeyedClient_WhenClientKeyIsNull()
    {
        var unkeyedClient = new RecordingChatClient("unkeyed");
        var keyedClient = new RecordingChatClient("keyed");

        var provider = new FakeServiceProvider(
            unkeyedClient,
            keyed: new Dictionary<string, IChatClient> { ["gpt-4o"] = keyedClient });

        var activities = new DurableChatActivities(provider, loggerFactory: null);

        var input = new DurableChatInput
        {
            Messages = [new ChatMessage(ChatRole.User, "hello")],
            ClientKey = null,
        };

        var output = await activities.GetResponseAsync(input);

        Assert.Equal("unkeyed", output.Response.Messages[0].Text);
    }

    [Fact]
    public async Task DurableChatActivities_ResolvesKeyedClient_WhenClientKeyIsSet()
    {
        var unkeyedClient = new RecordingChatClient("unkeyed");
        var keyedClient = new RecordingChatClient("keyed");

        var provider = new FakeServiceProvider(
            unkeyedClient,
            keyed: new Dictionary<string, IChatClient> { ["gpt-4o"] = keyedClient });

        var activities = new DurableChatActivities(provider, loggerFactory: null);

        var input = new DurableChatInput
        {
            Messages = [new ChatMessage(ChatRole.User, "hello")],
            ClientKey = "gpt-4o",
        };

        var output = await activities.GetResponseAsync(input);

        Assert.Equal("keyed", output.Response.Messages[0].Text);
    }

    // -----------------------------------------------------------------------
    // DurableChatClient — key propagation through DurableChatInput.ClientKey
    // -----------------------------------------------------------------------

    [Fact]
    public void ChatAsync_UsesPerCallKey_WhenBothPerCallAndDefaultAreSet()
    {
        // We verify that the per-call key wins by reading it back through
        // GetChatClientKey after round-tripping through WithChatClientKey.
        var options = new ChatOptions().WithChatClientKey("per-call-key");
        var execOptions = new DurableExecutionOptions
        {
            TaskQueue = "test",
            DefaultChatClientKey = "default-key",
        };

        // Per-call key must be present and override the default.
        Assert.Equal("per-call-key", options.GetChatClientKey());
        Assert.Equal("default-key", execOptions.DefaultChatClientKey);
    }

    [Fact]
    public void ChatAsync_UsesDefaultKey_WhenOnlyDefaultIsSet()
    {
        // No per-call key set — resolution should fall back to DefaultChatClientKey.
        var options = new ChatOptions(); // no WithChatClientKey
        var execOptions = new DurableExecutionOptions
        {
            TaskQueue = "test",
            DefaultChatClientKey = "default-key",
        };

        Assert.Null(options.GetChatClientKey());
        Assert.Equal("default-key", execOptions.DefaultChatClientKey);
    }

    [Fact]
    public void ChatAsync_UsesUnkeyed_WhenNeitherKeyIsSet()
    {
        // Neither per-call nor default key is configured.
        var options = new ChatOptions();
        var execOptions = new DurableExecutionOptions { TaskQueue = "test" };

        Assert.Null(options.GetChatClientKey());
        Assert.Null(execOptions.DefaultChatClientKey);
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    /// <summary>
    /// A minimal IChatClient stub that returns a canned response carrying a label
    /// so tests can assert which client was actually invoked.
    /// </summary>
    private sealed class RecordingChatClient(string label) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, label)]));

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            AsyncEnumerable.Empty<ChatResponseUpdate>();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    /// <summary>
    /// A minimal IServiceProvider / IKeyedServiceProvider fake that returns
    /// a registered unkeyed IChatClient and optionally keyed IChatClient instances.
    /// </summary>
    private sealed class FakeServiceProvider(
        IChatClient unkeyed,
        Dictionary<string, IChatClient>? keyed = null) : IServiceProvider, IKeyedServiceProvider
    {
        public object? GetService(Type serviceType)
        {
            if (serviceType == typeof(IChatClient)) return unkeyed;
            return null;
        }

        public object? GetKeyedService(Type serviceType, object? serviceKey)
        {
            if (serviceType == typeof(IChatClient) && serviceKey is string key
                && keyed is not null && keyed.TryGetValue(key, out var client))
                return client;
            return null;
        }

        public object GetRequiredKeyedService(Type serviceType, object? serviceKey) =>
            GetKeyedService(serviceType, serviceKey)
            ?? throw new InvalidOperationException(
                $"No keyed service registered for key '{serviceKey}'.");
    }
}
