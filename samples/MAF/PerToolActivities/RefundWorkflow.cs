// RefundWorkflow.cs — single-turn driver workflow.
//
// In step mode, the workflow only needs to send the user's complaint to the agent and
// return its final reply. The agent runs as a series of RunAgentStep + InvokeFunction
// activities under the hood; the workflow doesn't need to manage the tool loop directly.

using Microsoft.Extensions.AI;
using Temporalio.Workflows;
using static Temporalio.Extensions.Agents.TemporalWorkflowExtensions;

namespace PerToolActivities;

/// <summary>
/// Single-turn workflow that hands a customer complaint to <c>RefundAgent</c> and
/// returns the agent's final response.
/// <para>
/// Because <c>EnablePerToolActivities = true</c> on the worker, every LLM call this
/// agent makes is a separate <c>RunAgentStep</c> activity, and every tool call the
/// model issues is a separate <c>InvokeFunction</c> activity dispatched in parallel
/// from the step-mode loop inside <c>AgentWorkflow</c>. This workflow itself is
/// blissfully ignorant of that machinery — one <c>RunAsync</c> call covers the
/// whole multi-step turn.
/// </para>
/// </summary>
[Workflow("PerToolActivities.RefundWorkflow")]
public class RefundWorkflow
{
    [WorkflowRun]
    public async Task<string> RunAsync(string customerComplaint)
    {
        var agent = GetAgent("RefundAgent");
        var session = await agent.CreateSessionAsync().ConfigureAwait(true);

        var response = await agent.RunAsync(
            [new ChatMessage(ChatRole.User, customerComplaint)],
            session).ConfigureAwait(true);

        return response.Text ?? "(no response)";
    }
}
