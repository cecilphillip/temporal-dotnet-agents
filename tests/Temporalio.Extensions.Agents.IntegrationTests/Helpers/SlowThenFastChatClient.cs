using Microsoft.Extensions.AI;

namespace Temporalio.Extensions.Agents.IntegrationTests.Helpers;

internal sealed class SlowThenFastChatClient : IChatClient
{
    private readonly TimeSpan _slowDelay;
    private readonly int _maxSlowCalls;
    private int _callCount;

    public SlowThenFastChatClient(TimeSpan slowDelay, int maxSlowCalls = 1)
    {
        _slowDelay = slowDelay;
        _maxSlowCalls = maxSlowCalls;
    }

    public int CallCount => _callCount;

    public ChatClientMetadata Metadata { get; } = new("slow-then-fast");

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        int call = Interlocked.Increment(ref _callCount);
        if (call <= _maxSlowCalls)
        {
            await Task.Delay(_slowDelay, cancellationToken).ConfigureAwait(false);
        }

        var msgList = messages.ToList();
        var userMessages = msgList.Where(m => m.Role == ChatRole.User).ToList();
        var lastMessage = userMessages.LastOrDefault()?.Text ?? "(empty)";
        return new ChatResponse(new ChatMessage(ChatRole.Assistant, $"Echo [{userMessages.Count}]: {lastMessage}"));
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
