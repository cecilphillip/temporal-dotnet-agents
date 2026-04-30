# Multi-Agent Routing: Sequential and Parallel Dispatch

## Overview

Demonstrates two complementary patterns for dispatching to multiple agents: sequential routing (classify then dispatch to one specialist) and parallel fan-out (send the same query to all agents simultaneously). Both patterns include OpenTelemetry tracing wired through all four Temporal + agent span sources.

This sample demonstrates:
- `RoutingWorkflow`: keyword-based classification via a `RoutingActivities.ClassifyRequest` activity, then dispatch to one of three specialists
- `ParallelAgentWorkflow`: fan-out to all three specialists at once via `ExecuteAgentsInParallelAsync`
- OTel tracing with `TracingInterceptor` and `TemporalAgentTelemetry` span sources
- Routing decision as a durable, auditable activity result

## Architecture

```
User Question
    │
    ▼
RoutingWorkflow                         ParallelAgentWorkflow
    │                                       │
    ├─ Activity: ClassifyRequest()          ├─ GetAgent("WeatherAgent")    ─┐
    │    └─ returns: "WeatherAgent"         ├─ GetAgent("BillingAgent")    ─┼─ ExecuteAgentsInParallelAsync
    │                                       └─ GetAgent("TechSupportAgent") ┘
    └─ GetAgent(agentName).RunAsync()              │
         └─ specialist response               IReadOnlyList<AgentResponse>
```

## OTel Span Hierarchy

```
agent.client.send          (TemporalAgentTelemetry — agent name, session ID)
  UpdateWorkflow:RunAgent  (TracingInterceptor — workflow update span)
    RunActivity:ExecuteAgent  (TracingInterceptor — activity span)
      agent.turn            (TemporalAgentTelemetry — token counts, correlation ID)
```

All four sources must be registered for the full trace to appear:

```csharp
Sdk.CreateTracerProviderBuilder()
    .AddSource(TracingInterceptor.ClientSource.Name)
    .AddSource(TracingInterceptor.WorkflowsSource.Name)
    .AddSource(TracingInterceptor.ActivitiesSource.Name)
    .AddSource(TemporalAgentTelemetry.ActivitySourceName)
    .AddConsoleExporter()
    .Build();
```

## Highlights

- **Routing decision as activity.** `ClassifyRequest` runs in a `RoutingActivities` activity, not inline in the workflow. The result is cached in event history — a crash after classification won't re-invoke the classifier, and the decision is visible in the Temporal Web UI.
- **`ExecuteAgentsInParallelAsync` for fan-out.** The workflow-safe equivalent of `Task.WhenAll` — dispatches multiple agent activities concurrently and returns results in input order.
- **`TracingInterceptor` propagates context.** Registered on `ITemporalClient` via `opts.Interceptors`, it propagates OTel trace context across Temporal's RPC boundary so spans from client to workflow to activity form a single trace.
- **Three registered specialists.** `WeatherAgent`, `BillingAgent`, and `TechSupportAgent` are all added via `AddTemporalAgents()` on the same worker, demonstrating multi-agent registration.

## Getting Started

### Prerequisites

- [.NET 10 SDK](https://dot.net) or later
- A local Temporal server: `temporal server start-dev`
- An OpenAI-compatible API key

### Configure API credentials

```bash
dotnet user-secrets set "OPENAI_API_KEY" "sk-..." --project samples/MAF/MultiAgentRouting
dotnet user-secrets set "OPENAI_API_BASE_URL" "https://api.openai.com/v1" --project samples/MAF/MultiAgentRouting
```

### Run

```bash
dotnet run --project samples/MAF/MultiAgentRouting/MultiAgentRouting.csproj
```

### Expected Output

```
Worker started.

── Demonstrating workflow-based routing ────────────────────

User: Will it rain in Seattle tomorrow?
Agent: <WeatherAgent response>

User: Why was I charged twice on my last invoice?
Agent: <BillingAgent response>

User: My application keeps crashing with a null reference exception.
Agent: <TechSupportAgent response>

── Demonstrating parallel agent execution ──────────────────

Fan-out query (sent to all 3 agents simultaneously): "Briefly introduce yourself..."

Parallel responses:

[WeatherAgent]: I'm a weather specialist...
[BillingAgent]: I handle billing and payments...
[TechSupportAgent]: I provide technical support...

Done.
```
