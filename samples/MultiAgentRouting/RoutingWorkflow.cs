// RoutingWorkflow — demonstrates parallel agent execution inside a Temporal workflow.
//
// The workflow fans out to all three specialist agents simultaneously and returns
// all of their responses. Each agent call runs as a durable Temporal activity,
// so any worker crash is transparently recovered.

using Microsoft.Extensions.AI;
using Temporalio.Workflows;
using static Temporalio.Extensions.Agents.TemporalWorkflowExtensions;

namespace MultiAgentRouting;

/// <summary>
/// Workflow that fans out the same user query to all three specialist agents in parallel
/// and returns each agent's response as an array (in the order: Weather, Billing, TechSupport).
/// </summary>
[Workflow("MultiAgentRouting.RoutingWorkflow")]
public class RoutingWorkflow
{
    /// <summary>
    /// Runs the fan-out: sends <paramref name="userQuery"/> to all three agents in parallel
    /// and returns their responses.
    /// </summary>
    [WorkflowRun]
    public async Task<string[]> RunAsync(string userQuery)
    {
        // Obtain a TemporalAIAgent handle for each specialist.
        // GetAgent() is a static helper from TemporalWorkflowExtensions.
        var weather = GetAgent("WeatherAgent");
        var billing = GetAgent("BillingAgent");
        var techSupport = GetAgent("TechSupportAgent");

        // Create an independent session for each agent so histories don't mix.
        var wSession = await weather.CreateSessionAsync();
        var bSession = await billing.CreateSessionAsync();
        var tSession = await techSupport.CreateSessionAsync();

        // Build the message list — the same question goes to all three agents.
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, userQuery)
        };

        // Fan-out: ExecuteAgentsInParallelAsync calls all three agents concurrently
        // using Workflow.WhenAllAsync (the workflow-safe equivalent of Task.WhenAll).
        var results = await ExecuteAgentsInParallelAsync(
        [
            (weather,     (IList<ChatMessage>)messages, wSession),
                (billing,     messages, bSession),
                (techSupport, messages, tSession)
        ]);

        return results.Select(r => r.Text ?? string.Empty).ToArray();
    }
}