using Temporalio.Workflows;

namespace Temporalio.Extensions.Agents.Workflows;

/// <summary>
/// A simple, fire-and-forget Temporal workflow for scheduled or deferred agent runs.
/// Unlike <see cref="AgentWorkflow"/>, this workflow carries no conversation history,
/// no StateBag, no TTL loop, and no <c>[WorkflowUpdate]</c> handlers — it executes
/// a single agent activity and exits.
/// </summary>
/// <remarks>
/// Workflow ID convention: <c>ta-{agentName}-scheduled-{scheduleId}</c>.
/// This is disjoint from interactive session IDs (<c>ta-{agentName}-{sessionKey}</c>),
/// so scheduled runs never collide with live sessions.
/// </remarks>
[Workflow("Temporalio.Extensions.Agents.AgentJobWorkflow")]
internal sealed class AgentJobWorkflow
{
    [WorkflowRun]
    public async Task RunAsync(AgentJobInput input)
    {
        // No history, no StateBag — each scheduled run starts completely fresh.
        // The OTel agent.turn span fires automatically because it lives in
        // AgentActivities.ExecuteAgentAsync — the same code path as session runs.
        //
        // Note: ExecuteAgentInput is constructed outside the expression tree to avoid
        // CS9175 (collection expressions are not allowed inside expression trees).
        var activityInput = new ExecuteAgentInput(
            input.AgentName,
            input.Request,
            []);     // empty conversation history — no prior context for jobs

        await Temporalio.Workflows.Workflow.ExecuteActivityAsync(
            (AgentActivities a) => a.ExecuteAgentAsync(activityInput),
            new ActivityOptions
            {
                StartToCloseTimeout = input.ActivityStartToCloseTimeout ?? TimeSpan.FromMinutes(30),
                HeartbeatTimeout = input.ActivityHeartbeatTimeout ?? TimeSpan.FromMinutes(5),
                Summary = AgentActivities.BuildActivitySummary(input.AgentName),
            });
    }
}
