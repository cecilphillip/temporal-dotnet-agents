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

    /// <summary>
    /// Convenience over <see cref="TemporalAgentRunOptions"/> for callers who want to
    /// fire-and-forget a single message without explicitly constructing the options object.
    /// Equivalent to calling
    /// <c>agent.RunAsync(message, session, new TemporalAgentRunOptions { IsFireAndForget = true })</c>.
    /// </summary>
    /// <remarks>
    /// Only meaningful for agents that proxy to a Temporal-hosted session
    /// (e.g., <see cref="TemporalAIAgentProxy"/>). Behavior on plain in-process agents that do
    /// not honor <see cref="TemporalAgentRunOptions.IsFireAndForget"/> is the same as a normal
    /// <c>RunAsync</c> call.
    /// </remarks>
    /// <param name="agent">The agent to run.</param>
    /// <param name="message">The user message text.</param>
    /// <param name="session">Optional existing <see cref="AgentSession"/>. When <see langword="null"/>, a new session is created.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static Task<AgentResponse> RunFireAndForgetAsync(
        this AIAgent agent,
        string message,
        AgentSession? session = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentException.ThrowIfNullOrEmpty(message);

        var options = new TemporalAgentRunOptions { IsFireAndForget = true };
        return agent.RunAsync(message, session, options, cancellationToken);
    }
}
