using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Temporalio.Workflows;
using static Temporalio.Extensions.Agents.TemporalWorkflowExtensions;

namespace WorkflowRouting;

/// <summary>
/// Demonstrates truly dynamic routing where the workflow discovers available agents
/// at runtime via descriptors — no hardcoded agent names in the routing logic.
/// </summary>
/// <remarks>
/// <para>
/// Unlike <see cref="CustomerServiceWorkflow"/> (which uses a hardcoded switch),
/// this workflow queries the agent registry via an activity to discover what agents
/// exist and what they do, then passes that information to the Classifier so it can
/// pick the best match from whatever agents are currently registered.
/// </para>
/// <para>
/// This pattern is useful for: gradual rollouts, feature flags, A/B testing, or any
/// scenario where the set of agents changes across deployments without recompiling
/// the workflow.
/// </para>
/// <para>
/// Why an activity? Workflow code must be deterministic during replay, but the agent
/// registry is live process state that can change between deployments. Querying it in
/// an activity records the result in event history — replays use the cached snapshot.
/// </para>
/// </remarks>
[Workflow("WorkflowRouting.DynamicRoutingWorkflow")]
public class DynamicRoutingWorkflow
{
    private static readonly ActivityOptions RegistryActivityOptions =
        new() { StartToCloseTimeout = TimeSpan.FromSeconds(10) };

    private const string FallbackAgent = "GeneralAgent";

    [WorkflowRun]
    public async Task<string> RunAsync(string userQuestion)
    {
        // ── Step 1: Discover available agents via activity (cached on replay) ──
        // The activity reads AddAgentDescriptor() registrations from the registry.
        // This is the only place the workflow learns what specialists exist.
        var availableAgents = await Workflow.ExecuteActivityAsync(
            (RoutingActivities a) => a.GetAvailableAgents(),
            RegistryActivityOptions).ConfigureAwait(true);

        if (availableAgents.Length == 0)
        {
            Workflow.Logger.LogWarning("No agent descriptors registered — falling back to {Agent}", FallbackAgent);
            return await CallAgent(FallbackAgent, userQuestion).ConfigureAwait(true);
        }

        // ── Step 2: Build a routing prompt from the discovered descriptors ─────
        // The Classifier receives the agent list as context so it can pick
        // from whatever is registered — no hardcoded names in this workflow.
        var agentList = string.Join("\n", availableAgents.Select(a => $"  {a.Name} — {a.Description}"));
        var routingPrompt =
            $"Given the user question below, respond with ONLY the name of the best-matching agent.\n\n" +
            $"Available agents:\n{agentList}\n\n" +
            $"User question: {userQuestion}\n\n" +
            $"Respond with the agent name only. No explanation, no punctuation.";

        var classifier = GetAgent("Classifier");
        var classifierSession = await classifier.CreateSessionAsync().ConfigureAwait(true);
        var classifierResponse = await classifier.RunAsync(
            [new ChatMessage(ChatRole.User, routingPrompt)], classifierSession).ConfigureAwait(true);

        var chosenAgent = (classifierResponse.Text ?? string.Empty).Trim();

        // ── Step 3: Validate the LLM's choice via activity (cached on replay) ──
        // LLMs can hallucinate names. The activity confirms the agent exists,
        // falling back to GeneralAgent if not.
        var agentName = await Workflow.ExecuteActivityAsync(
            (RoutingActivities a) => a.ValidateAgent(chosenAgent, FallbackAgent),
            RegistryActivityOptions).ConfigureAwait(true);

        Workflow.Logger.LogInformation(
            "Dynamic routing: LLM chose '{ChosenAgent}', validated as '{Agent}'",
            chosenAgent, agentName);

        // ── Step 4: Call the resolved specialist ────────────────────────────────
        return await CallAgent(agentName, userQuestion).ConfigureAwait(true);
    }

    private async Task<string> CallAgent(string agentName, string userQuestion)
    {
        var specialist = GetAgent(agentName);
        var specialistSession = await specialist.CreateSessionAsync().ConfigureAwait(true);
        var response = await specialist.RunAsync(
            [new ChatMessage(ChatRole.User, userQuestion)], specialistSession).ConfigureAwait(true);
        return response.Text ?? string.Empty;
    }
}
