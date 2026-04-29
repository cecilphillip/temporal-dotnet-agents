using Microsoft.Extensions.AI;

namespace Temporalio.Extensions.AI;

/// <summary>
/// Adapts a synchronous <see cref="Func{T,TResult}"/> delegate to the <see cref="IChatReducer"/>
/// interface. The delegate must be synchronous — <see cref="ReduceAsync"/> always completes
/// inline without I/O.
/// </summary>
internal sealed class FuncChatReducer(Func<IList<ChatMessage>, IList<ChatMessage>> func)
    : IChatReducer
{
    /// <inheritdoc/>
    public Task<IEnumerable<ChatMessage>> ReduceAsync(
        IEnumerable<ChatMessage> messages,
        CancellationToken cancellationToken)
        => Task.FromResult<IEnumerable<ChatMessage>>(func(messages.ToList()));
}
