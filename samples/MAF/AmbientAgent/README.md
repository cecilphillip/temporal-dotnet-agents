# Ambient Agent: System Health Monitor

## Overview

An **ambient agent** is an AI system that operates continuously in the background, monitoring data streams and triggering actions without direct user prompts. Unlike conversational agents that respond to user queries, ambient agents proactively observe, analyze, and act.

This sample demonstrates a system health monitor that:
- Ingests simulated system metrics (CPU, memory, temperature) via Temporal signals
- Periodically calls an LLM **AnalysisAgent** to assess trends
- Proactively signals a separate **AlertAgent** workflow when anomalies are detected
- Maintains a bounded rolling buffer in workflow state
- Supports continue-as-new for indefinite monitoring

## Architecture

```
External System (simulated in Program.cs)
    │
    │  [WorkflowSignal("IngestHealthCheck")]
    ▼
MonitorWorkflow (custom [Workflow])
    ├─ _buffer: bounded rolling window of HealthCheckData
    ├─ Every N readings → GetAgent("AnalysisAgent") via activity
    ├─ If response contains "ANOMALY" → signal AlertWorkflow via activity
    ├─ [WorkflowQuery("GetStatus")] for external monitoring
    └─ Continue-as-new when history grows (carries buffer forward)

AlertWorkflow (custom [Workflow])
    ├─ [WorkflowSignal("IngestAnomaly")] receives AnomalyAlert
    ├─ GetAgent("AlertAgent") via activity → compose notification
    ├─ Stores notifications for query inspection
    └─ [WorkflowQuery("GetNotifications")] for inspection
```

## Communication Patterns

This sample showcases three Temporal communication primitives:

### 1. Signals (fire-and-forget ingestion)

External systems push `HealthCheckData` into `MonitorWorkflow` via `[WorkflowSignal("IngestHealthCheck")]`. This is non-blocking — the sender doesn't wait for a response. Ideal for ambient data streams where the producer shouldn't be coupled to the consumer's processing speed.

### 2. Cross-workflow signaling via activities

When `MonitorWorkflow` detects an anomaly, it signals `AlertWorkflow` through `AlertActivities.SignalAlertWorkflowAsync`. This goes through an activity with `ITemporalClient` because the Temporal .NET SDK doesn't expose direct `Workflow.SignalExternalWorkflow`. This is the established pattern from `TemporalAgentContext.SignalWorkflowAsync` in the library.

### 3. Queries (read-only observation)

External code inspects workflow state via `[WorkflowQuery]` — e.g., `GetStatus()` on the monitor, `GetNotifications()` on the alert workflow. Queries are non-blocking and don't affect workflow execution, making them safe for dashboards and health checks.

## Highlights

- **Custom workflows, not AgentWorkflow.** `AgentWorkflow` is designed for conversational sessions with history/HITL. The monitor needs signal-driven data ingestion + periodic batch analysis — a fundamentally different loop structure.

- **`GetAgent()` inside workflows for LLM calls.** This is the standard sub-agent pattern. Each LLM call runs as a durable activity — crash-safe and automatically replayed on recovery.

- **Cross-workflow signal via activity.** Since the Temporal .NET SDK doesn't have `Workflow.SignalExternalWorkflow`, we use the established pattern of an activity with `ITemporalClient`.

- **Bounded buffer + continue-as-new.** Prevents workflow state from growing unboundedly. Essential for long-lived ambient agents that may run for days or weeks.

- **Fixed workflow IDs + `UseExisting`.** Re-running the sample reuses existing workflows rather than creating duplicates — idempotent startup.

## Getting Started

### Prerequisites

- [.NET 10 SDK](https://dot.net) or later
- A local Temporal server: `temporal server start-dev`
- An OpenAI-compatible API key

### Configure API credentials

```bash
dotnet user-secrets set "OPENAI_API_KEY" "sk-..." --project samples/MAF/AmbientAgent
dotnet user-secrets set "OPENAI_API_BASE_URL" "https://api.openai.com/v1" --project samples/MAF/AmbientAgent
```

### Run

```bash
dotnet run --project samples/MAF/AmbientAgent/AmbientAgent.csproj
```

### Expected Output

The logging level is set to `Warning` in `Program.cs`, so workflow-internal log lines (`Workflow.Logger.LogInformation`) are suppressed. Only the `Console.WriteLine` calls in `Program.cs` are visible on the console.

The sample sends 20 health readings with a spike at readings 13–15. There are 4 analysis passes (every 5 readings), an anomaly detected during the spike window, and an alert notification composed by the AlertAgent. The exact metric values and LLM response text vary per run; the structure and section headers are fixed:

```
Worker started. Launching ambient agent workflows...

AlertWorkflow started: ambient-alert-001
MonitorWorkflow started: ambient-monitor-001

── Sending simulated health readings ───────────────────────
  Reading  1: CPU= 33.2% Mem= 51.3% Temp= 56.8°C
  Reading  2: CPU= 52.1% Mem= 47.6% Temp= 61.2°C
  ...
  Reading 13: CPU= 97.8% Mem= 98.3% Temp= 91.4°C ⚠️ SPIKE
  Reading 14: CPU= 96.1% Mem= 97.0% Temp= 89.7°C ⚠️ SPIKE
  Reading 15: CPU= 99.2% Mem= 99.5% Temp= 93.1°C ⚠️ SPIKE
  ...
  Reading 20: CPU= 41.5% Mem= 58.9% Temp= 52.3°C

Waiting for LLM analyses to complete...

── Monitor Status ──────────────────────────────────────────
  Buffer size: 20
  Total readings: 20
  Analyses performed: 4

  Analysis: NORMAL: CPU and memory usage are within acceptable ranges...

  Analysis: NORMAL: All metrics stable...

  Analysis: ANOMALY: CPU spiked to 95–100% with sustained high temperature...

  Analysis: NORMAL: Metrics have returned to normal ranges...

── Alert Notifications ─────────────────────────────────────

  URGENT - System Anomaly Detected
  Severity: HIGH ...

── Shutting down ───────────────────────────────────────────
  Shutdown signals sent.
Done.
```
