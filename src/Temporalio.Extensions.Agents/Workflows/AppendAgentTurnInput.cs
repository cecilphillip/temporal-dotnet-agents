using Microsoft.Agents.AI;

namespace Temporalio.Extensions.Agents.Workflows;

/// <summary>
/// Input for the <c>AppendAgentTurnAsync</c> activity. Carries the full
/// <see cref="AgentResponse"/> produced by <c>ExecuteDurableAgentTurnAsync</c> so the
/// activity can write the complete turn — including assistant tool-call messages,
/// tool-result messages, and the final assistant message — to the external history store.
/// </summary>
internal sealed record AppendAgentTurnInput
{
    /// <summary>Gets the name of the agent whose session history should be updated.</summary>
    public required string AgentName { get; init; }

    /// <summary>Gets the workflow ID of the session (used as the external-store session key).</summary>
    public required string SessionId { get; init; }

    /// <summary>Gets the run request that initiated this turn.</summary>
    public required RunRequest Request { get; init; }

    /// <summary>Gets the full agent response produced by the turn (all messages, all iterations).</summary>
    public required AgentResponse TurnResponse { get; init; }
}
