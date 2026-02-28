using Microsoft.Agents.AI;

namespace Temporalio.Extensions.Agents;

/// <summary>
/// Handler for processing responses from a Temporal agent. Used to stream responses to the user.
/// </summary>
public interface IAgentResponseHandler
{
    /// <summary>Handles a streaming response update from the agent.</summary>
    ValueTask OnStreamingResponseUpdateAsync(
        IAsyncEnumerable<AgentResponseUpdate> messageStream,
        CancellationToken cancellationToken);

    /// <summary>Handles a discrete (non-streaming) response from the agent.</summary>
    ValueTask OnAgentResponseAsync(
        AgentResponse message,
        CancellationToken cancellationToken);
}
