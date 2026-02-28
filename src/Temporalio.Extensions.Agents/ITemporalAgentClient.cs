using Microsoft.Agents.AI;

namespace Temporalio.Extensions.Agents;

/// <summary>
/// Client for running agents via Temporal workflow updates.
/// </summary>
public interface ITemporalAgentClient
{
    /// <summary>
    /// Runs an agent by sending a Temporal workflow update and waiting for the response.
    /// Starts the workflow if it is not already running.
    /// </summary>
    Task<AgentResponse> RunAgentAsync(
        TemporalAgentSessionId sessionId,
        RunRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs an agent by sending a fire-and-forget signal.
    /// Starts the workflow if it is not already running.
    /// Returns immediately without waiting for the agent response.
    /// </summary>
    Task RunAgentFireAndForgetAsync(
        TemporalAgentSessionId sessionId,
        RunRequest request,
        CancellationToken cancellationToken = default);

    // ── Routing (GAP 2) ──────────────────────────────────────────────────────

    /// <summary>
    /// Uses the registered <see cref="IAgentRouter"/> to classify the request messages,
    /// picks the best-matching registered agent, and runs it — all in one call.
    /// </summary>
    /// <param name="sessionKey">
    /// The session key used to build the routed session ID.
    /// A <see cref="TemporalAgentSessionId"/> is constructed from the chosen agent name
    /// and this key.
    /// </param>
    /// <param name="request">The request to dispatch to the chosen agent.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The response from the chosen agent.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when no <see cref="IAgentRouter"/> is registered or no descriptors exist.
    /// </exception>
    Task<AgentResponse> RouteAsync(
        string sessionKey,
        RunRequest request,
        CancellationToken cancellationToken = default);

    // ── Human-in-the-Loop (GAP 3) ────────────────────────────────────────────

    /// <summary>
    /// Queries the agent workflow for a pending <see cref="ApprovalRequest"/>,
    /// returning <see langword="null"/> if none exists.
    /// </summary>
    Task<ApprovalRequest?> GetPendingApprovalAsync(
        TemporalAgentSessionId sessionId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Submits a human <see cref="ApprovalDecision"/> to the agent workflow.
    /// Unblocks the tool that issued the <see cref="ApprovalRequest"/> and returns
    /// the resolved <see cref="ApprovalTicket"/>.
    /// </summary>
    Task<ApprovalTicket> SubmitApprovalAsync(
        TemporalAgentSessionId sessionId,
        ApprovalDecision decision,
        CancellationToken cancellationToken = default);
}
