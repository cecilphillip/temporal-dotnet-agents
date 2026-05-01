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
| `Microsoft.Extensions.AI.ChatMessage` and `AIContent` subtypes | Used directly in both libraries' history. Both `DurableChatWorkflow` and `AgentWorkflow` store entries whose `Messages` is `IReadOnlyList<ChatMessage>`. Polymorphism (`TextContent`, `FunctionCallContent`, `FunctionResultContent`, `ErrorContent`, `UsageContent`, `DataContent`, `HostedFileContent`, `HostedVectorStoreContent`, `TextReasoningContent`, `UriContent`, etc.) is preserved end-to-end through `DurableAIDataConverter`'s `AIJsonUtilities`-based serializer. |
| `ChatMessage.AdditionalProperties` round-trip | After Layer 1, this field survives serialization through the agent history pipeline as well as the chat pipeline — both libraries delegate to `AIJsonUtilities.DefaultOptions` and pick up new MEAI fields automatically. |
| `DurableSessionEntry` / `DurableSessionRequest` / `DurableSessionResponse` | After Layer 2, the entry layer itself is shared. Both `DurableChatWorkflow._history` and `AgentWorkflow._history` are `List<DurableSessionEntry>`. The MAF library's `AgentSessionRequest` / `AgentSessionResponse` are subclasses of the AI library's concrete types, contributing MAF-specific fields (`OrchestrationId`, `ResponseType`, `ResponseSchema`) without forking the base shape. Polymorphism is wired across the assembly boundary via a runtime `JsonTypeInfoResolver` modifier in `TemporalAgentJsonUtilities`. |
| Per-turn `Usage` and `CorrelationId` | `DurableSessionResponse.Usage` (`Microsoft.Extensions.AI.UsageDetails`, no wrapper) and `DurableSessionEntry.CorrelationId` are queryable on both libraries' `GetHistory()` results. |
| `DurableChatWorkflowBase<TOutput>` — workflow base class | After Layer 3, `AgentWorkflow : DurableChatWorkflowBase<AgentResponse>`. The base owns the session-loop body (turn mutex, `_isProcessing` gate, history append/reduce, continue-as-new triggering), the `[WorkflowQuery("GetHistory")]` handler, the `[WorkflowSignal("RequestShutdown")]` handler, all four HITL approval methods + their validators, and the standard `TurnCount` / `SessionCreatedAt` search-attribute upserts (gated by `EnableSearchAttributes`). The MAF subclass implements four hooks (`BuildResponseEntry`, `ExecuteTurnAsync`, `CreateContinueAsNewException`, `UpsertCustomSearchAttributes`) and adds a `[WorkflowUpdate("RunAgent")]` + `[WorkflowSignal("RunFireAndForget")]` for MAF-specific entry points. `AgentWorkflow` shrinks by ~150 lines vs. Layer 2. |
| `DurableChatWorkflowInput` — workflow-input base record | After Layer 3, `AgentWorkflowInput : DurableChatWorkflowInput`. Shared fields (`MaxEntryCount`, `HistoryReducer`, `OriginalCreatedAt`, `EnableSearchAttributes`, `CarriedHistory`, `ActivityTimeout`, `HeartbeatTimeout`) come from the base. MAF-only fields (`AgentName`, `TaskQueue`, `CarriedStateBag`, `RetryPolicy`) live on the subclass. The Tier 1 #3 timeout-name harmonization moved `ActivityTimeout` / `HeartbeatTimeout` from MAF-side duplicates (`ActivityStartToCloseTimeout` / `ActivityHeartbeatTimeout`, `TimeSpan?`) to base-inherited fields (`TimeSpan` non-null, defaults 5 min / 2 min). |

### What cannot be unified (today)

After Layers 1, 2, and 3, the message, content, entry, workflow-input, and workflow-loop layers are all shared. The remaining divergence is concentrated in two places: the activity implementation, and a small handful of subclass-only workflow members.

**Activity layer:** The two libraries dispatch fundamentally different calls in their core activities.

