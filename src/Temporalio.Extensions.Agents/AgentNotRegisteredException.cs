namespace Temporalio.Extensions.Agents;

/// <summary>
/// Exception thrown when an agent with the specified name has not been registered.
/// </summary>
public sealed class AgentNotRegisteredException : InvalidOperationException
{
    private AgentNotRegisteredException()
    {
        this.AgentName = string.Empty;
    }

    /// <summary>Initializes with the agent name.</summary>
    public AgentNotRegisteredException(string agentName) : base(GetMessage(agentName))
    {
        this.AgentName = agentName;
    }

    /// <summary>Initializes with the agent name and inner exception.</summary>
    public AgentNotRegisteredException(string agentName, Exception? innerException)
        : base(GetMessage(agentName), innerException)
    {
        this.AgentName = agentName;
    }

    /// <summary>Gets the name of the agent that was not registered.</summary>
    public string AgentName { get; }

    private static string GetMessage(string agentName)
    {
        ArgumentException.ThrowIfNullOrEmpty(agentName);
        return $"No agent named '{agentName}' was registered. Ensure the agent is registered using " +
               $"{nameof(ServiceCollectionExtensions.ConfigureTemporalAgents)} before using it.";
    }
}
