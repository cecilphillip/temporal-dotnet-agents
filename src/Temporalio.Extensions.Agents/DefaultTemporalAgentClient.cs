using System.Diagnostics;
using Microsoft.Agents.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Temporalio.Api.Enums.V1;
using Temporalio.Client;

namespace Temporalio.Extensions.Agents;

/// <summary>
/// Default implementation of <see cref="ITemporalAgentClient"/> that communicates with
/// <see cref="AgentWorkflow"/> via Temporal workflow updates (no polling).
/// </summary>
internal class DefaultTemporalAgentClient(
    ITemporalClient client,
    TemporalAgentsOptions options,
    string taskQueue,
    ILogger<DefaultTemporalAgentClient>? logger = null,
    IAgentRouter? router = null) : ITemporalAgentClient
{
    private readonly ILogger<DefaultTemporalAgentClient> _logger =
        logger ?? NullLogger<DefaultTemporalAgentClient>.Instance;

    /// <inheritdoc/>
    public async Task<AgentResponse> RunAgentAsync(
        TemporalAgentSessionId sessionId,
        RunRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // GAP 4: emit a client-side span wrapping the update round-trip.
        using var span = TemporalAgentTelemetry.ActivitySource.StartActivity(
            TemporalAgentTelemetry.AgentClientSendSpanName,
            ActivityKind.Client);

        span?.SetTag(TemporalAgentTelemetry.AgentNameAttribute, sessionId.AgentName);
        span?.SetTag(TemporalAgentTelemetry.AgentSessionIdAttribute, sessionId.WorkflowId);

        var workflowOptions = new WorkflowOptions(sessionId.WorkflowId, taskQueue)
        {
            IdConflictPolicy = WorkflowIdConflictPolicy.UseExisting,
            IdReusePolicy = WorkflowIdReusePolicy.AllowDuplicate
        };

        _logger.LogClientSendingUpdate(sessionId.AgentName, sessionId.WorkflowId);

        await client.StartWorkflowAsync(
            (AgentWorkflow wf) => wf.RunAsync(new AgentWorkflowInput
            {
                AgentName = sessionId.AgentName,
                TaskQueue = taskQueue,
                TimeToLive = options.GetTimeToLive(sessionId.AgentName),
                ActivityStartToCloseTimeout = options.ActivityStartToCloseTimeout,
                ActivityHeartbeatTimeout = options.ActivityHeartbeatTimeout
            }),
            workflowOptions);

        // Use a handle WITHOUT a pinned RunId so updates follow the continue-as-new chain.
        var handle = client.GetWorkflowHandle<AgentWorkflow>(sessionId.WorkflowId);

        var response = await handle.ExecuteUpdateAsync<AgentWorkflow, AgentResponse>(
            wf => wf.RunAgentAsync(request));

        _logger.LogClientUpdateCompleted(sessionId.AgentName, sessionId.WorkflowId);
        return response;
    }

    /// <inheritdoc/>
    public async Task RunAgentFireAndForgetAsync(
        TemporalAgentSessionId sessionId,
        RunRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var workflowOptions = new WorkflowOptions(sessionId.WorkflowId, taskQueue)
        {
            IdConflictPolicy = WorkflowIdConflictPolicy.UseExisting,
            IdReusePolicy = WorkflowIdReusePolicy.AllowDuplicate
        };

        _logger.LogClientFireAndForget(sessionId.AgentName, sessionId.WorkflowId);

        await client.StartWorkflowAsync(
            (AgentWorkflow wf) => wf.RunAsync(new AgentWorkflowInput
            {
                AgentName = sessionId.AgentName,
                TaskQueue = taskQueue,
                TimeToLive = options.GetTimeToLive(sessionId.AgentName),
                ActivityStartToCloseTimeout = options.ActivityStartToCloseTimeout,
                ActivityHeartbeatTimeout = options.ActivityHeartbeatTimeout
            }),
            workflowOptions);

        var handle = client.GetWorkflowHandle<AgentWorkflow>(sessionId.WorkflowId);
        await handle.SignalAsync<AgentWorkflow>(wf => wf.RunAgentFireAndForgetAsync(request));
    }

    // ── GAP 2: Routing ──────────────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task<AgentResponse> RouteAsync(
        string sessionKey,
        RunRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionKey);
        ArgumentNullException.ThrowIfNull(request);

        if (router is null)
        {
            throw new InvalidOperationException(
                "No IAgentRouter is configured. Call SetRouterAgent() on TemporalAgentsOptions to enable LLM routing.");
        }

        var descriptors = options.GetAgentDescriptors();
        if (descriptors.Count == 0)
        {
            throw new InvalidOperationException(
                "No agent descriptors are registered. Call AddAgentDescriptor() on TemporalAgentsOptions for each routable agent.");
        }

        var chosenAgentName = await router
            .RouteAsync(request.Messages, descriptors, cancellationToken)
            .ConfigureAwait(false);

        var routedSessionId = new TemporalAgentSessionId(chosenAgentName, sessionKey);

        Logs.LogClientRouting(_logger, chosenAgentName, routedSessionId.WorkflowId);

        return await RunAgentAsync(routedSessionId, request, cancellationToken).ConfigureAwait(false);
    }

    // ── GAP 3: Human-in-the-Loop ────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task<ApprovalRequest?> GetPendingApprovalAsync(
        TemporalAgentSessionId sessionId,
        CancellationToken cancellationToken = default)
    {
        var handle = client.GetWorkflowHandle<AgentWorkflow>(sessionId.WorkflowId);
        return await handle.QueryAsync<AgentWorkflow, ApprovalRequest?>(
            wf => wf.GetPendingApproval());
    }

    /// <inheritdoc/>
    public async Task<ApprovalTicket> SubmitApprovalAsync(
        TemporalAgentSessionId sessionId,
        ApprovalDecision decision,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(decision);

        var handle = client.GetWorkflowHandle<AgentWorkflow>(sessionId.WorkflowId);
        return await handle.ExecuteUpdateAsync<AgentWorkflow, ApprovalTicket>(
            wf => wf.SubmitApprovalAsync(decision));
    }
}
