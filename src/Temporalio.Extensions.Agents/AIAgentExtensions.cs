using Microsoft.Agents.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Temporalio.Extensions.Agents;

/// <summary>
/// Extension methods for <see cref="AIAgent"/>.
/// </summary>
public static class AIAgentExtensions
{
    /// <summary>
    /// Converts an <see cref="AIAgent"/> to a Temporal agent proxy.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// Thrown when the agent is already a <see cref="TemporalAIAgent"/> or has no name.
    /// </exception>
    /// <exception cref="AgentNotRegisteredException">
    /// Thrown when the agent's name is not registered in <paramref name="services"/>.
    /// </exception>
    public static AIAgent AsTemporalAgentProxy(this AIAgent agent, IServiceProvider services)
    {
        if (agent is TemporalAIAgent)
        {
            throw new ArgumentException(
                $"{nameof(TemporalAIAgent)} instances cannot be converted to a Temporal agent proxy.",
                nameof(agent));
        }

        string agentName = agent.Name
            ?? throw new ArgumentException("Agent must have a name.", nameof(agent));

        ServiceCollectionExtensions.ValidateAgentIsRegistered(services, agentName);

        ITemporalAgentClient agentClient = services.GetRequiredService<ITemporalAgentClient>();
        return new TemporalAIAgentProxy(agentName, agentClient);
    }
}
