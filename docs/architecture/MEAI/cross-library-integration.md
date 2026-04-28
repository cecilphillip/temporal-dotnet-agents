# Cross-Library Integration: Extensions.AI and Extensions.Agents

This document describes the structural relationship between `Temporalio.Extensions.AI` and `Temporalio.Extensions.Agents` — specifically what is shared, what is not, and why the boundary is drawn where it is.

---

## The Two-Library Structure

`Temporalio.Extensions.AI` is the lower-level primitive. It makes any `IChatClient` (Microsoft.Extensions.AI / MEAI) durable using Temporal workflows. It has no dependency on the Microsoft Agent Framework and carries minimal abstractions: a workflow, a set of activities, a session client, and the HITL types.

`Temporalio.Extensions.Agents` builds on top of `Extensions.AI`. It adds the full Microsoft Agent Framework model — `AIAgent`, `ChatClientAgent`, `AgentSessionStateBag`, multi-agent orchestration, LLM-powered routing, and scheduled runs — all backed by its own workflow (`AgentWorkflow`) and activity (`AgentActivities`). It takes a NuGet dependency on `Temporalio.Extensions.AI`, pulling in the shared types described below.

---

## Dependency Direction

The dependency is strictly one-way:

```
Temporalio.Extensions.Agents
        │
        │  depends on
        ▼
Temporalio.Extensions.AI
```

`Extensions.AI` has no knowledge of `Extensions.Agents`. This keeps the lower-level library lightweight and usable independently. Adding `Temporalio.Extensions.Agents` to a project automatically pulls in `Temporalio.Extensions.AI` — no separate `<PackageReference>` is needed.

---

## What Crosses the Boundary

Two types cross the boundary between the libraries: `DurableApprovalRequest` and `DurableApprovalDecision`. Both are defined in `Temporalio.Extensions.AI` and used by `Temporalio.Extensions.Agents`.

```csharp
// Namespace: Temporalio.Extensions.AI
// Used by both DurableChatWorkflow (Extensions.AI) and AgentWorkflow (Extensions.Agents)
public sealed record DurableApprovalRequest
{
    public required string RequestId { get; init; }
    public string? FunctionName { get; init; }   // populated by DurableAIFunction; null in MAF flows
    public string? CallId { get; init; }          // populated by DurableAIFunction; null in MAF flows
    public string? Description { get; init; }
}

public sealed record DurableApprovalDecision
{
    public string RequestId { get; init; } = string.Empty;
    public bool Approved { get; init; }
    public string? Reason { get; init; }
}
```

These types are the **shared wire protocol** for approval flows. An external system — an admin dashboard, a monitoring service, an approval API — that can handle `DurableApprovalRequest` from `DurableChatWorkflow` can handle the same type from `AgentWorkflow` without any modification.

---

## DurableAIDataConverter Consideration

`DurableAIDataConverter.Instance` must be applied to the Temporal client whenever MEAI types flow through workflow history. Because `Extensions.Agents` depends on `Extensions.AI`, both libraries require the same converter.

In the most common registration patterns, the converter is applied automatically:

| Registration pattern | Auto-wired by |
|---|---|
| `AddTemporalClient(addr, ns)` + `AddDurableAI()` | `Extensions.AI` — via `IConfigureOptions<TemporalClientConnectOptions>` |
| `AddHostedTemporalWorker(addr, ns, queue)` + `AddDurableAI()` | `Extensions.AI` — via `IPostConfigureOptions<TemporalWorkerServiceOptions>` |
| `AddHostedTemporalWorker(addr, ns, queue)` + `AddTemporalAgents()` | `Extensions.Agents` — same mechanism, applied during worker registration |
| Manual `TemporalClient.ConnectAsync` + `AddSingleton<ITemporalClient>` | Not auto-wired — set `DataConverter = DurableAIDataConverter.Instance` explicitly |

When running both libraries on the same worker, the auto-wiring registrations use `TryAddEnumerable`, so calling both `AddDurableAI()` and `AddTemporalAgents()` does not register the configurators twice.

---

## The HITL State Machine

Both `DurableChatWorkflow` and `AgentWorkflow` implement the same approval state machine using the same shared types. The protocol is identical:

```
Tool / Activity
    │
    │  RequestApprovalAsync [WorkflowUpdate]
    │  sends DurableApprovalRequest
    ▼
Workflow (DurableChatWorkflow or AgentWorkflow)
    │  stores _pendingApproval
    │  blocks on WaitConditionAsync
    │
    ▼
External System
    │  polls GetPendingApproval [WorkflowQuery] → DurableApprovalRequest
    │  calls SubmitApprovalAsync [WorkflowUpdate] with DurableApprovalDecision
    ▼
Workflow
    │  sets _approvalDecision
    │  WaitConditionAsync unblocks
    │  returns DurableApprovalDecision to the tool
    ▼
Tool / Activity
    │  proceeds or cancels based on Approved flag
```

The external system does not need to know which workflow type it is talking to. The same `GetPendingApprovalAsync` / `SubmitApprovalAsync` contract works against both. This means a single approval service can handle requests from either library.

---

## What Does Not Cross the Boundary

The following are entirely separate and belong to their respective libraries. Nothing from the Extensions.AI middleware or pipeline is used by Extensions.Agents, and vice versa.

**Extensions.AI only:**

- `DurableChatWorkflow` — the workflow that manages MEAI conversation history
- `DurableChatActivities` — the activity that executes `IChatClient.GetResponseAsync`
- `DurableFunctionActivities` — the activity that dispatches durable tool calls by name
- `DurableEmbeddingActivities` — the activity that executes `IEmbeddingGenerator.GenerateAsync`
- `DurableChatClient` — `DelegatingChatClient` middleware
- `DurableEmbeddingGenerator` — `DelegatingEmbeddingGenerator` middleware
- `DurableChatSessionClient` — external entry point for MEAI sessions
- Registration APIs: `AddDurableAI()`, `AddDurableTools()`, `UseDurableExecution()`

**Extensions.Agents only:**

- `AgentWorkflow` — the workflow that manages `AIAgent` session history and StateBag
- `AgentActivities` — the activity that executes the `AIAgent`
- `TemporalAIAgent` / `TemporalAIAgentProxy` — agent handles for workflow and external contexts
- `ITemporalAgentClient` / `DefaultTemporalAgentClient` — the update-based client
- `IAgentRouter` / `AIModelAgentRouter` — LLM-powered routing
- `TemporalAgentContext` — async-local context for tools running inside activities
- `AgentSessionStateBag` — per-session state bag persisted across turns
- Registration APIs: `AddTemporalAgents()`, `SetRouterAgent()`, `AddAIAgent()`

`Extensions.Agents` does not use `DurableChatWorkflow` or any Extensions.AI pipeline component. The two workflow types run independently, each managing its own history and HITL state.

---

## Related Documents

- [Human-in-the-Loop Patterns (Extensions.Agents)](../../how-to/MAF/hitl-patterns.md) — approval gates, dashboards, timeout configuration, and testing
- [Human-in-the-Loop Patterns (Extensions.AI)](../../how-to/MEAI/hitl-patterns.md) — MEAI tool-call approval flow and `DurableAIFunction` context
