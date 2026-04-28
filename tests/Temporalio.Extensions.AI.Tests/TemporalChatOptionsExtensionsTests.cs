using Microsoft.Extensions.AI;
using Xunit;

namespace Temporalio.Extensions.AI.Tests;

public class TemporalChatOptionsExtensionsTests
{
    [Fact]
    public void WithActivityTimeout_SetsProperty()
    {
        var options = new ChatOptions();
        options.WithActivityTimeout(TimeSpan.FromMinutes(10));

        Assert.NotNull(options.AdditionalProperties);
        Assert.Equal(TimeSpan.FromMinutes(10), options.GetActivityTimeout());
    }

    [Fact]
    public void WithMaxRetryAttempts_SetsProperty()
    {
        var options = new ChatOptions();
        options.WithMaxRetryAttempts(5);

        Assert.NotNull(options.AdditionalProperties);
        Assert.Equal(5, options.GetMaxRetryAttempts());
    }

    [Fact]
    public void WithHeartbeatTimeout_SetsProperty()
    {
        var options = new ChatOptions();
        options.WithHeartbeatTimeout(TimeSpan.FromMinutes(3));

        Assert.NotNull(options.AdditionalProperties);
        Assert.Equal(TimeSpan.FromMinutes(3), options.GetHeartbeatTimeout());
    }

    [Fact]
    public void GetActivityTimeout_ReturnsNullWhenNotSet()
    {
        var options = new ChatOptions();
        Assert.Null(options.GetActivityTimeout());
    }

    [Fact]
    public void GetActivityTimeout_ReturnsNullForNullOptions()
    {
        ChatOptions? options = null;
        Assert.Null(options.GetActivityTimeout());
    }

    [Fact]
    public void GetActivityTimeout_ReturnsValueWhenSet()
    {
        var options = new ChatOptions();
        options.WithActivityTimeout(TimeSpan.FromMinutes(15));
        Assert.Equal(TimeSpan.FromMinutes(15), options.GetActivityTimeout());
    }

    [Fact]
    public void GetMaxRetryAttempts_ReturnsValueWhenSet()
    {
        var options = new ChatOptions();
        options.WithMaxRetryAttempts(3);
        Assert.Equal(3, options.GetMaxRetryAttempts());
    }

    [Fact]
    public void GetMaxRetryAttempts_ReturnsNullWhenNotSet()
    {
        var options = new ChatOptions();
        Assert.Null(options.GetMaxRetryAttempts());
    }

    [Fact]
    public void GetHeartbeatTimeout_ReturnsValueWhenSet()
    {
        var options = new ChatOptions();
        options.WithHeartbeatTimeout(TimeSpan.FromSeconds(30));
        Assert.Equal(TimeSpan.FromSeconds(30), options.GetHeartbeatTimeout());
    }

    [Fact]
    public void FluentChaining_Works()
    {
        var options = new ChatOptions()
            .WithActivityTimeout(TimeSpan.FromMinutes(10))
            .WithMaxRetryAttempts(3)
            .WithHeartbeatTimeout(TimeSpan.FromMinutes(2));

        Assert.Equal(TimeSpan.FromMinutes(10), options.GetActivityTimeout());
        Assert.Equal(3, options.GetMaxRetryAttempts());
        Assert.Equal(TimeSpan.FromMinutes(2), options.GetHeartbeatTimeout());
    }

    [Fact]
    public void WithActivityTimeout_ThrowsOnNull()
    {
        Assert.Throws<ArgumentNullException>(
            () => TemporalChatOptionsExtensions.WithActivityTimeout(null!, TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public void Constants_AreCorrect()
    {
        Assert.Equal("temporal.activity.timeout", TemporalChatOptionsExtensions.ActivityTimeoutKey);
        Assert.Equal("temporal.retry.max_attempts", TemporalChatOptionsExtensions.MaxRetryAttemptsKey);
        Assert.Equal("temporal.heartbeat.timeout", TemporalChatOptionsExtensions.HeartbeatTimeoutKey);
    }

    [Fact]
    public void WithChatClientKey_SetsKeyInAdditionalProperties()
    {
        var options = new ChatOptions();
        options.WithChatClientKey("my-client");

        Assert.NotNull(options.AdditionalProperties);
        Assert.True(options.AdditionalProperties.ContainsKey(TemporalChatOptionsExtensions.ChatClientKeyKey));
        Assert.Equal("my-client", options.AdditionalProperties[TemporalChatOptionsExtensions.ChatClientKeyKey]);
    }

    [Fact]
    public void WithChatClientKey_ReturnsOptionsForChaining()
    {
        var options = new ChatOptions();
        var returned = options.WithChatClientKey("my-client");

        Assert.Same(options, returned);
    }

    [Fact]
    public void WithChatClientKey_ThrowsOnNullKey()
    {
        var options = new ChatOptions();
        Assert.Throws<ArgumentException>(() => options.WithChatClientKey(null!));
    }

    [Fact]
    public void WithChatClientKey_ThrowsOnEmptyKey()
    {
        var options = new ChatOptions();
        Assert.Throws<ArgumentException>(() => options.WithChatClientKey(""));
    }

    [Fact]
    public void GetChatClientKey_ReturnsNullWhenNotSet()
    {
        var options = new ChatOptions();
        Assert.Null(options.GetChatClientKey());
    }

    [Fact]
    public void GetChatClientKey_ReturnsKeyWhenSet()
    {
        var options = new ChatOptions();
        options.WithChatClientKey("my-client");
        Assert.Equal("my-client", options.GetChatClientKey());
    }

    [Fact]
    public async Task StripTemporalOptions_RemovesChatClientKey()
    {
        var options = new ChatOptions()
            .WithActivityTimeout(TimeSpan.FromMinutes(5))
            .WithChatClientKey("my-client");
        options.AdditionalProperties!["user.custom"] = "keep";

        // StripTemporalOptions is internal — exercise it through DurableChatClient pass-through
        // (not in workflow context) so the inner client receives stripped options.
        var captured = (ChatOptions?)null;
        var innerClient = new CapturingChatClient(opts => captured = opts);
        var execOptions = new DurableExecutionOptions { TaskQueue = "test" };
        var client = new DurableChatClient(innerClient, execOptions);

        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], options);

        Assert.NotNull(captured?.AdditionalProperties);
        Assert.False(captured!.AdditionalProperties!.ContainsKey(TemporalChatOptionsExtensions.ChatClientKeyKey));
        Assert.False(captured.AdditionalProperties.ContainsKey(TemporalChatOptionsExtensions.ActivityTimeoutKey));
        Assert.True(captured.AdditionalProperties.ContainsKey("user.custom"));
    }

    /// <summary>Minimal IChatClient stub that captures the ChatOptions it receives.</summary>
    private sealed class CapturingChatClient(Action<ChatOptions?> capture) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            capture(options);
            return Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, "ok")]));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) => AsyncEnumerable.Empty<ChatResponseUpdate>();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
