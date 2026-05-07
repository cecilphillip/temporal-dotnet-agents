using System.Diagnostics;
using Microsoft.Agents.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Temporalio.Api.Enums.V1;
using Temporalio.Client;
using Temporalio.Client.Schedules;
using Temporalio.Common;
using Temporalio.Extensions.Agents.Session;
using Temporalio.Extensions.Agents.State;
using Temporalio.Extensions.AI;
using Temporalio.Workflows;

namespace Temporalio.Extensions.Agents.Workflows;

/// <summary>
/// Default implementation of <see cref="ITemporalAgentClient"/> that communicates with
/// <see cref="AgentWorkflow"/> via Temporal workflow updates (no polling).
/// </summary>
internal sealed class DefaultTemporalAgentClient(
    ITemporalClient client,
    TemporalAgentsOptions options,
    string taskQueue,
    ILogger<DefaultTemporalAgentClient>? logger = null) : ITemporalAgentClient
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

        workflowOptions.Rpc = new RpcOptions { CancellationToken = cancellationToken };
        await client.StartWorkflowAsync(
            (AgentWorkflow wf) => wf.RunAsync(BuildAgentWorkflowInput(sessionId.AgentName)),
            workflowOptions).ConfigureAwait(false);

        // Use a handle WITHOUT a pinned RunId so updates follow the continue-as-new chain.
        var handle = client.GetWorkflowHandle<AgentWorkflow>(sessionId.WorkflowId);

        var response = await handle.ExecuteUpdateAsync<AgentWorkflow, AgentResponse>(
            wf => wf.RunAgentAsync(request),
            new WorkflowUpdateOptions { Rpc = new RpcOptions { CancellationToken = cancellationToken } })
            .ConfigureAwait(false);

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

        workflowOptions.Rpc = new RpcOptions { CancellationToken = cancellationToken };
        await client.StartWorkflowAsync(
            (AgentWorkflow wf) => wf.RunAsync(BuildAgentWorkflowInput(sessionId.AgentName)),
            workflowOptions).ConfigureAwait(false);

        var handle = client.GetWorkflowHandle<AgentWorkflow>(sessionId.WorkflowId);
        await handle.SignalAsync<AgentWorkflow>(
            wf => wf.RunAgentFireAndForgetAsync(request),
            new WorkflowSignalOptions { Rpc = new RpcOptions { CancellationToken = cancellationToken } })
            .ConfigureAwait(false);
    }

    // ── GAP 3: Human-in-the-Loop ────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task<DurableApprovalRequest?> GetPendingApprovalAsync(
        TemporalAgentSessionId sessionId,
        CancellationToken cancellationToken = default)
    {
        var handle = client.GetWorkflowHandle<AgentWorkflow>(sessionId.WorkflowId);
        return await handle.QueryAsync<AgentWorkflow, DurableApprovalRequest?>(
            wf => wf.GetPendingApproval(),
            new WorkflowQueryOptions { Rpc = new RpcOptions { CancellationToken = cancellationToken } })
            .ConfigureAwait(false);
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
            wf => wf.SubmitApprovalAsync(decision),
            new WorkflowUpdateOptions { Rpc = new RpcOptions { CancellationToken = cancellationToken } })
            .ConfigureAwait(false);
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

        workflowOptions.Rpc = new RpcOptions { CancellationToken = cancellationToken };

        try
        {
            await client.StartWorkflowAsync(
                (AgentWorkflow wf) => wf.RunAsync(BuildAgentWorkflowInput(sessionId.AgentName)),
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
                ActivityTimeout = options.ActivityTimeout,
                HeartbeatTimeout = options.HeartbeatTimeout,
                RetryPolicy = options.RetryPolicy,
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
                new Schedule(action, spec) { Policy = policy ?? new SchedulePolicy() },
                new ScheduleOptions { Rpc = new RpcOptions { CancellationToken = cancellationToken } }).ConfigureAwait(false);
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

    /// <summary>
    /// Constructs the <see cref="AgentWorkflowInput"/> for a session, applying the v0.3
    /// settings-inheritance rule: when the agent name resolves to a
    /// <see cref="DurableAgentRegistration"/>, per-agent overrides take precedence over the
    /// worker-level <see cref="TemporalAgentsOptions"/> values; legacy registrations continue to
    /// use the worker-level values verbatim.
    /// </summary>
    /// <remarks>
    /// Phase 3 wires the durability flag (<see cref="AgentWorkflowInput.IsDurable"/>) and the
    /// per-tool activity options dictionary
    /// (<see cref="AgentWorkflowInput.DurableAgentToolActivityOptions"/>). Phase 4 broadens the
    /// inheritance to include all per-agent scalars (timeouts, retry policy, max entry count,
    /// history reducer, etc.). Until then, scalars continue to flow from the worker-level options
    /// unchanged.
    /// </remarks>
    private AgentWorkflowInput BuildAgentWorkflowInput(string agentName)
    {
        var registration = options.DurableAgentRegistrations.GetValueOrDefault(agentName);

        Dictionary<string, ActivityOptions>? toolActivityOptions = null;
        if (registration is not null)
        {
            toolActivityOptions = BuildDurableAgentToolActivityOptions(registration);
        }

        return new AgentWorkflowInput
        {
            AgentName = agentName,
            TaskQueue = taskQueue,
            TimeToLive = options.GetTimeToLive(agentName) ?? TimeSpan.FromDays(14),
            ActivityTimeout = options.ActivityTimeout,
            HeartbeatTimeout = options.HeartbeatTimeout,
            ApprovalTimeout = options.ApprovalTimeout,
            RetryPolicy = options.RetryPolicy,
            MaxEntryCount = options.MaxEntryCount,
            HistoryReducer = options.HistoryReducer,
            EnableSearchAttributes = options.EnableSearchAttributes,
            UseExternalStore = options.UseExternalHistory,
            EnablePerToolActivities = options.EnablePerToolActivities,
            PerToolActivityOptions = options.PerToolActivityOptions,
            MaxToolCallsPerTurn = registration?.MaxToolCallsPerTurn ?? options.MaxToolCallsPerTurn,
            IsDurable = registration is not null,
            DurableAgentToolActivityOptions = toolActivityOptions,
            // OriginalCreatedAt intentionally omitted — null on first run, set by the workflow on CAN
        };
    }

    /// <summary>
    /// Pre-computes the per-tool <see cref="ActivityOptions"/> dictionary from a durable agent's
    /// tool registrations. Each entry uses the per-tool overrides where set, falling back to the
    /// worker-level defaults for unset fields. Built at workflow start so retry constraints are
    /// pinned at the time the workflow began running and survive across continue-as-new
    /// transitions (the dictionary travels with <see cref="AgentWorkflowInput"/>).
    /// </summary>
    private Dictionary<string, ActivityOptions> BuildDurableAgentToolActivityOptions(
        DurableAgentRegistration registration)
    {
        var result = new Dictionary<string, ActivityOptions>(StringComparer.OrdinalIgnoreCase);
        var workerActivityTimeout = options.ActivityTimeout;
        var workerHeartbeatTimeout = options.HeartbeatTimeout;
        RetryPolicy? workerRetryPolicy = options.RetryPolicy;

        foreach (var tool in registration.Tools)
        {
            var toolOpts = tool.Options;
            result[tool.Name] = new ActivityOptions
            {
                StartToCloseTimeout = toolOpts.StartToCloseTimeout ?? workerActivityTimeout,
                HeartbeatTimeout = toolOpts.HeartbeatTimeout ?? workerHeartbeatTimeout,
                RetryPolicy = toolOpts.RetryPolicy ?? workerRetryPolicy,
                Summary = tool.Name,
            };
        }

        return result;
    }
}
