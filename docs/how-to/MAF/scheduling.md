# Scheduling Agent Runs

How to schedule recurring and one-time agent runs — from config-time registration to programmatic schedule management and deferred workflows.

---

## Table of Contents

1. [Overview](#overview)
2. [Two Workflow Types](#two-workflow-types)
3. [Recurring Schedules](#recurring-schedules)
4. [One-Time Deferred Runs](#one-time-deferred-runs)
5. [Schedule Lifecycle Management](#schedule-lifecycle-management)
6. [Workflow ID Conventions](#workflow-id-conventions)
7. [Observability](#observability)
8. [Pitfalls and Gotchas](#pitfalls-and-gotchas)
9. [Choosing the Right Primitive](#choosing-the-right-primitive)

---

## Overview

TemporalAgents provides four scheduling primitives, each suited to a different context:

| Primitive | Context | Recurrence | Workflow Type |
|-----------|---------|------------|---------------|
| `AddScheduledAgentRun` | Config time | Recurring | `AgentJobWorkflow` |
| `ITemporalAgentClient.ScheduleAgentAsync` | Runtime (external) | Recurring | `AgentJobWorkflow` |
| `ScheduleActivities.ScheduleOneTimeAgentRunAsync` | Inside a workflow | One-time | `AgentJobWorkflow` |
| `ITemporalAgentClient.RunAgentDelayedAsync` | Runtime (external) | One-time | `AgentWorkflow` |

The first three use `AgentJobWorkflow` — a lightweight, fire-and-forget workflow. The fourth uses the full `AgentWorkflow` with conversation history and StateBag.

---

## Two Workflow Types

Understanding the distinction between these two workflows is key to choosing the right scheduling approach.

### AgentJobWorkflow (Scheduled/Deferred)

A minimal workflow that runs a single agent activity and exits:

```csharp
// Internal — you don't instantiate this directly
[Workflow("Temporalio.Extensions.Agents.AgentJobWorkflow")]
internal sealed class AgentJobWorkflow
{
    [WorkflowRun]
    public async Task RunAsync(AgentJobInput input)
    {
        var activityInput = new ExecuteAgentInput(
            input.AgentName,
            input.Request,
            []);  // empty history — no prior context

        await Workflow.ExecuteActivityAsync(
            (AgentActivities a) => a.ExecuteAgentAsync(activityInput),
            new ActivityOptions
            {
                StartToCloseTimeout = input.ActivityTimeout,
                HeartbeatTimeout = input.HeartbeatTimeout,
            });
    }
}
```

**Properties:**
- No conversation history — starts fresh every time
- No StateBag persistence
- No TTL loop or `[WorkflowUpdate]` handlers
- No continue-as-new
- Result is visible in the Temporal Web UI event history

### AgentWorkflow (Full Session)

The standard long-lived workflow with conversation history, StateBag, HITL, and continue-as-new. Only `RunAgentDelayedAsync` uses this for scheduling, because it creates a full session that can receive follow-up messages after the initial delayed run.

---

## Recurring Schedules

### Config-Time Registration

Declare scheduled runs inside `AddTemporalAgents`. The `ScheduleRegistrationService` (a `BackgroundService`) creates them automatically when the worker starts:

```csharp
builder.Services
    .AddHostedTemporalWorker("localhost:7233", "default", "agents")
    .AddTemporalAgents(opts =>
    {
        opts.AddAIAgent(summaryAgent);

        opts.AddScheduledAgentRun(
            agentName: "SummaryAgent",
            scheduleId: "daily-summary",
            request: new RunRequest("Summarize today's activity report."),
            spec: new ScheduleSpec
            {
                Intervals = [new ScheduleIntervalSpec(Every: TimeSpan.FromDays(1))]
            });
    });
```

**What happens on worker restart:** If the schedule already exists (e.g., from a previous startup), a `ScheduleAlreadyRunningException` is caught, a warning is logged, and the existing schedule is left untouched. The worker does **not** overwrite or update the schedule.

### Programmatic Scheduling

Call `ScheduleAgentAsync` at any time to create a Temporal Schedule. The returned `ScheduleHandle` gives you full lifecycle control:

```csharp
ITemporalAgentClient client = // resolved from DI

ScheduleHandle handle = await client.ScheduleAgentAsync(
    agentName: "ReportAgent",
    scheduleId: "weekly-report",
    request: new RunRequest("Generate the weekly metrics report."),
    spec: new ScheduleSpec
    {
        Calendars =
        [
            new ScheduleCalendarSpec
            {
                Hour = [new ScheduleRange(9)],
                DayOfWeek = [new ScheduleRange(1)]  // Monday at 9:00
            }
        ]
    });
```

### Schedule Policy

Both registration methods accept an optional `SchedulePolicy` for overlap and catchup behavior:

```csharp
opts.AddScheduledAgentRun(
    agentName: "ReportAgent",
    scheduleId: "hourly-report",
    request: new RunRequest("Generate the hourly report."),
    spec: new ScheduleSpec
    {
        Intervals = [new ScheduleIntervalSpec(Every: TimeSpan.FromHours(1))]
    },
    policy: new SchedulePolicy
    {
        Overlap = ScheduleOverlapPolicy.Skip,    // skip if previous run still active
        CatchupWindow = TimeSpan.FromMinutes(10)  // catch up missed runs within 10 min
    });
```

---

## One-Time Deferred Runs

### From Inside a Workflow

Use `ScheduleActivities.ScheduleOneTimeAgentRunAsync` to schedule a future run without blocking the current workflow. This uses Temporal's `StartDelay` — a single workflow execution is created with a delayed start, leaving no persistent schedule entity behind:

```csharp
[Workflow]
public class ResearchWorkflow
{
    [WorkflowRun]
    public async Task RunAsync(string topic)
    {
        // Run analysis now
        var analyst = TemporalWorkflowExtensions.GetAgent("AnalystAgent");
        var session = await analyst.CreateSessionAsync();
        await analyst.RunAsync($"Analyze: {topic}", session);

        // Schedule a follow-up in 7 days
        await Workflow.ExecuteActivityAsync(
            (ScheduleActivities a) => a.ScheduleOneTimeAgentRunAsync(new OneTimeAgentRun
            {
                AgentName = "AnalystAgent",
                RunId     = $"followup-{topic}",
                Request   = new RunRequest($"Compare findings on '{topic}' against last week's."),
                RunAt     = Workflow.UtcNow + TimeSpan.FromDays(7)
            }),
            new ActivityOptions { StartToCloseTimeout = TimeSpan.FromSeconds(30) });
    }
}
```

**Idempotency:** If the activity retries after a crash-before-ack, `WorkflowIdConflictPolicy.UseExisting` ensures the second `StartWorkflowAsync` call finds the already-scheduled workflow and returns normally.

**Past `RunAt`:** If `RunAt` is in the past when the activity executes, the delay is clamped to zero and the run starts immediately.

### From an External Caller

`RunAgentDelayedAsync` defers the start of a **full `AgentWorkflow` session** — with conversation history and StateBag. Use this when you need a delayed session that can receive follow-up messages:

```csharp
ITemporalAgentClient client = // resolved from DI

var sessionId = new TemporalAgentSessionId("OnboardingAgent", userId);

// Workflow is created now but starts executing after 24 hours
await client.RunAgentDelayedAsync(
    sessionId,
    new RunRequest("Welcome! Your trial period has started."),
    delay: TimeSpan.FromHours(24));

// After the delay, you can send follow-up messages to the same session:
// await client.RunAgentAsync(sessionId, new RunRequest("How is your setup going?"));
```

> **Known limitation:** If a workflow with the same session ID is already running (`UseExisting` policy), `StartDelay` is ignored and the existing workflow is reused immediately. This method only applies the delay when starting a brand-new session.

---

## Schedule Lifecycle Management

The `ScheduleHandle` returned by `ScheduleAgentAsync` (or retrieved by `GetAgentScheduleHandle`) provides full lifecycle control:

```csharp
// Create a schedule
ScheduleHandle handle = await client.ScheduleAgentAsync(
    "ReportAgent", "weekly-report", request, spec);

// Trigger immediately (outside the normal cadence)
await handle.TriggerAsync();

// Pause and resume
await handle.PauseAsync(note: "Pausing during maintenance window.");
await handle.UnpauseAsync();

// Retrieve an existing handle by ID (e.g., from a different service)
ScheduleHandle existing = client.GetAgentScheduleHandle("weekly-report");

// Delete when decommissioning
await existing.DeleteAsync();
```

### Updating a Schedule's Spec

Temporal schedules are immutable once created via `ScheduleRegistrationService`. To update a schedule's spec:

1. Delete the existing schedule via the handle
2. Restart the worker (or call `ScheduleAgentAsync` again with the new spec)

```csharp
// Step 1: Delete the old schedule
var handle = client.GetAgentScheduleHandle("daily-summary");
await handle.DeleteAsync();

// Step 2: Create with updated spec (on next worker restart, or immediately)
await client.ScheduleAgentAsync(
    "SummaryAgent", "daily-summary", request, updatedSpec);
```

---

## Workflow ID Conventions

Scheduled and deferred runs use a distinct naming convention to avoid collisions with interactive sessions:

| Context | Workflow ID Format | Example |
|---------|-------------------|---------|
| Interactive session | `ta-{agent}-{key}` | `ta-weatheragent-abc123` |
| Scheduled/deferred run | `ta-{agent}-scheduled-{id}` | `ta-reportagent-scheduled-weekly-report` |

The `-scheduled-` infix ensures that a recurring schedule never accidentally targets an existing interactive session, and vice versa. Temporal appends a timestamp automatically for recurring schedules (e.g., `ta-reportagent-scheduled-weekly-report-2026-03-13T09:00:00Z`).

---

## Observability

Three OTel spans cover scheduling operations:

| Span | Emitted By | Key Attributes |
|------|-----------|---------------|
| `temporal.agent.schedule.create` | `ScheduleAgentAsync` | `agent.name`, `schedule.id` |
| `temporal.agent.schedule.delayed` | `RunAgentDelayedAsync` | `agent.name`, `agent.session_id`, `schedule.delay` |
| `temporal.agent.schedule.one_time` | `ScheduleOneTimeAgentRunAsync` | `agent.name`, `schedule.job_id`, `schedule.delay` |

Once the scheduled workflow executes, the standard `agent.turn` span fires inside `AgentActivities.ExecuteAgentAsync` — the same code path as interactive sessions. This means scheduled runs are fully visible in your tracing backend alongside interactive sessions.

For full OTel setup instructions, see [Observability](./observability.md).

---

## Pitfalls and Gotchas

### Schedule Orphaning

Temporal Schedules are **independent of workers**. Removing an agent from `TemporalAgentsOptions` does **not** delete its schedule — it will keep firing. The scheduled workflow will fail with `AgentNotRegisteredException` on each trigger.

**Always** delete the schedule before decommissioning an agent:

```csharp
var handle = client.GetAgentScheduleHandle("daily-summary");
await handle.DeleteAsync();
```

### Config Drift

If you change a schedule's spec in code (e.g., from daily to hourly), the change is **silently skipped** on restart — `ScheduleRegistrationService` catches the `ScheduleAlreadyRunningException` and logs a warning. The old spec remains active.

**Fix:** Delete the schedule first, then restart:

```bash
# Via Temporal CLI
temporal schedule delete --schedule-id daily-summary
```

Or programmatically via `GetAgentScheduleHandle("daily-summary").DeleteAsync()`.

### Delayed Session Reuse

`RunAgentDelayedAsync` uses `WorkflowIdConflictPolicy.UseExisting`. If the session workflow is already running, the delay is ignored. This is by design (you can't delay an already-running workflow), but it can be surprising if you expect the delay to apply unconditionally.

### Activity Timeouts for Scheduled Runs

`AgentJobWorkflow` inherits `ActivityTimeout` and `HeartbeatTimeout` from `TemporalAgentsOptions`. If your scheduled agent makes long-running tool calls, ensure the timeout is sufficient:

```csharp
opts.DefaultActivityTimeout = TimeSpan.FromMinutes(60);
```

This timeout applies to both interactive and scheduled runs. There is currently no per-schedule timeout override — the global option is used.

---

## Choosing the Right Primitive

**Is the schedule known at deploy time?**
- **Yes** → Use `AddScheduledAgentRun` for zero-code schedule management

**Does the schedule need to be created dynamically (e.g., user-triggered)?**
- **Yes, recurring** → Use `ScheduleAgentAsync`
- **Yes, one-time from outside a workflow** → Use `RunAgentDelayedAsync`
- **Yes, one-time from inside a workflow** → Use `ScheduleOneTimeAgentRunAsync`

**Does the scheduled run need conversation history?**
- **Yes** → Use `RunAgentDelayedAsync` (creates a full `AgentWorkflow` session)
- **No** → Use any of the other three (all use the stateless `AgentJobWorkflow`)

**Do you need to send follow-up messages after the delayed run?**
- **Yes** → Use `RunAgentDelayedAsync` — the session persists and accepts further messages
- **No** → Use `ScheduleOneTimeAgentRunAsync` or `AddScheduledAgentRun`

---

## References

- `src/Temporalio.Extensions.Agents/AgentJobWorkflow.cs` — fire-and-forget workflow for scheduled runs
- `src/Temporalio.Extensions.Agents/ScheduleActivities.cs` — one-time scheduling from inside workflows
- `src/Temporalio.Extensions.Agents/ScheduleRegistrationService.cs` — config-time schedule creation
- `src/Temporalio.Extensions.Agents/DefaultTemporalAgentClient.cs` — `ScheduleAgentAsync` and `RunAgentDelayedAsync`
- `src/Temporalio.Extensions.Agents/OneTimeAgentRun.cs` — input type for deferred runs
- [Usage Guide — Scheduling](./usage.md#scheduling) — quick-start examples
- [Observability](./observability.md) — scheduling OTel spans
- [Temporal Schedules Documentation](https://docs.temporal.io/workflows#schedule)

---

_Last updated: 2026-03-13_
