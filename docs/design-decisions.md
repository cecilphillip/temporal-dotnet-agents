# Design Decisions

Architectural findings and rationale for the two-library design. This document records decisions that are not derivable from reading the code alone — the why behind the structure, and the investigation results that shaped it.

---

## Table of Contents

1. [MEAI Features in the MAF Context](#meai-features-in-the-maf-context)
2. [Code Sharing Analysis](#code-sharing-analysis)
3. [MAF Library Evolution](#maf-library-evolution)
4. [Architectural Verdict](#architectural-verdict)

---

## MEAI Features in the MAF Context

The question: do the durable embedding and history reduction features from `Temporalio.Extensions.AI` transfer usefully to `Temporalio.Extensions.Agents`?

### History Reduction

**Yes — via a plain `IChatReducer` in `clientFactory`.**

`AgentWorkflow._history` already provides durable full-history persistence across turns and continue-as-new transitions. The same approach now applies to `DurableChatWorkflow`, whose `_history` field is the single source of truth for full conversation state in the MEAI stack.

The useful pattern in either stack is passing a plain stateless `IChatReducer` (e.g., `new MessageCountingChatReducer(N)`) in the chat client pipeline. This trims the LLM context window on each turn without disturbing the workflow-owned history.

### Durable Embeddings

**Passthrough — the outer activity already provides durability.**

`DurableEmbeddingGenerator` dispatches via `Workflow.ExecuteActivityAsync` when `Workflow.InWorkflow == true`. Agent tools run inside `AgentActivities.ExecuteAgentAsync`, which is a Temporal activity — so `Workflow.InWorkflow == false`. The generator passes through to the inner `IEmbeddingGenerator` directly.

This is the correct behavior. The `ExecuteAgent` activity wrapper already provides the timeout and retry guarantees for the full tool execution. Dispatching an inner embedding call as a separate activity from inside an activity would create a nested activity that bypasses the outer retry context.

Practical upshot: inject `IEmbeddingGenerator<string, Embedding<float>>` into tool classes via DI constructor injection. Register with or without `UseDurableExecution()` — the runtime behavior is identical in the tool context. `UseDurableExecution()` is only meaningful when `GenerateAsync` is called from inside a custom `[Workflow]` class.

---

## Code Sharing Analysis

### What is already shared

These types are defined in `Temporalio.Extensions.AI` and used by both libraries:

| Type | Purpose |
|---|---|
| `DurableApprovalMixin` | Internal helper encapsulating the HITL approval state machine (`WaitConditionAsync`, timeout, decision storage). Both `DurableChatWorkflow` and `AgentWorkflow` hold an instance and delegate their approval update/query methods to it. |
| `DurableApprovalRequest` | Wire type for initiating a human approval gate. Used by both `DurableChatSessionClient` and `ITemporalAgentClient`. |
| `DurableApprovalDecision` | Wire type for the human response to an approval request. |
| `DurableSessionAttributes` | Typed search attribute keys (`TurnCount`, `SessionCreatedAt`). Shared key names allow a single Temporal list query to span both `DurableChatWorkflow` and `AgentWorkflow` instances. |
| `DurableAIDataConverter` | `DataConverter` configured with `AIJsonUtilities.DefaultOptions` for correct `AIContent` polymorphic round-tripping through Temporal history. Required by both stacks. |
| `DurableAIClientOptionsConfigurator` | `IConfigureOptions<TemporalClientConnectOptions>` that auto-wires `DurableAIDataConverter.Instance` when using `AddTemporalClient(address, ns)`. |
| `DurableAIWorkerClientConfigurator` | `IPostConfigureOptions<TemporalWorkerServiceOptions>` that auto-wires `DurableAIDataConverter.Instance` when using the 3-arg `AddHostedTemporalWorker` overload. |

### What cannot be unified

**Activity layer:** The two libraries dispatch fundamentally different calls in their core activities.

- `DurableChatActivities` calls `IChatClient.GetResponseAsync()` — stateless, returns `ChatResponse`, no session object.
- `AgentActivities.ExecuteAgentAsync` calls `AIAgent.RunStreamingAsync()` — stateful, manages `TemporalAgentSession`, serializes `AgentSessionStateBag`, streams `AgentResponseUpdate`, and runs `AIContextProvider` hooks.

These require separate activity classes. A unified base class would need to abstract over the session lifecycle, StateBag serialization, streaming vs. non-streaming response handling, and provider hooks — the result would be more complex than either class alone.

**History format:** `DurableChatWorkflow` stores `List<ChatMessage>` (MEAI types). `AgentWorkflow` stores `List<TemporalAgentStateEntry>` — a richer format that includes correlation IDs, timestamps, and per-turn token usage, serialized via `TemporalAgentStateJsonContext`. Different metadata requirements and different serialization contexts mean a common history type would need to accommodate fields that are irrelevant to one side or the other.

**One unexplored option:** A shared `DurableChatWorkflowBase<TOutput>` from which both `DurableChatWorkflow` and `AgentWorkflow` inherit the session loop, continue-as-new logic, and HITL handling. Assessment: this would require at least three type parameters to abstract over the history entry type, input type, and output type. The `[Workflow]` attribute has `Inherited = false`, which prevents base classes from carrying the attribute — the subclass must redeclare it. The marginal reduction in duplicated code does not justify the added abstraction complexity. Not recommended.

---

## MAF Library Evolution

`Microsoft.Agents.AI` is diverging from MEAI as a platform, not converging. Upstream direction includes:

- **Graph-based Workflows engine** — declarative agent orchestration at the MAF level, separate from `IChatClient`
- **A2A protocol** — Agent-to-Agent communication protocol, not tied to any particular chat client abstraction
- **AG-UI** — agent UI streaming protocol

These directions indicate that `AIAgent` and `IChatClient` will continue to be parallel abstractions rather than converging into one. Designing `Temporalio.Extensions.Agents` toward a future where `AIAgent` becomes an `IChatClient` subtype would be planning against the upstream trajectory.

**No pluggable durability backend.** `Microsoft.Agents.AI.DurableTask` (the existing durable counterpart) is tightly coupled to Azure Storage via the DurableTask framework. There is no durability interface that Temporal can implement as a drop-in replacement. The relationship between `Temporalio.Extensions.Agents` and MAF is a Temporal-native integration, not a backend swap.

**`[Workflow]` attribute is not inheritable.** `[Workflow(Inherited = false)]` means workflow attributes cannot be declared on a base class and inherited by a subclass. This is a hard constraint against building a shared workflow base class that the Temporal SDK discovers automatically.

**One plausible upstream ask:** `AIAgent.AsChatClient()` — an adapter that wraps an `AIAgent` as an `IChatClient`. If it existed, a MAF agent could be registered with `AddDurableAI()` (Combination 2) and gain the full MEAI middleware pipeline, including `DurableChatClient`, without `AddTemporalAgents()`. Impact: moderate — would simplify the transitional Combination 1 posture and allow MEAI middleware to compose over MAF agents more naturally. Likelihood: uncertain; not on any known public roadmap as of this writing.

---

## Architectural Verdict

**Keep the two-library design.**

- `Temporalio.Extensions.AI` serves `IChatClient` users who do not need named agents, routing, StateBag, or `AIContextProvider`. It is the right choice for most MEAI-native projects.
- `Temporalio.Extensions.Agents` serves `AIAgent` users who need the full MAF session model, search attributes, routing, parallel fan-out, and `TemporalAgentContext`. It is the right choice for MAF-native projects.

**Share at the type level — not the activity or workflow level.**

The sharing point is HITL types (`DurableApprovalRequest`, `DurableApprovalDecision`, `DurableApprovalMixin`), the data converter (`DurableAIDataConverter`), and session attribute keys (`DurableSessionAttributes`). These are genuinely shared concerns. The activity implementations and workflow history formats are not.

**Compose MEAI features into MAF agents via `clientFactory` and DI — no deeper coupling required or recommended.**

A plain `IChatReducer` in `clientFactory` gives history reduction. An `IEmbeddingGenerator` injected into a tool class gives embeddings. Both patterns work without `AddDurableAI()`, without `DurableChatWorkflow`, and without any new abstractions.

---

_Last updated: 2026-04-07_