- `DurableChatActivities` calls `IChatClient.GetResponseAsync()` — stateless, returns `ChatResponse`, no session object.
- `AgentActivities.ExecuteAgentAsync` calls `AIAgent.RunStreamingAsync()` — stateful, manages `TemporalAgentSession`, serializes `AgentSessionStateBag`, streams `AgentResponseUpdate`, and runs `AIContextProvider` hooks.

These require separate activity classes. A unified activity base class would need to abstract over the session lifecycle, StateBag serialization, streaming vs. non-streaming response handling, and provider hooks — the result would be more complex than either class alone. The activity-shape divergence is about **session lifecycle and StateBag**, not about how messages are represented or how entries are shaped.

**Subclass-only workflow members.** Three concerns live exclusively on `AgentWorkflow` and would need to live somewhere on a MAF subclass regardless of how much further the base evolves:

- `_currentStateBag` — serialized `AgentSessionStateBag` carried forward across turns and continue-as-new boundaries (decoupling provider state from the entry-layer payload). Restored at the top of `AgentWorkflow.RunAsync`; persisted at the end of the subclass's `ExecuteTurnAsync` override; threaded through the subclass's `CreateContinueAsNewException` override.
- The fire-and-forget signal handler (`[WorkflowSignal("RunFireAndForget")] RunAgentFireAndForgetAsync`) that kicks off a background task without an Update return path. No analog in the chat library.
- The `AgentName` search-attribute upsert (via the new `UpsertCustomSearchAttributes` virtual hook on the base). The base's default is a no-op; `DurableChatWorkflow` does not override it because chat sessions are not named.

These are deliberately scoped to the subclass and are not candidates for further base-class promotion.

**MAF-specific entry fields:** The `AgentSessionRequest` subclass adds `OrchestrationId`, `ResponseType`, and `ResponseSchema` to the shared `DurableSessionRequest` base. These are MAF-only because they correspond to MAF-specific concepts (sub-agent orchestration, structured-output format hints) that have no analog in the chat library today. They're a clean extension point — additive on the subclass, invisible to AI-library readers.

**`[Workflow]` attribute is not inheritable.** `[Workflow(Inherited = false)]` means the workflow attribute cannot be declared on a base class and inherited by a subclass. The shared base (`DurableChatWorkflowBase<TOutput>`) is therefore not itself a workflow type — only the concrete subclasses (`DurableChatWorkflow`, `AgentWorkflow`) carry `[Workflow(...)]`. Each subclass redeclares the attribute and the `[WorkflowRun]`-annotated entry-point method. This is a minor mechanical cost rather than a structural blocker; the inherited members (queries, signals, updates, validators) are picked up by the SDK's reflection scan on the concrete subclass.

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

If the goal is **per-LLM-call observability** (logs, spans, metrics, custom telemetry around each
model call) — not per-tool durability — that capability is already available today via the
`ChatClientFactory` pattern; no library change is required. See the how-to guide at
`docs/how-to/MAF/llm-call-interception.md`. Maintainers triaging requests for granular dispatch
should rule out the observability case first.

### Exit criteria — when would granular dispatch be added?

The gate above does not say "never." It says "not yet, and not without a concrete reason." This
section makes the bar visible so a future maintainer can evaluate a request against a checklist
rather than re-litigating the design from scratch.

**Granular dispatch becomes a candidate for implementation when at least one of the following is
true and documented:**

1. **A documented production cost or billing incident.** A real failure where a costly tool
   error caused a full-turn replay — re-billing the LLM call plus tools 1..N-1 — at a scale that
   has financial impact. The bar is concrete, not hypothetical:
   - The incident is reproducible in a test harness (or has at least one customer-supplied trace)
     showing the wasted spend.
   - The waste exceeds either ~$100 of recoverable LLM/tool spend per failure occurrence, or
     occurs at a frequency above ~10 full-turn replays per week on a single agent.
   - The user has already considered narrower mitigations (smaller turns, fewer tools per turn,
     more aggressive activity retry policy) and explained why they are insufficient.

2. **A long-running tool that exceeds the activity timeout because the timeout is shared across
   all tools in the turn.** The full-turn `StartToCloseTimeout` and `HeartbeatTimeout` apply to
   the entire LLM-plus-tools loop. If a single tool legitimately needs hours but the turn as a
   whole would not, the user is forced to choose between an unreasonably large turn timeout
   (masking real failures elsewhere) and breaking the long-running work out of the agent loop.
   Per-tool dispatch would let each tool have its own timeout.

