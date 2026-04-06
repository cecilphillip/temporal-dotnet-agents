using System.Linq.Expressions;
using Microsoft.Extensions.DependencyInjection;
using Temporalio.Client;
using Temporalio.Exceptions;
using Temporalio.Extensions.AI;
using Temporalio.Extensions.Agents.Workflows;

namespace Temporalio.Extensions.Agents.Session;

/// <summary>
/// Provides async-local access to Temporal capabilities for agent tools executing inside
/// an <see cref="AgentActivities.ExecuteAgentAsync"/> activity.
/// Equivalent to <c>DurableAgentContext</c>.
/// </summary>
public sealed class TemporalAgentContext
{
    private static readonly AsyncLocal<TemporalAgentContext?> s_current = new();
    private readonly ITemporalClient _client;
    private readonly IServiceProvider _services;

    internal TemporalAgentContext(
        ITemporalClient client,
        TemporalAgentSession session,
        IServiceProvider services)
    {
        _client = client;
        CurrentSession = session;
        _services = services;
    }

    /// <summary>Gets the current <see cref="TemporalAgentContext"/>.</summary>
    /// <exception cref="InvalidOperationException">Thrown when no context is set.</exception>
    public static TemporalAgentContext Current =>
        s_current.Value ?? throw new InvalidOperationException("No TemporalAgentContext is available in the current async context.");

    internal static void SetCurrent(TemporalAgentContext? ctx) => s_current.Value = ctx;

    /// <summary>Gets the current agent session.</summary>
    public TemporalAgentSession CurrentSession { get; }

    /// <summary>Starts a new workflow and returns its workflow ID.</summary>
    public async Task<string> StartWorkflowAsync<TWorkflow>(
        Expression<Func<TWorkflow, Task>> workflowRunCall,
        WorkflowOptions options)
    {
        var handle = await _client.StartWorkflowAsync(workflowRunCall, options);
        return handle.Id;
    }

    /// <summary>Gets the description of an existing workflow.</summary>
    public async Task<WorkflowExecutionDescription?> GetWorkflowDescriptionAsync(string workflowId)
    {
        try
        {
            var handle = _client.GetWorkflowHandle(workflowId);
            return await handle.DescribeAsync();
        }
        catch (RpcException)
        {
            return null;
        }
    }

    /// <summary>Sends a signal to an existing workflow.</summary>
    public Task SignalWorkflowAsync<TWorkflow>(
        string workflowId,
        Expression<Func<TWorkflow, Task>> signalCall)
    {
        var handle = _client.GetWorkflowHandle<TWorkflow>(workflowId);
        return handle.SignalAsync(signalCall);
    }

    /// <summary>Gets a service from the DI container.</summary>
    public TService? GetService<TService>(object? serviceKey = null)
    {
        return (TService?)GetService(typeof(TService), serviceKey);
    }

    /// <summary>Gets a service from the DI container.</summary>
    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        if (serviceKey is not null)
        {
            if (_services is not IKeyedServiceProvider ksp)
            {
                throw new InvalidOperationException("The service provider does not support keyed services.");
            }

            return ksp.GetKeyedService(serviceType, serviceKey);
        }

        return _services.GetService(serviceType);
    }

    // ── GAP 3: Human-in-the-Loop ────────────────────────────────────────────

    /// <summary>
    /// Sends an approval request to the backing <see cref="AgentWorkflow"/> and blocks
    /// until a human submits a decision via <see cref="ITemporalAgentClient.SubmitApprovalAsync"/>.
    /// </summary>
    /// <remarks>
    /// Call this from inside a tool implementation when the action requires human review:
    /// <code>
    /// var decision = await TemporalAgentContext.Current.RequestApprovalAsync(
    ///     new DurableApprovalRequest { RequestId = Guid.NewGuid().ToString("N"), Description = "Send email to all users" });
    /// if (!decision.Approved) throw new OperationCanceledException("Action rejected by reviewer.");
    /// </code>
    /// <para>
    /// <b>Timeout note:</b> the calling activity blocks for the duration of human review.
    /// Ensure <c>ActivityStartToCloseTimeout</c> is set to exceed your expected review time
    /// (e.g. <c>TimeSpan.FromHours(24)</c>) on the agent's <see cref="TemporalAgentsOptions"/>.
    /// </para>
    /// <para>
    /// <b>Cancellation risk:</b> if the activity is cancelled or times out while the workflow is
    /// waiting for a human response, <c>_pendingApproval</c> remains set in the workflow state.
    /// The workflow will then reject any new <c>RunAgentAsync</c> updates until the stale approval
    /// is resolved. To recover, submit an explicit denial externally using
    /// <see cref="ITemporalAgentClient.SubmitApprovalAsync"/> with a <see cref="DurableApprovalDecision"/>
    /// whose <c>Approved</c> is <see langword="false"/>.
    /// </para>
    /// </remarks>
    public async Task<DurableApprovalDecision> RequestApprovalAsync(
        DurableApprovalRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var handle = _client.GetWorkflowHandle<AgentWorkflow>(CurrentSession.SessionId.WorkflowId);
        return await handle.ExecuteUpdateAsync<AgentWorkflow, DurableApprovalDecision>(
            wf => wf.RequestApprovalAsync(request));
    }
}
