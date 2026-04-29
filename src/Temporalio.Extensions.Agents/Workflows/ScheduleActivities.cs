using System.Diagnostics;
using Temporalio.Activities;
using Temporalio.Api.Enums.V1;
using Temporalio.Client;

namespace Temporalio.Extensions.Agents.Workflows;

/// <summary>
/// Temporal activities for scheduling deferred, one-time agent runs from inside orchestrating workflows.
/// </summary>
/// <remarks>
/// <para>
/// Use this from inside a <c>[WorkflowRun]</c> method when you want to schedule a future agent
/// invocation without blocking the current workflow:
/// </para>
/// <code>
/// await Workflow.ExecuteActivityAsync(
///     (ScheduleActivities a) => a.ScheduleOneTimeAgentRunAsync(new OneTimeAgentRun
///     {
///         AgentName = "ResearchAnalyst",
///         RunId     = "followup-q1",
///         Request   = new RunRequest("Compare today's data against last week's findings."),
///         RunAt     = Workflow.UtcNow + TimeSpan.FromDays(7)
///     }),
///     new ActivityOptions { StartToCloseTimeout = TimeSpan.FromSeconds(30) });
/// </code>
/// <para>
/// Internally this uses <c>StartDelay</c> on <see cref="ITemporalClient.StartWorkflowAsync"/>,
/// which leaves a single visible workflow execution in the Temporal Web UI rather than a
/// persistent schedule entity. This avoids zombie schedules after the single run completes.
/// </para>
/// </remarks>
public sealed class ScheduleActivities(ITemporalClient client, string taskQueue)
{
    /// <summary>
    /// Schedules a one-time, deferred <see cref="AgentJobWorkflow"/> run.
    /// </summary>
    /// <param name="run">Describes the agent, run identifier, request, and target time.</param>
    /// <remarks>
    /// <para>
    /// The resulting workflow ID is <c>ta-{agentName}-scheduled-{runId}</c>. If the activity
    /// retries after a crash-before-ack, <c>UseExisting</c> conflict policy ensures idempotency —
    /// a second <c>StartWorkflowAsync</c> call finds the already-scheduled workflow and returns normally.
    /// </para>
    /// <para>
    /// If <see cref="OneTimeAgentRun.RunAt"/> is in the past when this activity executes,
    /// the agent run starts immediately (delay clamped to zero).
    /// </para>
    /// </remarks>
    [Activity]
    public async Task ScheduleOneTimeAgentRunAsync(OneTimeAgentRun run)
    {
        ArgumentNullException.ThrowIfNull(run);

        var delay = run.RunAt - DateTimeOffset.UtcNow;
        if (delay < TimeSpan.Zero)
        {
            delay = TimeSpan.Zero;
        }

        using var span = TemporalAgentTelemetry.ActivitySource.StartActivity(
            TemporalAgentTelemetry.AgentScheduleOneTimeSpanName,
            ActivityKind.Internal);

        span?.SetTag(TemporalAgentTelemetry.AgentNameAttribute, run.AgentName);
        span?.SetTag(TemporalAgentTelemetry.ScheduleJobIdAttribute, run.RunId);
        span?.SetTag(TemporalAgentTelemetry.ScheduleDelayAttribute, delay.ToString());

        var workflowId = $"ta-{run.AgentName.ToLowerInvariant()}-scheduled-{run.RunId}";

        try
        {
            await client.StartWorkflowAsync(
                (AgentJobWorkflow wf) => wf.RunAsync(new AgentJobInput
                {
                    AgentName = run.AgentName,
                    TaskQueue = taskQueue,
                    Request = run.Request,
                    RetryPolicy = run.RetryPolicy,
                }),
                new WorkflowOptions(workflowId, taskQueue)
                {
                    StartDelay = delay,
                    IdConflictPolicy = WorkflowIdConflictPolicy.UseExisting,
                });
        }
        catch (Exception ex)
        {
            span?.SetStatus(ActivityStatusCode.Error, ex.Message);
            throw;
        }
    }
}
