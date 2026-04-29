using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Temporalio.Extensions.Agents.Session;
using Temporalio.Workflows;

namespace Temporalio.Extensions.Agents;

/// <summary>
/// Extension methods for use inside Temporal workflows.
/// Equivalent to <c>TaskOrchestrationContextExtensions</c>.
/// </summary>
/// <remarks>
/// All members on this type require Temporal workflow context (i.e. <see cref="Workflow.InWorkflow"/>
/// must be <see langword="true"/>). Calling these from external code throws
/// <see cref="InvalidOperationException"/>; resolve <see cref="TemporalAIAgentProxy"/> from the
/// service provider via <c>GetTemporalAgentProxy(name)</c> for external invocation.
/// </remarks>
public static class TemporalWorkflowExtensions
{
    /// <summary>
    /// Gets a <see cref="TemporalAIAgent"/> for use in an orchestrating workflow.
    /// </summary>
    /// <param name="agentName">The registered agent name.</param>
    /// <param name="activityOptions">Optional activity options. Defaults to 30-minute StartToCloseTimeout.</param>
    /// <remarks>
    /// Must be called from within a Temporal workflow. For external (non-workflow) invocation,
    /// resolve a <see cref="TemporalAIAgentProxy"/> via <c>GetTemporalAgentProxy(name)</c>.
    /// </remarks>
    /// <exception cref="InvalidOperationException">Thrown when called outside a Temporal workflow.</exception>
    public static TemporalAIAgent GetAgent(string agentName, ActivityOptions? activityOptions = null)
    {
        if (!Workflow.InWorkflow)
        {
            throw new InvalidOperationException(
                $"{nameof(GetAgent)} can only be called from within a Temporal workflow. " +
                "If you need to invoke an agent from external code, resolve a TemporalAIAgentProxy " +
                "from your service provider via GetTemporalAgentProxy(name) instead.");
        }

        ArgumentException.ThrowIfNullOrEmpty(agentName);
        return new TemporalAIAgent(agentName, activityOptions);
    }

    /// <summary>
    /// Generates a deterministic <see cref="TemporalAgentSessionId"/> using
    /// <see cref="Workflow.NewGuid()"/>.
    /// </summary>
    /// <remarks>
    /// Must be called from within a Temporal workflow — the determinism contract relies on
    /// <see cref="Workflow.NewGuid()"/>, which is only valid inside workflow context.
    /// </remarks>
    /// <exception cref="InvalidOperationException">Thrown when called outside a Temporal workflow.</exception>
    public static TemporalAgentSessionId NewAgentSessionId(string agentName)
    {
        if (!Workflow.InWorkflow)
        {
            throw new InvalidOperationException(
                $"{nameof(NewAgentSessionId)} can only be called from within a Temporal workflow. " +
                "Use TemporalAgentSessionId.WithRandomKey(agentName) in external (non-workflow) code.");
        }

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
    /// <remarks>
    /// Must be called from within a Temporal workflow — internally uses
    /// <see cref="Workflow.WhenAllAsync{TResult}(IEnumerable{Task{TResult}})"/>.
    /// </remarks>
    /// <exception cref="InvalidOperationException">Thrown when called outside a Temporal workflow.</exception>
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
        if (!Workflow.InWorkflow)
        {
            throw new InvalidOperationException(
                $"{nameof(ExecuteAgentsInParallelAsync)} can only be called from within a Temporal workflow. " +
                "It uses Workflow.WhenAllAsync, which is only valid in workflow context.");
        }

        ArgumentNullException.ThrowIfNull(requests);

        var requestList = requests as IList<(TemporalAIAgent Agent, IList<ChatMessage> Messages, AgentSession Session)>
            ?? requests.ToList();
        var tasks = new List<Task<AgentResponse>>(requestList.Count);
        foreach (var r in requestList)
            tasks.Add(r.Agent.RunAsync(r.Messages, r.Session, null, cancellationToken));

        return await Workflow.WhenAllAsync(tasks);
    }
}
