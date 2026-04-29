using System.Diagnostics;
using Microsoft.Agents.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Temporalio.Api.Enums.V1;
using Temporalio.Client;
using Temporalio.Client.Schedules;
using Temporalio.Extensions.Agents.Session;
using Temporalio.Extensions.AI;

namespace Temporalio.Extensions.Agents.Workflows;

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
                ActivityHeartbeatTimeout = options.ActivityHeartbeatTimeout,
                ApprovalTimeout = options.ApprovalTimeout
            }),
            workflowOptions).ConfigureAwait(false);

        // Use a handle WITHOUT a pinned RunId so updates follow the continue-as-new chain.
        var handle = client.GetWorkflowHandle<AgentWorkflow>(sessionId.WorkflowId);

        var response = await handle.ExecuteUpdateAsync<AgentWorkflow, AgentResponse>(
            wf => wf.RunAgentAsync(request)).ConfigureAwait(false);

        _logger.LogClientUpdateCompleted(sessionId.AgentName, sessionId.WorkflowId);
        return response;
    }

    /// <inheritdoc/>
    public Task<AgentResponse> RunAgentAsync(
        string agentName,
        string message,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentName);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);

        var sessionId = TemporalAgentSessionId.WithRandomKey(agentName);
        var request = new RunRequest(message)
        {
            CorrelationId = Guid.NewGuid().ToString("N"),
        };
        return RunAgentAsync(sessionId, request, cancellationToken);
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
                ActivityHeartbeatTimeout = options.ActivityHeartbeatTimeout,
                ApprovalTimeout = options.ApprovalTimeout
            }),
            workflowOptions).ConfigureAwait(false);

        var handle = client.GetWorkflowHandle<AgentWorkflow>(sessionId.WorkflowId);
        await handle.SignalAsync<AgentWorkflow>(wf => wf.RunAgentFireAndForgetAsync(request)).ConfigureAwait(false);
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

        _logger.LogClientRouting(chosenAgentName, routedSessionId.WorkflowId);

        return await RunAgentAsync(routedSessionId, request, cancellationToken).ConfigureAwait(false);
    }

    // ── GAP 3: Human-in-the-Loop ────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task<DurableApprovalRequest?> GetPendingApprovalAsync(
        TemporalAgentSessionId sessionId,
        CancellationToken cancellationToken = default)
    {
        var handle = client.GetWorkflowHandle<AgentWorkflow>(sessionId.WorkflowId);
        return await handle.QueryAsync<AgentWorkflow, DurableApprovalRequest?>(
            wf => wf.GetPendingApproval()).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<DurableApprovalDecision> SubmitApprovalAsync(
        TemporalAgentSessionId sessionId,
        DurableApprovalDecision decision,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(decision);

        var handle = client.GetWorkflowHandle<AgentWorkflow>(sessionId.WorkflowId);
        return await handle.ExecuteUpdateAsync<AgentWorkflow, DurableApprovalDecision>(
            wf => wf.SubmitApprovalAsync(decision)).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task RunAgentDelayedAsync(
        TemporalAgentSessionId sessionId,
        RunRequest request,
        TimeSpan delay,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var span = TemporalAgentTelemetry.ActivitySource.StartActivity(
            TemporalAgentTelemetry.AgentScheduleDelayedSpanName,
            ActivityKind.Client);

        span?.SetTag(TemporalAgentTelemetry.AgentNameAttribute, sessionId.AgentName);
        span?.SetTag(TemporalAgentTelemetry.AgentSessionIdAttribute, sessionId.WorkflowId);
        span?.SetTag(TemporalAgentTelemetry.ScheduleDelayAttribute, delay.ToString());

        _logger.LogClientDelayedStart(sessionId.AgentName, sessionId.WorkflowId, delay);

        // StartDelay only applies when starting a NEW workflow. If the session workflow is
        // already running (UseExisting policy), the delay is ignored and the existing run is
        // reused immediately. This is documented as a known limitation.
        var workflowOptions = new WorkflowOptions(sessionId.WorkflowId, taskQueue)
        {
            IdConflictPolicy = WorkflowIdConflictPolicy.UseExisting,
            IdReusePolicy = WorkflowIdReusePolicy.AllowDuplicate,
            StartDelay = delay,
        };

        try
        {
            await client.StartWorkflowAsync(
                (AgentWorkflow wf) => wf.RunAsync(new AgentWorkflowInput
                {
                    AgentName = sessionId.AgentName,
                    TaskQueue = taskQueue,
                    TimeToLive = options.GetTimeToLive(sessionId.AgentName),
                    ActivityStartToCloseTimeout = options.ActivityStartToCloseTimeout,
                    ActivityHeartbeatTimeout = options.ActivityHeartbeatTimeout,
                    ApprovalTimeout = options.ApprovalTimeout
                }),
                workflowOptions).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            span?.SetStatus(ActivityStatusCode.Error, ex.Message);
            throw;
        }
    }

    /// <inheritdoc/>
    public async Task<ScheduleHandle> ScheduleAgentAsync(
        string agentName,
        string scheduleId,
        RunRequest request,
        ScheduleSpec spec,
        SchedulePolicy? policy = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentName);
        ArgumentException.ThrowIfNullOrWhiteSpace(scheduleId);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(spec);

        // Each schedule fire gets a deterministic workflow ID. Temporal appends a timestamp
        // automatically: ta-weatheragent-scheduled-daily-2026-02-28T09:00:00Z
        var workflowId = $"ta-{agentName.ToLowerInvariant()}-scheduled-{scheduleId}";

        var action = ScheduleActionStartWorkflow.Create(
            (AgentJobWorkflow wf) => wf.RunAsync(new AgentJobInput
            {
                AgentName = agentName,
                TaskQueue = taskQueue,
                Request = request,
                ActivityStartToCloseTimeout = options.ActivityStartToCloseTimeout,
                ActivityHeartbeatTimeout = options.ActivityHeartbeatTimeout,
            }),
            new WorkflowOptions(workflowId, taskQueue));

        using var span = TemporalAgentTelemetry.ActivitySource.StartActivity(
            TemporalAgentTelemetry.AgentScheduleCreateSpanName,
            ActivityKind.Client);

        span?.SetTag(TemporalAgentTelemetry.AgentNameAttribute, agentName);
        span?.SetTag(TemporalAgentTelemetry.ScheduleIdAttribute, scheduleId);

        _logger.LogScheduleAgentCreating(scheduleId, agentName);

        try
        {
            return await client.CreateScheduleAsync(
                scheduleId,
                new Schedule(action, spec) { Policy = policy ?? new SchedulePolicy() }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            span?.SetStatus(ActivityStatusCode.Error, ex.Message);
            throw;
        }
    }

    /// <inheritdoc/>
    public ScheduleHandle GetAgentScheduleHandle(string scheduleId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scheduleId);
        return client.GetScheduleHandle(scheduleId);
    }
}
