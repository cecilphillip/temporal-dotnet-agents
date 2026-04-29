using Temporalio.Activities;
using Temporalio.Extensions.Agents;

namespace WorkflowRouting;

/// <summary>
/// Activities that safely query the agent registry at runtime.
/// Activities are NOT replayed — their results are cached in workflow history.
/// This makes registry lookups safe even if the agent set changes between deployments.
/// </summary>
public class RoutingActivities(TemporalAgentsOptions options)
{
    /// <summary>
    /// Returns information about all registered agents.
    /// The workflow uses these to build a context-aware routing prompt for the Classifier.
    /// </summary>
    /// <remarks>
    /// This is safe inside an activity because the result is recorded in Temporal's event
    /// history. On replay, the cached list is returned — the registry is never re-queried.
    /// Even if agents are added or removed between the original execution and a replay,
    /// the workflow sees the same agent list it saw originally.
    /// </remarks>
    [Activity("WorkflowRouting.GetAvailableAgents")]
    public AgentInfo[] GetAvailableAgents()
    {
        // Map registered agent names to their descriptions.
        // Descriptions are declared here alongside the names — they are not stored in
        // TemporalAgentsOptions because routing metadata is a concern of the routing
        // activity, not the core agent registry.
        var descriptions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["OrdersAgent"]     = "Handles order tracking, returns, shipping status, and purchase questions.",
            ["TechSupportAgent"] = "Handles technical issues, app crashes, error messages, and troubleshooting.",
            ["GeneralAgent"]    = "Handles greetings, general inquiries, and anything else.",
        };

        return options.GetRegisteredAgentNames()
            .Select(name => new AgentInfo(
                name,
                descriptions.GetValueOrDefault(name, $"Agent: {name}")))
            .ToArray();
    }

    /// <summary>
    /// Verifies that a specific agent is currently registered. Used as a safety check
    /// before dispatching to an agent whose name came from LLM output.
    /// </summary>
    [Activity("WorkflowRouting.ValidateAgent")]
    public string ValidateAgent(string agentName, string fallback)
    {
        return options.IsAgentRegistered(agentName) ? agentName : fallback;
    }
}

/// <summary>
/// Serializable record for passing agent metadata through Temporal activity results.
/// </summary>
public record AgentInfo(string Name, string Description);
