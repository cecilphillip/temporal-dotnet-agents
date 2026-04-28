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

## How to Run

### Prerequisites

- A local Temporal server: `temporal server start-dev`
- OpenAI API credentials via `dotnet user-secrets` (see below) or environment variable

### Configure API credentials

Set your API key with:

```bash
dotnet user-secrets set "OPENAI_API_KEY" "sk-..." --project samples/MAF/AmbientAgent
```

### Run

```bash
dotnet run --project samples/AmbientAgent/AmbientAgent.csproj
```

### Expected Output

The sample sends 20 health readings with a spike window at readings 13-15. You should see:
- 4 analysis passes (every 5 readings)
- An anomaly detection during the spike window
- An alert notification composed by the AlertAgent

## Key Design Decisions

1. **Custom workflows, not AgentWorkflow.** `AgentWorkflow` is designed for conversational sessions with history/HITL. The monitor needs signal-driven data ingestion + periodic batch analysis — a fundamentally different loop structure.

2. **`GetAgent()` inside workflows for LLM calls.** This is the standard sub-agent pattern. Each LLM call runs as a durable activity — crash-safe and automatically replayed on recovery.

3. **Cross-workflow signal via activity.** Since the Temporal .NET SDK doesn't have `Workflow.SignalExternalWorkflow`, we use the established pattern of an activity with `ITemporalClient`.

4. **Bounded buffer + continue-as-new.** Prevents workflow state from growing unboundedly. Essential for long-lived ambient agents that may run for days or weeks.

5. **Fixed workflow IDs + `UseExisting`.** Re-running the sample reuses existing workflows rather than creating duplicates — idempotent startup.
