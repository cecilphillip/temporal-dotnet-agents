# Observability

How to instrument, trace, and query TemporalAgents workloads — from OpenTelemetry span setup to search attribute queries in the Temporal UI.

---

## Table of Contents

1. [Overview](#overview)
2. [Setup](#setup)
3. [Span Reference](#span-reference)
4. [Attribute Reference](#attribute-reference)
5. [Full Span Hierarchy](#full-span-hierarchy)
6. [Search Attributes](#search-attributes)
7. [Correlating Across Continue-as-New](#correlating-across-continue-as-new)
8. [Operational Patterns](#operational-patterns)

---

## Overview

TemporalAgents emits two layers of distributed tracing spans:

1. **Agent spans** — emitted by `TemporalAgentTelemetry.ActivitySource` (`"Temporalio.Extensions.Agents"`) to capture agent-semantic events like "agent turn" and "client send"
2. **Temporal SDK spans** — emitted by the `TracingInterceptor` from `Temporalio.Extensions.OpenTelemetry` to capture protocol-level events like `StartWorkflow` and `RunActivity`

These two layers compose naturally: agent spans nest inside (or wrap) Temporal SDK spans, giving you a single trace that spans the full lifecycle of a request — from the external caller through the workflow down to the LLM inference.

Additionally, when `EnableSearchAttributes = true` in `TemporalAgentsOptions`, `AgentWorkflow` upserts **search attributes** on each workflow run, enabling operational queries in the Temporal Web UI and via the `ListWorkflowsAsync` API.

---

## Setup

Install the required packages:

```bash
dotnet add package Temporalio.Extensions.OpenTelemetry
dotnet add package OpenTelemetry.Exporter.OpenTelemetryProtocol  # or your preferred exporter
```

Register **all four** `ActivitySource` names plus the `TracingInterceptor`:

```csharp
using OpenTelemetry.Trace;
using Temporalio.Extensions.OpenTelemetry;
using Temporalio.Extensions.Agents;

// 1. Configure the OTel tracer provider with all relevant sources
using var tracerProvider = Sdk.CreateTracerProviderBuilder()
    .AddSource(
        TracingInterceptor.ClientSource.Name,      // Temporal client spans
        TracingInterceptor.WorkflowsSource.Name,   // Temporal workflow spans
        TracingInterceptor.ActivitiesSource.Name,  // Temporal activity spans
        TemporalAgentTelemetry.ActivitySourceName) // "Temporalio.Extensions.Agents"
    .AddOtlpExporter()
    .Build();

// 2. Add the tracing interceptor to the Temporal client
builder.Services.AddTemporalClient(opts =>
{
    opts.TargetHost = "localhost:7233";
    opts.Interceptors = [new TracingInterceptor()];
});

// 3. Register agents as usual
builder.Services
    .AddHostedTemporalWorker("agents")
    .AddTemporalAgents(opts => opts.AddAIAgent(agent));
```

> **Missing spans?** The most common cause is forgetting one of the four `AddSource` calls. All four are required for the complete trace hierarchy.

---

## Span Reference

### `agent.client.send` (Client kind)

**Emitted by:** `DefaultTemporalAgentClient.RunAgentAsync`

Wraps the full round-trip of sending an update to `AgentWorkflow` — from the external caller through `StartWorkflowAsync` + `ExecuteUpdateAsync` back to the response.

| Attribute | Value |
|-----------|-------|
| `agent.name` | The registered agent name |
| `agent.session_id` | The Temporal workflow ID (`ta-{name}-{key}`) |

**Error handling:** If the update fails, `span.SetStatus(ActivityStatusCode.Error, ex.Message)` is called.

### `agent.turn` (Internal kind)

**Emitted by:** `AgentActivities.ExecuteAgentAsync`

Wraps a single agent inference turn — the actual LLM call inside the activity. This is where token usage metrics are captured.

| Attribute | Value |
|-----------|-------|
| `agent.name` | The registered agent name |
| `agent.session_id` | The Temporal workflow ID |
| `agent.correlation_id` | Links the request to its response (from `RunRequest.CorrelationId`) |
| `agent.input_tokens` | Prompt tokens consumed (from `AgentResponse.Usage`) |
| `agent.output_tokens` | Completion tokens produced |
| `agent.total_tokens` | Input + output tokens |

Token attributes are only set when the underlying LLM provider reports usage data.

### `temporal.agent.schedule.create` (Client kind)

**Emitted by:** `DefaultTemporalAgentClient.ScheduleAgentAsync`

Wraps the creation of a recurring Temporal Schedule.

| Attribute | Value |
|-----------|-------|
| `agent.name` | The agent being scheduled |
| `schedule.id` | The Temporal schedule ID |

### `temporal.agent.schedule.delayed` (Client kind)

**Emitted by:** `DefaultTemporalAgentClient.RunAgentDelayedAsync`

Wraps the creation of a delayed one-time agent session via `StartDelay`.

| Attribute | Value |
|-----------|-------|
| `agent.name` | The agent being scheduled |
| `agent.session_id` | The Temporal workflow ID |
| `schedule.delay` | The delay as `TimeSpan.ToString()` |

### `temporal.agent.schedule.one_time` (Internal kind)

**Emitted by:** `ScheduleActivities.ScheduleOneTimeAgentRunAsync`

Wraps a one-time scheduled run started from within a workflow via an activity.

| Attribute | Value |
|-----------|-------|
| `agent.name` | The agent being scheduled |
| `schedule.job_id` | The run ID of the one-time job |
| `schedule.delay` | The delay before execution |

---

## Attribute Reference

All attributes are defined as constants on `TemporalAgentTelemetry`:

| Constant | Attribute Name | Type | Used In |
|----------|---------------|------|---------|
| `AgentNameAttribute` | `agent.name` | string | All spans |
| `AgentSessionIdAttribute` | `agent.session_id` | string | `client.send`, `turn`, `schedule.delayed` |
| `AgentCorrelationIdAttribute` | `agent.correlation_id` | string | `turn` |
| `InputTokensAttribute` | `agent.input_tokens` | int? | `turn` |
| `OutputTokensAttribute` | `agent.output_tokens` | int? | `turn` |
| `TotalTokensAttribute` | `agent.total_tokens` | int? | `turn` |
| `ScheduleIdAttribute` | `schedule.id` | string | `schedule.create` |
| `ScheduleDelayAttribute` | `schedule.delay` | string | `schedule.delayed`, `schedule.one_time` |
| `ScheduleJobIdAttribute` | `schedule.job_id` | string | `schedule.one_time` |

---

## Full Span Hierarchy

A single `RunAsync` call from an external caller produces this trace:

```
agent.client.send                           ← TemporalAgentTelemetry (Client kind)
  │
  ├── StartWorkflow:AgentWorkflow           ← TracingInterceptor.ClientSource
  │
  └── UpdateWorkflow:RunAgent               ← TracingInterceptor.ClientSource
        │
        └── HandleUpdate:RunAgent           ← TracingInterceptor.WorkflowsSource
              │
              └── RunActivity:ExecuteAgent  ← TracingInterceptor.ActivitiesSource
                    │
                    └── agent.turn          ← TemporalAgentTelemetry (Internal kind)
                          │
                          └── (LLM HTTP call, if instrumented by the HTTP client)
```

The two `TemporalAgentTelemetry` spans bookend the trace — `agent.client.send` at the top (caller-side) and `agent.turn` at the bottom (inference-side). The Temporal SDK spans fill in the middle, showing the workflow and activity execution.

---

## Search Attributes

Search attribute upserts are **opt-in**. Set `EnableSearchAttributes = true` in your `TemporalAgentsOptions` to enable them:

```csharp
builder.Services
    .AddHostedTemporalWorker("localhost:7233", "default", "agents")
    .AddTemporalAgents(opts =>
    {
        opts.AddDurableAgent("Agent", a => a.ChatClient = sp => sp.GetRequiredService<IChatClient>());
        opts.EnableSearchAttributes = true;
    });
```

When enabled, `AgentWorkflow` upserts three [custom search attributes](https://docs.temporal.io/visibility#custom-search-attributes) on each workflow:

| Attribute | Type | When Updated |
|-----------|------|-------------|
| `AgentName` | Keyword | On workflow start |
| `SessionCreatedAt` | DateTimeOffset | On workflow start |
| `TurnCount` | Long | After each completed agent response |

### Registration for Production Clusters

With `temporal server start-dev`, search attributes are created automatically. For production clusters, register them via the CLI:

```bash
temporal operator search-attribute create --name AgentName --type Keyword
temporal operator search-attribute create --name SessionCreatedAt --type Datetime
temporal operator search-attribute create --name TurnCount --type Int
```

### Example Queries in the Temporal UI

```
AgentName = "BillingAgent" AND TurnCount > 10
```

```
SessionCreatedAt > "2026-03-01T00:00:00Z"
```

```
AgentName = "WeatherAgent" AND ExecutionStatus = "Running"
```

### Querying via ListWorkflowsAsync

```csharp
var result = client.ListWorkflowsAsync(
    "AgentName = 'BillingAgent' AND TurnCount > 5");

await foreach (var execution in result)
{
    Console.WriteLine($"{execution.Id} — turns: {execution.SearchAttributes["TurnCount"]}");
}
```

---

## Correlating Across Continue-as-New

When `AgentWorkflow` triggers continue-as-new, the Temporal workflow ID stays the same but the run ID changes. Two mechanisms help correlate spans across these boundaries:

1. **Session ID (`agent.session_id`)** — remains constant across continue-as-new transitions since it is the workflow ID
2. **Correlation ID (`agent.correlation_id`)** — set per-request on `RunRequest.CorrelationId`, allowing you to trace a single request across the boundary

To find all spans for a session regardless of which run they belong to, filter by `agent.session_id`. To trace a single request, use `agent.correlation_id`.

---

## Operational Patterns

### Finding Expensive Agents by Token Count

Filter `agent.turn` spans by `agent.total_tokens` in your tracing backend:

```
service.name = "my-agent-service"
AND name = "agent.turn"
AND agent.total_tokens > 10000
```

This surfaces turns where the LLM consumed an unusually high number of tokens — useful for identifying agents that need prompt optimization or context trimming.

### Monitoring Scheduling Spans

The three scheduling spans (`temporal.agent.schedule.create`, `temporal.agent.schedule.delayed`, `temporal.agent.schedule.one_time`) help monitor the health of scheduled agent runs. Errors on these spans indicate schedule creation failures — check for missing agents, invalid schedule specs, or Temporal server connectivity issues.

### Error Detection via Span Status

All agent spans set `ActivityStatusCode.Error` on failure with the exception message. Set up alerts in your tracing backend for:

```
status = ERROR AND name = "agent.client.send"
```

This catches agent invocation failures at the outermost layer — including workflow start failures, update rejections, and downstream agent errors.

### Latency Breakdown

Compare `agent.client.send` duration against `agent.turn` duration. The difference is the Temporal overhead (workflow scheduling, activity dispatch, serialization). In healthy systems this overhead is typically < 100ms; significantly higher values may indicate Temporal server pressure or large payload serialization.

### Per-LLM-Call Visibility

The `agent.turn` span captures the whole turn — one LLM call plus any tool rounds the model triggers. To go finer and see each individual LLM round (request, response, token usage, finish reason), wrap the inner `IChatClient` with a `DelegatingChatClient` decorator and register it via the `clientFactory` parameter of `AsAIAgent(...)`.

This is a doc-only pattern with no library opt-in flag. See [Per-LLM-Call Interception via `ChatClientFactory`](./llm-call-interception.md) for the full guide. It is the answer to "I want more visibility into when agents call the model and execute tools" — and it composes cleanly with the rest of the OTel setup described above.

---

## References

- `src/Temporalio.Extensions.Agents/TemporalAgentTelemetry.cs` — all span and attribute constants
- `src/Temporalio.Extensions.Agents/DefaultTemporalAgentClient.cs` — `agent.client.send` and scheduling spans
- `src/Temporalio.Extensions.Agents/AgentActivities.cs` — `agent.turn` span with token metrics
- `src/Temporalio.Extensions.Agents/AgentWorkflow.cs` — search attribute upserts
- `samples/MultiAgentRouting/Program.cs` — complete OTel setup example
- [Temporal Visibility](https://docs.temporal.io/visibility) — search attribute documentation
- [Temporalio.Extensions.OpenTelemetry](https://github.com/temporalio/sdk-dotnet) — SDK tracing interceptor

---

_Last updated: 2026-03-13_
