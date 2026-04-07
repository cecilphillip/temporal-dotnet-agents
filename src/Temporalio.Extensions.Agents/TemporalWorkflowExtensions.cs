using System.ComponentModel;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Temporalio.Extensions.Agents.Session;
using Temporalio.Workflows;

namespace Temporalio.Extensions.Agents;

/// <summary>
/// Extension methods for use inside Temporal workflows.
/// Equivalent to <c>TaskOrchestrationContextExtensions</c>.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class TemporalWorkflowExtensions
{
    /// <summary>
    /// Gets a <see cref="TemporalAIAgent"/> for use in an orchestrating workflow.
    /// </summary>
    /// <param name="agentName">The registered agent name.</param>
    /// <param name="activityOptions">Optional activity options. Defaults to 30-minute StartToCloseTimeout.</param>
    public static TemporalAIAgent GetAgent(string agentName, ActivityOptions? activityOptions = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(agentName);
        return new TemporalAIAgent(agentName, activityOptions);
    }

    /// <summary>
    /// Generates a deterministic <see cref="TemporalAgentSessionId"/> using
    /// <see cref="Workflow.NewGuid()"/>.
    /// </summary>
    public static TemporalAgentSessionId NewAgentSessionId(string agentName)
    {
        ArgumentException.ThrowIfNullOrEmpty(agentName);
        return TemporalAgentSessionId.WithDeterministicKey(agentName, Workflow.NewGuid());
    }

    /// <summary>
    /// Dispatches multiple agent calls in parallel and returns all responses in input order.
    /// Uses <see cref="Workflow.WhenAllAsync{TResult}(IEnumerable{Task{TResult}})"/> internally,
    /// which is the workflow-safe equivalent of <c>Task.WhenAll</c>.
    /// </summary>
    /// <param name="requests">
    /// Sequence of <c>(Agent, Messages, Session)</c> tuples to run concurrently.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// An <see cref="IReadOnlyList{AgentResponse}"/> in the same order as <paramref name="requests"/>.
    /// </returns>
    /// <example>
    /// <code>
    /// var results = await ExecuteAgentsInParallelAsync(new[]
    /// {
    ///     (GetAgent("Researcher"),  researchMessages,  researchSession),
    ///     (GetAgent("Summarizer"),  summaryMessages,   summarySession),
    ///     (GetAgent("Critic"),      criticMessages,    criticSession),
    /// });
    /// </code>
    /// </example>
    public static async Task<IReadOnlyList<AgentResponse>> ExecuteAgentsInParallelAsync(
        IEnumerable<(TemporalAIAgent Agent, IList<ChatMessage> Messages, AgentSession Session)> requests,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requests);

        var requestList = requests as IList<(TemporalAIAgent Agent, IList<ChatMessage> Messages, AgentSession Session)>
            ?? requests.ToList();
        var tasks = new List<Task<AgentResponse>>(requestList.Count);
        foreach (var r in requestList)
            tasks.Add(r.Agent.RunAsync(r.Messages, r.Session, null, cancellationToken));

        return await Workflow.WhenAllAsync(tasks);
    }
}