3. **An audit or compliance requirement for per-tool history with independent retry attempts
   visible in the workflow event history.** Some regulated environments require that each tool
   invocation produce a separate, immutable event-history entry with its own retry counter and
   outcome. The full-loop model produces a single activity entry per turn, with internal tool
   rounds invisible in event history. If a customer can point to a written compliance requirement
   (SOX, HIPAA, FedRAMP, internal audit policy) that mandates per-tool granularity, that is a
   sufficient driver.

4. **A request from a user who has already adopted the constraint.** Specifically: the user has
   migrated off stateful `AIContextProvider` implementations (no `Mem0Provider` or similar that
   would double-fire in a workflow-managed loop), is using `ChatClientAgent` (not `A2AAgent` or
   graph-workflow agents), and explicitly accepts the documented limitation that granular
   dispatch is incompatible with non-`ChatClientAgent` types. This is the only criterion that
   does not require an external incident — but it requires the user to have already done the
   migration work, not merely to want the feature.

**Negative criteria — what would NOT unlock this:**

- "It feels architecturally cleaner." Architectural restlessness is the failure mode this gate
  exists to prevent.
- "We should be more like `Temporalio.Extensions.AI` Mode 2 (`AddDurableTools()`)." Mode 2 is
  already reachable from MAF code via `Temporalio.Extensions.AI` directly; symmetry between
  libraries is not, by itself, a reason to add a feature.
- Speculative scaling concerns ("at high enough volume this could matter"). Real volume that
  matters today qualifies under criterion 1; speculative volume does not.
- A user who wants "more visibility into LLM calls" but has not yet tried `ChatClientFactory`
  interception. That is criterion-zero — if interception solves the visibility problem, the user
  did not need granular dispatch at all. See `docs/how-to/MAF/llm-call-interception.md`.
- A user who wants per-tool retry policies but has not yet considered tool-level retry inside
  the tool implementation (e.g., Polly inside the function body). The activity-level retry policy
  is one knob; the tool-internal retry policy is another, and is reachable today.

**What would NOT be enough on its own:**

- A single user request without an incident or compliance driver.
- A benchmark showing speed differences. Granular dispatch trades latency for durability
  granularity; it does not improve throughput in the steady state.
- An upstream MAF API change that hypothetically *could* be used by a granular implementation.
  The constraint discussed above is `AIContextProvider.InvokingAsync` semantics, not API surface.

**Process when the bar is met:** Open an issue documenting which criterion the request satisfies,
quote the incident report or compliance citation, and propose a design that explicitly handles
the `AIContextProvider` constraint (either documents the incompatibility with stateful providers,
or uses the `ChatClientFactory` pattern to substitute a stripped client for the LLM-only rounds).
Then revisit Tier 2 of the recommendation in the team's research log.

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

**Share at the type level and the workflow-loop level — but not at the activity level.**

After Layers 1 through 3, the shared surface is substantially broader than it was at the start of this analysis. It now includes:

