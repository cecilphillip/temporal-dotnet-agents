using FakeItEasy;
using Microsoft.Extensions.AI;
using Xunit;

namespace Temporalio.Extensions.AI.Tests;

public class DurableChatClientTests
{
    [Fact]
    public async Task GetResponseAsync_PassesThroughWhenNotInWorkflow()
    {
        var expectedResponse = new ChatResponse([new ChatMessage(ChatRole.Assistant, "Hello!")]);
        var innerClient = A.Fake<IChatClient>();
        A.CallTo(() => innerClient.GetResponseAsync(
                A<IEnumerable<ChatMessage>>._, A<ChatOptions?>._, A<CancellationToken>._))
            .Returns(Task.FromResult(expectedResponse));

        var options = new DurableExecutionOptions { TaskQueue = "test" };
        var client = new DurableChatClient(innerClient, options);

        var messages = new List<ChatMessage> { new(ChatRole.User, "Hi") };
        var response = await client.GetResponseAsync(messages);

        Assert.Same(expectedResponse, response);
        A.CallTo(() => innerClient.GetResponseAsync(
                A<IEnumerable<ChatMessage>>._, A<ChatOptions?>._, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task GetStreamingResponseAsync_PassesThroughWhenNotInWorkflow()
    {
        // Create a response and convert to updates (avoids read-only Text setter).
        var response = new ChatResponse([new ChatMessage(ChatRole.Assistant, "Hello World")]);
        var updates = response.ToChatResponseUpdates().ToList();

        var innerClient = A.Fake<IChatClient>();
        A.CallTo(() => innerClient.GetStreamingResponseAsync(
                A<IEnumerable<ChatMessage>>._, A<ChatOptions?>._, A<CancellationToken>._))
            .Returns(updates.ToAsyncEnumerable());

        var options = new DurableExecutionOptions { TaskQueue = "test" };
        var client = new DurableChatClient(innerClient, options);

        var messages = new List<ChatMessage> { new(ChatRole.User, "Hi") };
        var result = new List<ChatResponseUpdate>();
        await foreach (var update in client.GetStreamingResponseAsync(messages))
        {
            result.Add(update);
        }

        Assert.NotEmpty(result);
    }

    [Fact]
    public void Constructor_ThrowsOnNullOptions()
    {
        var innerClient = A.Fake<IChatClient>();
        Assert.Throws<ArgumentNullException>(() => new DurableChatClient(innerClient, null!));
    }

    [Fact]
    public void GetService_ReturnsDurableExecutionOptions()
    {
        var innerClient = A.Fake<IChatClient>();
        var options = new DurableExecutionOptions { TaskQueue = "test" };
        var client = new DurableChatClient(innerClient, options);

        var result = client.GetService<DurableExecutionOptions>();
        Assert.Same(options, result);
    }

    [Fact]
    public async Task GetResponseAsync_StripsTemporalKeysBeforeForwardingToInner()
    {
        ChatOptions? capturedOptions = null;
        var expectedResponse = new ChatResponse([new ChatMessage(ChatRole.Assistant, "hi")]);
        var innerClient = A.Fake<IChatClient>();
        A.CallTo(() => innerClient.GetResponseAsync(
                A<IEnumerable<ChatMessage>>._, A<ChatOptions?>._, A<CancellationToken>._))
            .Invokes((IEnumerable<ChatMessage> _, ChatOptions? opts, CancellationToken _) =>
                capturedOptions = opts)
            .Returns(Task.FromResult(expectedResponse));

        var execOptions = new DurableExecutionOptions { TaskQueue = "test" };
        var client = new DurableChatClient(innerClient, execOptions);

        var chatOptions = new ChatOptions()
            .WithActivityTimeout(TimeSpan.FromMinutes(5))
            .WithHeartbeatTimeout(TimeSpan.FromMinutes(1))
            .WithMaxRetryAttempts(3);
        chatOptions.AdditionalProperties!["user.custom.key"] = "keep-me";

        var messages = new List<ChatMessage> { new(ChatRole.User, "hello") };
        await client.GetResponseAsync(messages, chatOptions);

        // The inner client must not see Temporal-internal keys.
        Assert.NotNull(capturedOptions?.AdditionalProperties);
        Assert.False(capturedOptions!.AdditionalProperties!.ContainsKey(TemporalChatOptionsExtensions.ActivityTimeoutKey));
        Assert.False(capturedOptions.AdditionalProperties.ContainsKey(TemporalChatOptionsExtensions.HeartbeatTimeoutKey));
        Assert.False(capturedOptions.AdditionalProperties.ContainsKey(TemporalChatOptionsExtensions.MaxRetryAttemptsKey));
        // Non-Temporal keys must be preserved.
        Assert.True(capturedOptions.AdditionalProperties.ContainsKey("user.custom.key"));
        Assert.Equal("keep-me", capturedOptions.AdditionalProperties["user.custom.key"]);
    }

    [Fact]
    public async Task GetResponseAsync_StripsChartClientKey_BeforeForwardingToInnerClient()
    {
        ChatOptions? capturedOptions = null;
        var expectedResponse = new ChatResponse([new ChatMessage(ChatRole.Assistant, "hi")]);
        var innerClient = A.Fake<IChatClient>();
        A.CallTo(() => innerClient.GetResponseAsync(
                A<IEnumerable<ChatMessage>>._, A<ChatOptions?>._, A<CancellationToken>._))
            .Invokes((IEnumerable<ChatMessage> _, ChatOptions? opts, CancellationToken _) =>
                capturedOptions = opts)
            .Returns(Task.FromResult(expectedResponse));

        var execOptions = new DurableExecutionOptions { TaskQueue = "test" };
        var client = new DurableChatClient(innerClient, execOptions);

        // Set ChatClientKey AND one non-Temporal additional property.
        var chatOptions = new ChatOptions().WithChatClientKey("gpt-4o");
        chatOptions.AdditionalProperties!["custom-key"] = "custom-value";

        var messages = new List<ChatMessage> { new(ChatRole.User, "hello") };
        await client.GetResponseAsync(messages, chatOptions);

        // ChatClientKey must not leak to the inner client.
        Assert.NotNull(capturedOptions?.AdditionalProperties);
        Assert.False(capturedOptions!.AdditionalProperties!.ContainsKey(TemporalChatOptionsExtensions.ChatClientKeyKey));
        // Non-Temporal key must be preserved.
        Assert.True(capturedOptions.AdditionalProperties.ContainsKey("custom-key"));
        Assert.Equal("custom-value", capturedOptions.AdditionalProperties["custom-key"]);
    }
}
