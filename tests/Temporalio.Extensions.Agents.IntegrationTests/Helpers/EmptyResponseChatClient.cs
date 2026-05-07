using Microsoft.Extensions.AI;

namespace Temporalio.Extensions.Agents.IntegrationTests.Helpers;

internal sealed class EmptyResponseChatClient : IChatClient
{
    public ChatClientMetadata Metadata { get; } = new("empty");

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, string.Empty));
        return Task.FromResult(response);
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        return Empty();

        async IAsyncEnumerable<ChatResponseUpdate> Empty()
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }
}
