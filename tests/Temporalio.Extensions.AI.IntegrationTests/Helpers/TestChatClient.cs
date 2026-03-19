using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace Temporalio.Extensions.AI.IntegrationTests.Helpers;

/// <summary>
/// A simple <see cref="IChatClient"/> that returns canned responses.
/// Tracks call count and arguments for assertions.
/// </summary>
public sealed class TestChatClient : IChatClient
{
    private int _callCount;

    public int CallCount => _callCount;
    public List<IList<ChatMessage>> ReceivedMessages { get; } = [];
    public string ResponsePrefix { get; set; } = "Response";

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var messageList = messages.ToList();
        lock (ReceivedMessages)
        {
            ReceivedMessages.Add(messageList);
        }
        Interlocked.Increment(ref _callCount);

        var lastUserMessage = messageList
            .LastOrDefault(m => m.Role == ChatRole.User)?.Text ?? "(empty)";

        var response = new ChatResponse(
            [new ChatMessage(ChatRole.Assistant, $"{ResponsePrefix}: {lastUserMessage}")])
        {
            ModelId = "test-model",
            Usage = new UsageDetails
            {
                InputTokenCount = lastUserMessage.Length,
                OutputTokenCount = lastUserMessage.Length + ResponsePrefix.Length + 2,
            },
        };

        return Task.FromResult(response);
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await GetResponseAsync(messages, options, cancellationToken);
        foreach (var update in response.ToChatResponseUpdates())
        {
            yield return update;
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }
}
