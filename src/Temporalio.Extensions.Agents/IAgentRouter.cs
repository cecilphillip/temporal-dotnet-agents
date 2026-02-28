using Microsoft.Extensions.AI;

namespace Temporalio.Extensions.Agents;

/// <summary>
/// Decides which registered agent should handle an incoming message.
/// Implement this interface to provide a custom routing strategy, or use
/// <see cref="LlmAgentRouter"/> for LLM-powered classification.
/// </summary>
public interface IAgentRouter
{
    /// <summary>
    /// Inspects the conversation <paramref name="messages"/> and returns the name of the
    /// best matching agent from <paramref name="agents"/>.
    /// </summary>
    /// <param name="messages">The conversation messages to route.</param>
    /// <param name="agents">The candidates the router may choose from.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The name of the chosen agent (must be present in <paramref name="agents"/>).</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the router cannot determine a valid agent name.
    /// </exception>
    Task<string> RouteAsync(
        IList<ChatMessage> messages,
        IEnumerable<AgentDescriptor> agents,
        CancellationToken cancellationToken = default);
}
