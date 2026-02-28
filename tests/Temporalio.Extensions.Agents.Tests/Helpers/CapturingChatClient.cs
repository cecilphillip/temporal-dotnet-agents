using Microsoft.Extensions.AI;

namespace Temporalio.Extensions.Agents.Tests.Helpers;

/// <summary>
/// A test-only <see cref="IChatClient"/> that records the <see cref="ChatOptions"/>
/// passed to each <see cref="GetResponseAsync"/> call, allowing assertions about middleware behavior.
/// </summary>
internal sealed class CapturingChatClient : IChatClient
{
    private readonly List<ChatOptions?> _capturedOptions = [];

    /// <summary>Gets the options captured from each <see cref="GetResponseAsync"/> call.</summary>
    public IReadOnlyList<ChatOptions?> CapturedOptions => _capturedOptions;

    /// <summary>Gets the options from the most recent call, or <see langword="null"/> if none.</summary>
    public ChatOptions? LastOptions => _capturedOptions.Count > 0 ? _capturedOptions[^1] : null;

    public ChatClientMetadata Metadata { get; } = new("test-provider");

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        _capturedOptions.Add(options);
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "stub response")));
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }
}
