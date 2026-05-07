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

        var workflowId = $"ta-{agentName.ToLowerInvariant()}-scheduled-{scheduleId}";

        var action = ScheduleActionStartWorkflow.Create(
            (AgentJobWorkflow wf) => wf.RunAsync(new AgentJobInput
            {
                AgentName = agentName,
                TaskQueue = taskQueue,
                Request = request,
                ActivityTimeout = options.DefaultActivityTimeout,
                HeartbeatTimeout = options.DefaultHeartbeatTimeout,
                RetryPolicy = options.DefaultRetryPolicy,
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
    /// Constructs the <see cref="AgentWorkflowInput"/> for a session by resolving every per-agent
    /// scalar via the inheritance rule: <c>effective = registration.X ?? options.DefaultX</c>.
    /// </summary>
    /// <exception cref="AgentNotRegisteredException">
    /// Thrown when no durable-agent registration exists for <paramref name="agentName"/>.
    /// Proxy-only declarations (<see cref="TemporalAgentsOptions.AddAgentProxy"/>) cannot start
    /// a session locally — that path runs in the worker process where the durable agent is
    /// registered.
    /// </exception>
    internal AgentWorkflowInput BuildAgentWorkflowInput(string agentName) =>
        BuildAgentWorkflowInputCore(agentName, options, taskQueue);

    /// <summary>
    /// Pure builder used by <see cref="BuildAgentWorkflowInput(string)"/> and unit tests.
    /// </summary>
    internal static AgentWorkflowInput BuildAgentWorkflowInputCore(
        string agentName,
        TemporalAgentsOptions options,
        string taskQueue)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentName);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(taskQueue);

        if (!options.DurableAgentRegistrations.TryGetValue(agentName, out var registration))
        {
            // Proxy-only declarations don't have a registration on this side — but for proxy
            // clients, BuildAgentWorkflowInput should never be invoked in-process; the proxy
            // dispatches updates to the worker. Throw a clear error so that misconfiguration
            // (e.g. running RunAgentAsync from a process that only declared AddAgentProxy)
            // surfaces immediately.
            throw new AgentNotRegisteredException(agentName);
        }

        var perAgentTimeToLive = registration.TimeToLive ?? options.DefaultTimeToLive ?? TimeSpan.FromDays(14);
        var perAgentActivityTimeout = registration.ActivityTimeout ?? options.DefaultActivityTimeout;
        var perAgentHeartbeatTimeout = registration.HeartbeatTimeout ?? options.DefaultHeartbeatTimeout;
        var perAgentApprovalTimeout = registration.ApprovalTimeout ?? options.DefaultApprovalTimeout;
        var perAgentRetryPolicy = registration.RetryPolicy ?? options.DefaultRetryPolicy;
        var perAgentMaxEntryCount = registration.MaxEntryCount ?? options.DefaultMaxEntryCount;
        var perAgentHistoryReducer = registration.HistoryReducer ?? options.DefaultHistoryReducer;

        var toolActivityOptions = BuildDurableAgentToolActivityOptions(
            registration,
            perAgentActivityTimeout,
            perAgentHeartbeatTimeout,
            perAgentRetryPolicy);

        var hasExternalStore = registration.HistoryStore is not null || options.HistoryStore is not null;

        return new AgentWorkflowInput
        {
            AgentName = agentName,
            TaskQueue = taskQueue,
            TimeToLive = perAgentTimeToLive,
            ActivityTimeout = perAgentActivityTimeout,
            HeartbeatTimeout = perAgentHeartbeatTimeout,
            ApprovalTimeout = perAgentApprovalTimeout,
            RetryPolicy = perAgentRetryPolicy,
            MaxEntryCount = perAgentMaxEntryCount,
            HistoryReducer = perAgentHistoryReducer,
            EnableSearchAttributes = options.EnableSearchAttributes,
            UseExternalStoreMode = hasExternalStore,
            MaxToolCallsPerTurn = registration.MaxToolCallsPerTurn,
            DurableAgentToolActivityOptions = toolActivityOptions,
        };
    }

    private static Dictionary<string, ActivityOptions> BuildDurableAgentToolActivityOptions(
        DurableAgentRegistration registration,
        TimeSpan defaultActivityTimeout,
        TimeSpan defaultHeartbeatTimeout,
        RetryPolicy? defaultRetryPolicy)
    {
        var result = new Dictionary<string, ActivityOptions>(StringComparer.OrdinalIgnoreCase);

        foreach (var tool in registration.Tools)
        {
            var toolOpts = tool.Options;
            result[tool.Name] = new ActivityOptions
            {
                StartToCloseTimeout = toolOpts.StartToCloseTimeout ?? defaultActivityTimeout,
                HeartbeatTimeout = toolOpts.HeartbeatTimeout ?? defaultHeartbeatTimeout,
                RetryPolicy = toolOpts.RetryPolicy ?? defaultRetryPolicy,
                Summary = tool.Name,
            };
        }

        return result;
    }
}
