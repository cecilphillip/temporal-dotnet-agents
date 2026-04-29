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

## Function Invocation: Loop Ownership and Durability Granularity

The two libraries handle tool/function invocation differently. This difference is not a bug — it
reflects a deliberate trade-off between durability granularity and implementation complexity.

### The AI library supports two modes

**Mode 1 — Full loop inside one activity (default, idiomatic MEAI registration)**

When `IChatClient` is registered with MEAI's `UseFunctionInvocation()` middleware:

```csharp
services.AddChatClient(innerClient)
    .UseFunctionInvocation()
    .Build();
```

`DurableChatActivities.GetStreamingResponseAsync` calls a `FunctionInvokingChatClient` under the
hood. That middleware owns the entire tool loop — LLM call, detect tool requests, execute tools,
feed results back, LLM call again — and only returns the final complete response. From Temporal's
perspective, the entire turn is one atomic activity regardless of how many tool rounds occurred.

**Mode 2 — Workflow-managed loop (opt-in, requires `AddDurableTools()`)**

When tools are registered via `AddDurableTools()` and the `IChatClient` does NOT have
`UseFunctionInvocation()`, each `AIFunction` is wrapped as a `DurableAIFunction`. When called
from inside a `[Workflow]`, `DurableAIFunction` dispatches via `Workflow.ExecuteActivityAsync`
rather than calling the function directly. The workflow manages the loop: call LLM activity →
detect tool requests → dispatch each tool as a separate activity → feed results back → repeat.

In this mode every tool call has its own entry in workflow event history, its own retry policy,
and its own timeout. Failure on tool 7 retries only tool 7 — not the full turn.

**Both modes produce identical final responses.** The difference is durability granularity:

| Registration | Loop location | Temporal checkpoint per |
|---|---|---|
| `UseFunctionInvocation()` | MEAI middleware inside one activity | Turn |
| `AddDurableTools()` (no `UseFunctionInvocation`) | Workflow across multiple activities | LLM call + each tool call |

### The Agents library today: always full loop

`AgentActivities.ExecuteAgentAsync` calls MAF's `RunStreamingAsync`, which owns the entire
agentic loop internally — identical in behavior to Mode 1 above. MAF's `FunctionInvokingChatClient`
handles all LLM rounds and tool executions before the activity returns.

There is no opt-in equivalent of Mode 2 in the Agents library today. Adding one would require the
workflow to manage the tool loop — calling a "LLM-only" activity, dispatching tool activities,
feeding results back — mirroring the AI library's Mode 2.

### The `AIContextProvider` constraint on granular dispatch

MAF's `AIContextProvider.InvokingAsync` fires once per `RunStreamingAsync` call at the MAF
orchestration level. In the current single-activity model it fires exactly once per turn — even
if `FunctionInvokingChatClient` internally loops through many tool rounds.

A workflow-managed loop would call `RunStreamingAsync` multiple times per turn (once for the
initial LLM call, once per LLM round after tool results). This would cause `AIContextProvider` to
fire on each call, potentially:

- Adding duplicate context messages for providers like `Mem0Provider`
- Double-writing state in `InvokedAsync`

Any granular dispatch mode in the Agents library must either: (a) document this as an
incompatibility with stateful `AIContextProvider` implementations, or (b) use MAF's
`ChatClientAgentRunOptions.ChatClientFactory` to inject a stripped `IChatClient` per-call rather
than calling `RunStreamingAsync` at all for the LLM-only rounds.

### How `ChatClientFactory` enables granular dispatch without a second agent instance

`ChatClientAgentRunOptions.ChatClientFactory` (`Func<IChatClient, IChatClient>?`) allows
per-request replacement or decoration of the chat client. MAF applies it before each
`RunAsync`/`RunStreamingAsync` call without permanently modifying the agent:

```csharp
private static IChatClient ApplyRunOptionsTransformations(AgentRunOptions? options, IChatClient chatClient)
{
    if (options is ChatClientAgentRunOptions agentChatOptions && agentChatOptions.ChatClientFactory is not null)
    {
        chatClient = agentChatOptions.ChatClientFactory(chatClient);
    }
    return chatClient;
}
```

This is the mechanism a future granular dispatch implementation would use: for the LLM-only call,
pass a `ChatClientFactory` that strips `FunctionInvokingChatClient` from the pipeline. The LLM
sees full tool definitions, returns `FunctionCallContent`, and the workflow dispatches those tool
calls as separate activities. No second agent registration required; the agent's DI key and options
remain unchanged.

### Current recommendation

Do not add granular tool dispatch to the Agents library until a real production use case
demonstrates the need. The full-loop model is correct for most workloads — it already provides
per-turn durability via Temporal's workflow history, activity retry, and heartbeating. The granular
model adds implementation complexity and the `AIContextProvider` constraint described above.

If per-tool granularity is needed today, use `Temporalio.Extensions.AI` with `AddDurableTools()`
(Mode 2 above) and compose with MAF-specific state via `TemporalAgentContext.GetService<T>()`.

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
