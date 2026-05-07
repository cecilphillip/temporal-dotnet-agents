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
    /// Phase 4 (v0.3): every per-agent scalar on <see cref="DurableAgentRegistration"/>
    /// (TimeToLive, ApprovalTimeout, ActivityTimeout, HeartbeatTimeout, RetryPolicy,
    /// MaxEntryCount, MaxToolCallsPerTurn, HistoryReducer) flows through the inheritance rule
    /// <c>effective = registration.X ?? options.X</c>. The worker-level property names are still
    /// the v0.2 forms (no <c>Default*</c> prefix); Phase 5 will rename them as a breaking change.
    /// <para>
    /// External history is opted into when <em>either</em> <see cref="DurableAgentRegistration.HistoryStore"/>
    /// or <see cref="TemporalAgentsOptions.HistoryStore"/> is non-null; the workflow flag
    /// <see cref="AgentWorkflowInput.UseExternalStore"/> reflects this composite decision so the
    /// workflow side can omit history from <see cref="ExecuteAgentInput.ConversationHistory"/>
    /// and skip carry-forward at continue-as-new. The legacy
    /// <see cref="TemporalAgentsOptions.UseExternalHistory"/> flag still drives this for legacy
    /// agents.
    /// </para>
    /// </remarks>
    internal AgentWorkflowInput BuildAgentWorkflowInput(string agentName) =>
        BuildAgentWorkflowInputCore(agentName, options, taskQueue);

    /// <summary>
    /// Pure builder used by <see cref="BuildAgentWorkflowInput(string)"/> and unit tests.
    /// Extracted so the inheritance rule can be exercised without instantiating an
    /// <see cref="ITemporalClient"/> or starting a worker.
    /// </summary>
    internal static AgentWorkflowInput BuildAgentWorkflowInputCore(
        string agentName,
        TemporalAgentsOptions options,
        string taskQueue)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentName);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(taskQueue);

        var registration = options.DurableAgentRegistrations.GetValueOrDefault(agentName);

        if (registration is null)
        {
            // Legacy path — preserve v0.2 behavior verbatim. Worker-level scalars flow through
            // unchanged so existing AddAIAgent/AddAIAgentFactory callers see no behavior change.
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
                MaxToolCallsPerTurn = options.MaxToolCallsPerTurn,
                IsDurable = false,
            };
        }

        // Durable path (Phase 4): apply the inheritance rule for every settable scalar.
        // For each setting, prefer the registration's value when set; otherwise fall back to the
        // worker-level default. The per-agent registration's TimeToLive is also reflected in
        // the legacy _agentTimeToLive map by AddDurableAgent so options.GetTimeToLive() still
        // returns the per-agent value here — but we go through the registration directly for
        // clarity and to avoid the tiny risk of map drift.
        var perAgentTimeToLive = registration.TimeToLive ?? options.GetTimeToLive(agentName) ?? TimeSpan.FromDays(14);
        var perAgentActivityTimeout = registration.ActivityTimeout ?? options.ActivityTimeout;
        var perAgentHeartbeatTimeout = registration.HeartbeatTimeout ?? options.HeartbeatTimeout;
        var perAgentApprovalTimeout = registration.ApprovalTimeout ?? options.ApprovalTimeout;
        var perAgentRetryPolicy = registration.RetryPolicy ?? options.RetryPolicy;
        var perAgentMaxEntryCount = registration.MaxEntryCount ?? options.MaxEntryCount;
        var perAgentHistoryReducer = registration.HistoryReducer ?? options.HistoryReducer;

        // Compose per-tool activity options using the resolved per-agent timeouts/retry policy
        // as defaults so write tools that don't set explicit overrides still inherit from the
        // per-agent values rather than the worker-wide defaults.
        var toolActivityOptions = BuildDurableAgentToolActivityOptions(
            registration,
            perAgentActivityTimeout,
            perAgentHeartbeatTimeout,
            perAgentRetryPolicy);

        // Q6 inheritance: external store is opt-in via either the per-agent or the worker-level
        // factory. The activity-side resolution looks at the same composite condition — see
        // AgentActivities.ComposeDurableAgent.
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
            UseExternalStore = hasExternalStore,
            // Legacy step-mode settings are intentionally not propagated to durable agents —
            // the durable workflow branch supersedes step mode (see AgentWorkflow.cs comments).
            EnablePerToolActivities = false,
            PerToolActivityOptions = null,
            MaxToolCallsPerTurn = registration.MaxToolCallsPerTurn,
            IsDurable = true,
            DurableAgentToolActivityOptions = toolActivityOptions,
            // OriginalCreatedAt intentionally omitted — null on first run, set by the workflow on CAN
        };
    }

    /// <summary>
    /// Pre-computes the per-tool <see cref="ActivityOptions"/> dictionary from a durable agent's
    /// tool registrations. Each entry uses the per-tool overrides where set, falling back to the
    /// supplied per-agent defaults (which themselves cascade from worker-level defaults). Built
    /// at workflow start so retry constraints are pinned at the time the workflow began running
    /// and survive across continue-as-new transitions (the dictionary travels with
    /// <see cref="AgentWorkflowInput"/>).
    /// </summary>
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
