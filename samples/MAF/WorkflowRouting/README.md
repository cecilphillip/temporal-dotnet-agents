# Workflow Routing: Customer Service Router

## Overview

This sample demonstrates **routing entirely inside a Temporal workflow** — no `IAgentRouter`, no `SetRouterAgent`, no `RouteAsync`. The workflow itself acts as the router with full programmatic control.

A `CustomerServiceWorkflow` receives a customer question and:

1. Calls a lightweight **Classifier** agent to determine the intent category (`ORDERS`, `TECH_SUPPORT`, or `GENERAL`)
2. Uses a `switch` expression to select the correct specialist agent
3. Calls the specialist and returns its response

```
User Question
    │
    ▼
CustomerServiceWorkflow
    │
    ├─ GetAgent("Classifier")  →  "ORDERS" / "TECH_SUPPORT" / "GENERAL"
    │
    ├─ switch (classification)
    │     "ORDERS"       → GetAgent("OrdersAgent")
    │     "TECH_SUPPORT" → GetAgent("TechSupportAgent")
    │     _              → GetAgent("GeneralAgent")
    │
    └─ Return specialist response
```

Every agent call runs as a durable Temporal activity. If the worker crashes after classification but before the specialist call, replay skips the classifier LLM call entirely — no duplicate work, no duplicate billing.

## How This Differs from MultiAgentRouting

| | MultiAgentRouting | WorkflowRouting (this sample) |
|---|---|---|
| **Routing mechanism** | `IAgentRouter` + `RouteAsync` (external) | `switch` expression inside the workflow |
| **Router registration** | `SetRouterAgent` + `AddAgentDescriptor` | `AddAIAgent` + optionally `AddAgentDescriptor` (for dynamic routing) |
| **Control flow** | Framework-driven (LLM picks agent name, framework dispatches) | Code-driven (you write the if/else/switch) |
| **Extensibility** | Add descriptors to influence LLM routing | Add any logic — fallback chains, confidence thresholds, multi-step classification |
| **Agent discovery** | Automatic from descriptors | Static (hardcoded) or dynamic (via activity + descriptors) |

## Getting Started

### Prerequisites

