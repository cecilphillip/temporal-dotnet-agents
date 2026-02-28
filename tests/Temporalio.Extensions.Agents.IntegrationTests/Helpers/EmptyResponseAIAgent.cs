using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Temporalio.Extensions.Agents.IntegrationTests.Helpers;

/// <summary>
/// A test agent that returns an empty <see cref="AgentResponse"/> (no messages).
/// Used to verify that the Temporal workflow and state serialization handle
/// empty response payloads gracefully without crashing.
/// </summary>
internal sealed class EmptyResponseAIAgent : TestAgentBase
{
    public EmptyResponseAIAgent(string name) : base(name)
    {
    }

    protected override Task<AgentResponse> RunCoreAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session = null,
        AgentRunOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        // Return a valid AgentResponse with zero messages.
        var response = new AgentResponse
        {
            Messages = [],
            CreatedAt = DateTimeOffset.UtcNow
        };

        return Task.FromResult(response);
    }
}
