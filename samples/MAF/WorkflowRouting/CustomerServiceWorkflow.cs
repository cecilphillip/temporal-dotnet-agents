using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Temporalio.Workflows;
using static Temporalio.Extensions.Agents.TemporalWorkflowExtensions;

namespace WorkflowRouting;

/// <summary>
/// A workflow that acts as a router — no <c>IAgentRouter</c> abstraction needed.
/// A lightweight Classifier agent determines the user's intent, then the workflow
/// dispatches to the correct specialist agent using plain <c>if/else</c> logic.
/// </summary>
/// <remarks>
/// Every agent call runs as a durable Temporal activity, so:
/// <list type="bullet">
///   <item>The classifier result is recorded — a crash after classification won't re-invoke the LLM.</item>
///   <item>The specialist call is independently retried on transient failure.</item>
///   <item>The full routing decision is visible in the workflow event history.</item>
/// </list>
/// </remarks>
[Workflow("WorkflowRouting.CustomerServiceWorkflow")]
public class CustomerServiceWorkflow
{
    /// <summary>
    /// Receives a user question, classifies intent, and routes to the appropriate specialist.
    /// </summary>
    /// <param name="userQuestion">The customer's question.</param>
    /// <returns>The specialist agent's response text.</returns>
    [WorkflowRun]
    public async Task<string> RunAsync(string userQuestion)
    {
        // ── Step 1: Classify the user's intent ──────────────────────────────
        var classifier = GetAgent("Classifier");
        var classifierSession = await classifier.CreateSessionAsync().ConfigureAwait(true);

        var classifierResponse = await classifier.RunAsync(
            [new ChatMessage(ChatRole.User, userQuestion)],
            classifierSession).ConfigureAwait(true);

        var classification = (classifierResponse.Text ?? string.Empty).Trim().ToUpperInvariant();

        // ── Step 2: Route to the correct specialist ─────────────────────────
        var specialistName = classification switch
        {
            "ORDERS" => "OrdersAgent",
            "TECH_SUPPORT" => "TechSupportAgent",
            _ => "GeneralAgent",
        };

        Workflow.Logger.LogInformation(
            "Classified as '{Classification}' → routing to {Agent}",
            classification,
            specialistName);

        // ── Step 3: Call the specialist agent ────────────────────────────────
        var specialist = GetAgent(specialistName);
        var specialistSession = await specialist.CreateSessionAsync().ConfigureAwait(true);

        var specialistResponse = await specialist.RunAsync(
            [new ChatMessage(ChatRole.User, userQuestion)],
            specialistSession).ConfigureAwait(true);

        return specialistResponse.Text ?? string.Empty;
    }
}