- [.NET 10 SDK](https://dot.net) or later
- A local Temporal server
- An OpenAI-compatible API key

### 1. Start Temporal

```bash
temporal server start-dev
```

### 2. Configure API credentials

Set your API key with:

```bash
dotnet user-secrets set "OPENAI_API_KEY" "sk-..." --project samples/MAF/WorkflowRouting
```

To use a non-default Temporal address, add it to `appsettings.json`:

```json
{
    "TEMPORAL_ADDRESS": "your-temporal-host:7233"
}
```

### 3. Run the sample

```bash
dotnet run --project samples/WorkflowRouting/WorkflowRouting.csproj
```

### Expected Output

Three workflows are submitted with different customer questions:

```
Worker started. Submitting customer service workflows...

Starting workflow cs-orders-<guid>

── Question: Where is my order #12345?
   Workflow: cs-orders-<guid>
   Response: <OrdersAgent response>

Starting workflow cs-tech-support-<guid>

── Question: My app keeps crashing on startup
   Workflow: cs-tech-support-<guid>
   Response: <TechSupportAgent response>

Starting workflow cs-general-<guid>

── Question: What services do you offer?
   Workflow: cs-general-<guid>
   Response: <GeneralAgent response>

── Dynamic Routing ─────────────────────────────────────

Starting dynamic workflow cs-dynamic-<guid>

── Question: I need to return a defective product
   Workflow: cs-dynamic-<guid>
   Response: <OrdersAgent response — discovered via descriptors>

Done.
```

Each question is classified by the Classifier agent, then routed to the appropriate specialist. You can inspect the workflow event history in the Temporal UI at [http://localhost:8233](http://localhost:8233) to see both the classification and specialist activities for each workflow.

## Agents

| Agent | Role | System Prompt Summary |
|-------|------|----------------------|
| **Classifier** | Intent classification | Returns exactly one keyword: `ORDERS`, `TECH_SUPPORT`, or `GENERAL` |
| **OrdersAgent** | Order specialist | Order tracking, returns, shipping, delivery estimates |
| **TechSupportAgent** | Tech specialist | Software issues, crashes, error messages, troubleshooting |
| **GeneralAgent** | Catch-all | Greetings, general inquiries, company information |

## Key Design Decisions

1. **Workflow as router.** All routing logic lives in `CustomerServiceWorkflow.RunAsync` as a plain `switch` expression. No framework abstractions to learn or configure.

2. **Graceful default.** Unrecognized classifications fall through to `GeneralAgent` via the `_` discard pattern, so unexpected LLM output doesn't crash the workflow.

3. **Auto-extracted descriptions.** Specialist agents carry `description:` in their `AsAIAgent()` calls, and `AddAIAgent()` auto-extracts these into the descriptor registry. No explicit `AddAgentDescriptor()` needed — the dynamic routing workflow discovers agents via the same descriptors. No `IAgentRouter` is created.

4. **Independent sessions per agent.** Each agent call gets its own session (`CreateSessionAsync`), keeping conversation histories isolated between the classifier and specialist.

## Dynamic Routing via Activity

The sample also includes `DynamicRoutingWorkflow`, which demonstrates **truly dynamic agent discovery** — the workflow has zero hardcoded agent names in its routing logic.

### The Problem

`CustomerServiceWorkflow` uses hardcoded agent names in a `switch` expression. If you add a new agent or rename one, you must recompile. And calling `TemporalAgentsOptions.GetRegisteredAgentNames()` directly in workflow code is **unsafe**:

- Workflow code must produce identical results during replay
- If the agent set changes between the original execution and a replay, the routing decision would differ
- This breaks Temporal's determinism guarantee — the same reason `DateTime.UtcNow` is forbidden in workflows

### The Safe Pattern: Descriptors + Activity

`AddAgentDescriptor()` is typically associated with `IAgentRouter`, but it's also a general-purpose agent metadata store. `DynamicRoutingWorkflow` uses descriptors *without* `SetRouterAgent`:

1. **An activity** calls `options.GetRegisteredDescriptors()` to discover available agents and their descriptions — the result is cached in workflow event history
2. **The Classifier agent** receives the descriptor list as context and picks the best match from whatever agents are currently registered
3. **A validation activity** confirms the LLM's choice is actually registered, with a fallback

```
DynamicRoutingWorkflow
    │
    ├─ Activity: GetAvailableAgents()
    │    └─ reads AddAgentDescriptor() registrations from TemporalAgentsOptions
    │    └─ returns: [("OrdersAgent", "Handles orders..."), ("TechSupportAgent", "..."), ...]
    │    └─ result cached in event history (replay-safe)
    │
    ├─ GetAgent("Classifier") with dynamic prompt:
    │    "Available agents:\n  OrdersAgent — Handles orders...\n  ..."
    │    └─ LLM picks: "OrdersAgent"
    │
    ├─ Activity: ValidateAgent("OrdersAgent", fallback: "GeneralAgent")
    │    └─ confirms agent exists in registry
    │    └─ result cached in event history (replay-safe)
    │
    └─ GetAgent("OrdersAgent") → specialist response
```

### Why This is Truly Dynamic

- **No hardcoded agent names** in the routing workflow — it discovers what's available at runtime
- **Add a new agent** via `AddAIAgent` + `AddAgentDescriptor` and it's automatically picked up
- **Remove an agent** and the validation activity falls back gracefully
- **The Classifier adapts** — its prompt is built from live descriptors, not a static enum
- **Replay-safe** — both activity results (descriptor list + validation) are recorded in history

### Auto-Extracted Descriptions

When agents carry a `description:` parameter in their `AsAIAgent()` call, `AddAIAgent()` automatically populates the descriptor registry — no separate `AddAgentDescriptor()` needed. The descriptors are stored in `TemporalAgentsOptions` and exposed via `options.GetRegisteredDescriptors()`. This sample uses them as a metadata source without creating an `IAgentRouter` at all.

For factory-registered agents (`AddAIAgentFactory`), use `AddAgentDescriptor()` explicitly since there's no agent instance available at registration time.

### Files

| File | Purpose |
|------|---------|
| `RoutingActivities.cs` | Activities that query `TemporalAgentsOptions` — `GetAvailableAgents()` returns descriptors, `ValidateAgent()` confirms a name exists |
| `DynamicRoutingWorkflow.cs` | Workflow that discovers agents via activity, builds a dynamic classifier prompt, validates the choice, and dispatches |
