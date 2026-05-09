// RoutingWorkflow — workflow-based routing:
//   A RoutingActivities.ClassifyRequest activity picks the right specialist from
//   a keyword analysis of the user's question. The decision is recorded in event
//   history and is therefore durable, retryable, and visible in the Temporal Web UI.
//
// ParallelAgentWorkflow — parallel fan-out:
//   Sends the same query to all three specialists simultaneously via
//   ExecuteAgentsInParallelAsync.

using Microsoft.Extensions.AI;
using Temporalio.Workflows;
using static Temporalio.Extensions.Agents.TemporalWorkflowExtensions;

namespace MultiAgentRouting;

/// <summary>
/// Routes a user question to the appropriate specialist agent.
/// The routing decision runs inside a <see cref="RoutingActivities.ClassifyRequest"/>
/// activity so it is recorded in event history — durable, retryable, and auditable.
/// </summary>
[Workflow("MultiAgentRouting.RoutingWorkflow")]
public class RoutingWorkflow
{
    private static readonly ActivityOptions ClassifyActivityOptions =
        new() { StartToCloseTimeout = TimeSpan.FromSeconds(30) };

    /// <summary>
    /// Receives a user question, classifies intent via an activity, and routes to the
    /// appropriate specialist agent.
    /// </summary>
    [WorkflowRun]
    public async Task<string> RunAsync(string userQuestion)
    {
        // ── Step 1: Classify the intent via activity ─────────────────────────
        // Running classification in an activity means:
        //   • The result is cached in workflow history — a crash after this point
        //     won't re-invoke the classifier.
        //   • The routing decision is visible in the Temporal Web UI event log.
        //   • Retries apply if the classification activity fails transiently.
        var agentName = await Workflow.ExecuteActivityAsync(
            (RoutingActivities a) => a.ClassifyRequest(userQuestion),
            ClassifyActivityOptions).ConfigureAwait(true);

        // ── Step 2: Dispatch to the chosen specialist ────────────────────────
        var specialist = GetAgent(agentName);
        var session = await specialist.CreateSessionAsync().ConfigureAwait(true);
        var response = await specialist.RunAsync(
            [new ChatMessage(ChatRole.User, userQuestion)],
            session).ConfigureAwait(true);

        return response.Text ?? string.Empty;
    }
}

/// <summary>
/// Fans out the same query to all three specialist agents in parallel and returns
/// all of their responses. Uses <c>ExecuteAgentsInParallelAsync</c>, the
/// workflow-safe equivalent of <c>Task.WhenAll</c>.
/// </summary>
[Workflow("MultiAgentRouting.ParallelAgentWorkflow")]
public class ParallelAgentWorkflow
{
    /// <summary>
    /// Sends <paramref name="userQuery"/> to all three specialist agents in parallel
    /// and returns their responses.
    /// </summary>
    [WorkflowRun]
    public async Task<string[]> RunAsync(string userQuery)
    {
        var weather     = GetAgent("WeatherAgent");
        var billing     = GetAgent("BillingAgent");
        var techSupport = GetAgent("TechSupportAgent");

        var wSession = await weather.CreateSessionAsync().ConfigureAwait(true);
        var bSession = await billing.CreateSessionAsync().ConfigureAwait(true);
        var tSession = await techSupport.CreateSessionAsync().ConfigureAwait(true);

        IList<ChatMessage> messages = [new ChatMessage(ChatRole.User, userQuery)];

        var results = await ExecuteAgentsInParallelAsync(
        [
            (weather,     messages, wSession),
            (billing,     messages, bSession),
            (techSupport, messages, tSession)
        ]).ConfigureAwait(true);

        return results.Select(r => r.Text ?? string.Empty).ToArray();
    }
}
