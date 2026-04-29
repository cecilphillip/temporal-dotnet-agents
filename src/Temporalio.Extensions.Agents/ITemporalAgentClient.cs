using Microsoft.Agents.AI;
using Temporalio.Client.Schedules;
using Temporalio.Extensions.Agents.Session;
using Temporalio.Extensions.Agents.Workflows;
using Temporalio.Extensions.AI;

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
    /// Convenience overload that resolves the agent by name and sends a single text message.
    /// A new session is created automatically using a random key.
    /// </summary>
    /// <param name="agentName">The registered agent name.</param>
    /// <param name="message">The user message text.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The agent's response.</returns>
    Task<AgentResponse> RunAgentAsync(
        string agentName,
        string message,
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
    /// Queries the agent workflow for a pending <see cref="DurableApprovalRequest"/>,
    /// returning <see langword="null"/> if none exists.
    /// </summary>
    Task<DurableApprovalRequest?> GetPendingApprovalAsync(
        TemporalAgentSessionId sessionId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Submits a human <see cref="DurableApprovalDecision"/> to the agent workflow.
    /// Unblocks the tool that issued the <see cref="DurableApprovalRequest"/> and returns
    /// the resolved decision.
    /// </summary>
    Task<DurableApprovalDecision> SubmitApprovalAsync(
        TemporalAgentSessionId sessionId,
        DurableApprovalDecision decision,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Cancels the pending approval request (if any) by submitting a rejected
    /// <see cref="DurableApprovalDecision"/> on behalf of an external system.
    /// No-op when no approval is pending.
    /// </summary>
    /// <param name="sessionId">The agent session ID.</param>
    /// <param name="reason">Optional reason recorded on the rejection. Defaults to <c>"Cancelled externally."</c>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    async Task CancelPendingApprovalAsync(
        TemporalAgentSessionId sessionId,
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        var pending = await GetPendingApprovalAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (pending is null)
        {
            return;
        }

        await SubmitApprovalAsync(
            sessionId,
            new DurableApprovalDecision
            {
                RequestId = pending.RequestId,
                Approved = false,
                Reason = reason ?? "Cancelled externally.",
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Starts a new agent session with a deferred start time.
    /// The agent workflow is created immediately but does not begin executing until
    /// <paramref name="delay"/> has elapsed.
    /// </summary>
    /// <param name="sessionId">The session ID that identifies the agent and session key.</param>
    /// <param name="request">The request to send once the delay has elapsed.</param>
    /// <param name="delay">How long to wait before the workflow begins executing.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <remarks>
    /// <b>Known limitation:</b> if a workflow with the same <paramref name="sessionId"/> is
    /// already running (due to <c>UseExisting</c> conflict policy), the delay is ignored and
    /// the existing workflow is reused immediately. This method only applies the delay when
    /// starting a brand-new session.
    /// </remarks>
    Task RunAgentDelayedAsync(
        TemporalAgentSessionId sessionId,
        RunRequest request,
        TimeSpan delay,
        CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Creates a Temporal Schedule that fires <see cref="AgentJobWorkflow"/> on the given spec.
    /// Each scheduled run is fire-and-forget — results are visible in the Temporal Web UI.
    /// </summary>
    /// <param name="agentName">Name of the agent to invoke on each schedule tick.</param>
    /// <param name="scheduleId">
    /// Unique identifier for this schedule. Also used to build the workflow ID for each run:
    /// <c>ta-{agentName}-scheduled-{scheduleId}</c>.
    /// </param>
    /// <param name="request">The request to send to the agent on each scheduled run.</param>
    /// <param name="spec">When and how often the schedule fires.</param>
    /// <param name="policy">Overlap and catchup policy. Defaults to <see cref="SchedulePolicy"/> defaults.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A <see cref="ScheduleHandle"/> for pausing, triggering, updating, or deleting the schedule.</returns>
    /// <remarks>
    /// <b>Schedule orphaning:</b> schedules are independent of workers. Removing an agent from
    /// <see cref="TemporalAgentsOptions"/> does <em>not</em> delete its schedule — it will keep
    /// firing. Use <see cref="GetAgentScheduleHandle"/> to retrieve the handle and call
    /// <c>DeleteAsync()</c> when decommissioning an agent.
    /// </remarks>
    Task<ScheduleHandle> ScheduleAgentAsync(
        string agentName,
        string scheduleId,
        RunRequest request,
        ScheduleSpec spec,
        SchedulePolicy? policy = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns a <see cref="ScheduleHandle"/> for an existing schedule by its ID.
    /// Use the handle to pause, trigger, update, or delete the schedule.
    /// </summary>
    /// <param name="scheduleId">The schedule ID passed to <see cref="ScheduleAgentAsync"/>.</param>
    ScheduleHandle GetAgentScheduleHandle(string scheduleId);
}
