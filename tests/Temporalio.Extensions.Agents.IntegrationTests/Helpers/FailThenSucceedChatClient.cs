using Microsoft.Extensions.AI;

namespace Temporalio.Extensions.Agents.IntegrationTests.Helpers;

internal sealed class FailThenSucceedChatClient : IChatClient
{
    private readonly int _failCount;
    private int _callCount;

    public FailThenSucceedChatClient(int failCount = 1)
    {
        _failCount = failCount;
    }

    public int CallCount => _callCount;

    public ChatClientMetadata Metadata { get; } = new("fail-then-succeed");

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        int call = Interlocked.Increment(ref _callCount);
        if (call <= _failCount)
        {
            throw new InvalidOperationException(
                $"Simulated chat-client failure (attempt {call} of {_failCount} planned failures)");
        }

        var msgList = messages.ToList();
        var userMessages = msgList.Where(m => m.Role == ChatRole.User).ToList();
        var lastMessage = userMessages.LastOrDefault()?.Text ?? "(empty)";
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, $"Echo [{userMessages.Count}]: {lastMessage}")));
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
        foreach (var msg in response.Messages)
        {
            yield return new ChatResponseUpdate(msg.Role, msg.Text);
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }
}
