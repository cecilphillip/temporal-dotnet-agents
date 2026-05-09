# Configurable Agent: Two-Tier Customer Support

## Overview

A two-tier customer support system that shows how to bind agent instructions from `appsettings.json` and resolve tool services from the worker's DI container at activity dispatch time.

`TriageAgent` handles incoming customer messages, using a `LookupOrder` tool for common order questions. When it cannot resolve a case, it embeds `[ESCALATE: summary]` in its reply. `SupportWorkflow` detects the marker, extracts the one-sentence summary, and hands off to `EscalationAgent` — which has access to the return policy and is instructed to handle complaints and refunds. Both agent calls are durable Temporal activities.

## Architecture

```
Client
  │
  │  StartWorkflow(SupportWorkflow, customerMessage)
  ▼
SupportWorkflow ([Workflow])
  │
  ├─ GetAgent("TriageAgent")
  │    ├─ RunAsync(customerMessage)           ← RunDurableAgentStep activity
  │    │    └─ LookupOrder tool              ← InvokeAgentTool activity
  │    └─ response contains [ESCALATE: ...]?
  │
  ├─── No  → return responseText             (resolved by triage)
  │
  └─── Yes → extract caseSummary
               │
               ├─ GetAgent("EscalationAgent")
               │    └─ RunAsync(caseSummary + original message)
               │         ├─ RunDurableAgentStep activity
               │         └─ GetReturnPolicy tool   ← InvokeAgentTool activity
               └─ return escalation response
```

## Highlights

- **DI-factory-per-slot pattern.** `agent.AddTool("Name", sp => ...)` resolves `OrderService` and `EscalationPolicyService` from the worker's `IServiceProvider` at first activity dispatch. No `BuildServiceProvider()` bootstrap is needed — the library calls each factory once and caches the result for the worker's lifetime.

- **Configuration-driven instructions.** Agent instruction templates are bound from `appsettings.json` via `IConfiguration.GetSection(...).Get<T>()`. The `{CompanyName}` placeholder is substituted at startup using `string.Replace`, so each deployment can use different company names or instruction text without changing code.

- **Agent-to-agent handoff via the workflow.** `SupportWorkflow` is the orchestrator. It runs `TriageAgent`, inspects the response for the escalation marker, and conditionally invokes `EscalationAgent` — passing both the customer's original message and the triage summary. This is the correct pattern: routing decisions belong inside a durable workflow, not inside the agent itself. For production, prefer structured output (a typed model response) over free-text marker detection so routing decisions are unambiguous.

- **Crash safety between triage and escalation.** Each `RunAsync` call dispatches a `RunDurableAgentStep` activity. If the worker crashes after `TriageAgent` completes but before `EscalationAgent` starts, the workflow replays from history — `TriageAgent` is not re-executed and its LLM result is served from cached state.

- **Domain services belong in DI.** `OrderService` and `EscalationPolicyService` are registered as singletons and injected via the tool factory. In a real application these would call a database or external API — the DI factory pattern makes swapping or mocking them straightforward without touching agent registration code.

## Getting Started

### Prerequisites

- [.NET 10 SDK](https://dot.net) or later
- A local Temporal server: `temporal server start-dev`
- An OpenAI-compatible API key

### Configure API credentials

`OPENAI_API_BASE_URL` defaults to `https://api.openai.com/v1` in `appsettings.json`. Override it with user-secrets if you use a different endpoint.

```bash
dotnet user-secrets set "OPENAI_API_KEY" "sk-..." --project samples/MAF/ConfigurableAgent
dotnet user-secrets set "OPENAI_API_BASE_URL" "https://api.openai.com/v1" --project samples/MAF/ConfigurableAgent
```

### Run

```bash
dotnet run --project samples/MAF/ConfigurableAgent/ConfigurableAgent.csproj
```

## Expected Output

The sample submits two workflows concurrently and prints each result when it completes.

**Case 1** — a simple order-status question. `TriageAgent` calls `LookupOrder("ORD-001")`, gets the status, and replies directly. No escalation marker is emitted.

**Case 2** — a delayed order with a refund request. `TriageAgent` finds the carrier exception status for `ORD-004` but cannot process the refund, so it appends `[ESCALATE: ...]`. The workflow hands off to `EscalationAgent`, which calls `GetReturnPolicy` and composes a resolution.

```
Worker started. Submitting support workflows...

─── Case 1: Simple order lookup ─────────────────────────────
User: Where is my order ORD-001?
Agent: Your order ORD-001 has shipped and is estimated to arrive in 2 days.
       Is there anything else I can help you with?

─── Case 2: Delayed order + refund request ──────────────────
User: My order ORD-004 has been delayed for two weeks. I want a refund.
Agent: I'm sorry to hear about the delay with your order ORD-004. Our return
       policy allows refunds for delayed or damaged orders, and we can process
       a full refund within 5–7 business days. I'll initiate that for you now.
       Please allow a few days for the refund to appear on your statement.

Done.
```

LLM response text varies per run; the structure and section headers are fixed.
