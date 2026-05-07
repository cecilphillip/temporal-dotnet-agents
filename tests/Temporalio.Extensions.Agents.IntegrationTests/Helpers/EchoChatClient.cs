using Microsoft.Extensions.AI;

namespace Temporalio.Extensions.Agents.IntegrationTests.Helpers;

/// <summary>
/// A minimal <see cref="IChatClient"/> for integration testing the v0.3 durable-agent path.
/// Returns "Echo [{turnCount}]: {lastUserMessage}" without calling any real LLM.
/// </summary>
internal sealed class EchoChatClient : IChatClient
{
    public ChatClientMetadata Metadata { get; } = new("echo");

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var messageList = messages.ToList();
        var userMessages = messageList.Where(m => m.Role == ChatRole.User).ToList();
        int turnCount = userMessages.Count;
        string lastMessage = userMessages.LastOrDefault()?.Text ?? "(empty)";
        var responseText = $"Echo [{turnCount}]: {lastMessage}";

        var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, responseText));
        return Task.FromResult(response);
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