- HITL types — `DurableApprovalRequest`, `DurableApprovalDecision`, `DurableApprovalMixin`.
- The data converter and its DI auto-wiring — `DurableAIDataConverter`, `DurableAIClientOptionsConfigurator`, `DurableAIWorkerClientConfigurator`.
- Session attribute keys — `DurableSessionAttributes` (`TurnCount`, `SessionCreatedAt`).
- Message and content types — `Microsoft.Extensions.AI.ChatMessage` and the full `AIContent` hierarchy (Layer 1).
- The entry layer — `DurableSessionEntry`, `DurableSessionRequest`, `DurableSessionResponse`, with MAF subclasses `AgentSessionRequest` / `AgentSessionResponse` extending the shared base (Layer 2). Both libraries' workflow history is now `List<DurableSessionEntry>`, with polymorphism wired across the assembly boundary via a runtime `JsonTypeInfoResolver` modifier.
- Per-turn observability fields — `CorrelationId` and `Usage` (`Microsoft.Extensions.AI.UsageDetails`, used directly with no wrapper) on every response entry, queryable via `GetHistory()` on either library's session client.
- The workflow-loop body and the workflow-input record (Layer 3). `AgentWorkflow : DurableChatWorkflowBase<AgentResponse>` and `AgentWorkflowInput : DurableChatWorkflowInput`. The session-loop body — turn mutex, history append/reduce, continue-as-new triggering, monotonic turn-count derivation, the `[WorkflowQuery("GetHistory")]` handler, the `[WorkflowSignal("RequestShutdown")]` handler, and all four HITL approval methods + their validators — lives once on the base. The MAF subclass implements four hooks (`BuildResponseEntry`, `ExecuteTurnAsync`, `CreateContinueAsNewException`, `UpsertCustomSearchAttributes`) and contributes a `[WorkflowUpdate("RunAgent")]` plus a `[WorkflowSignal("RunFireAndForget")]` for MAF-specific entry points. Public API surface for both libraries is unchanged from Layer 2.

What remains divergent after Layer 3:

- **The activity implementations themselves.** `DurableChatActivities` and `AgentActivities` have fundamentally different shapes — stateless `IChatClient.GetResponseAsync` vs. stateful `AIAgent.RunStreamingAsync` with session lifecycle, StateBag serialization, streaming, and `AIContextProvider` hooks. The subclass owns activity-input construction in its `ExecuteTurnAsync` override, which is the right seam: the activity payload (`DurableChatInput` vs. `ExecuteAgentInput`) reflects the activity shape.
- **Subclass-only workflow members on `AgentWorkflow`.** The `_currentStateBag` carry-forward, the fire-and-forget signal handler, and the `AgentName` search-attribute upsert. These are intentionally scoped to the subclass.

The argument for two libraries today is the activity-layer fork plus those subclass-only workflow members — not the type system, the workflow-loop body, or the workflow-input shape, all of which have converged.

**Compose MEAI features into MAF agents via `clientFactory` and DI — no deeper coupling required or recommended.**

A plain `IChatReducer` in `clientFactory` gives in-pipeline LLM-context reduction. An `IEmbeddingGenerator` injected into a tool class gives embeddings. Both patterns work without `AddDurableAI()`, without `DurableChatWorkflow`, and without any new abstractions. (Workflow-level history reduction is now the entry-shaped `Func<IList<DurableSessionEntry>, IList<DurableSessionEntry>>?` on `HistoryReducer`, symmetric across both libraries.)

**Forward-looking note (post-Layer-3).** Layer 3 collapsed the workflow-loop fork point. The remaining seams worth considering, in roughly increasing order of difficulty:

- **Workflow-update payload type unification.** `DurableChatWorkflow` accepts `DurableChatInput` from its update; `AgentWorkflow` accepts `RunRequest`. These shapes diverge intentionally (`RunRequest` carries `OrchestrationId`, `ResponseFormat`, etc.), but a shared base or a sealed-hierarchy approach could reduce duplication on the request side. Modest payoff; modest risk.
- **Granular tool dispatch in the Agents library.** The MEAI library already supports per-tool durability via `AddDurableTools()` (no `UseFunctionInvocation`); the Agents library does not. The blocker is `AIContextProvider.InvokingAsync` semantics under a workflow-managed loop, not the workflow loop itself. Different motivation, different design surface — defer until a real production use case surfaces.
- **A shared activity base class.** Hardest of the three. `DurableChatActivities.GetResponseAsync` and `AgentActivities.ExecuteAgentAsync` have very different shapes (the latter manages session lifecycle, StateBag, streaming, `AIContextProvider`). Unifying them would require abstracting over significantly more behavior than the workflow loops did. Possible in principle; not obviously a win.

This document does not commit to any of the above. They are listed for completeness; the natural pause point after Layer 3 is to let the new shared base settle in production before extracting more.

---

_Last updated: 2026-04-30 — added exit criteria for granular tool dispatch, pointer to LLM-call interception how-to, and the Tier 1 #3 timeout-name harmonization (`ActivityTimeout` / `HeartbeatTimeout` are now inherited from the AI library's base record)._
