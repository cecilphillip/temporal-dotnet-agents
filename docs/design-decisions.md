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

`DurableEmbeddingGenerator` dispatches via `Workflow.ExecuteActivityAsync` when `Workflow.InWorkflow == true`. Agent tools run inside `AgentActivities.InvokeAgentToolAsync`, which is a Temporal activity — so `Workflow.InWorkflow == false`. The generator passes through to the inner `IEmbeddingGenerator` directly.

This is the correct behavior. The per-tool `InvokeAgentTool` activity wrapper already provides the timeout and retry guarantees for the tool execution (configurable per tool via `DurableToolOptions`). Dispatching an inner embedding call as a separate activity from inside an activity would create a nested activity that bypasses the outer retry context.

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
- `AgentActivities.RunDurableAgentStepAsync` calls `IChatClient.GetStreamingResponseAsync()` directly (the workflow owns the tool loop) — stateful, manages `TemporalAgentSession`, serializes `AgentSessionStateBag`, streams `ChatResponseUpdate`, and runs `AIContextProvider` hooks. A second activity, `AgentActivities.InvokeAgentToolAsync`, dispatches each pending tool call.

These require separate activity classes. A unified activity base class would need to abstract over the session lifecycle, StateBag serialization, streaming vs. non-streaming response handling, and provider hooks — the result would be more complex than either class alone. The activity-shape divergence is about **session lifecycle, StateBag, and per-tool dispatch**, not about how messages are represented or how entries are shaped.

**Subclass-only workflow members.** Three concerns live exclusively on `AgentWorkflow` and would need to live somewhere on a MAF subclass regardless of how much further the base evolves:

- `_currentStateBag` — serialized `AgentSessionStateBag` carried forward across turns and continue-as-new boundaries (decoupling provider state from the entry-layer payload). Restored at the top of `AgentWorkflow.RunAsync`; persisted at the end of the subclass's `ExecuteTurnAsync` override; threaded through the subclass's `CreateContinueAsNewException` override.
- The fire-and-forget signal handler (`[WorkflowSignal("RunFireAndForget")] RunAgentFireAndForgetAsync`) that kicks off a background task without an Update return path. No analog in the chat library.
- The `AgentName` search-attribute upsert (via the new `UpsertCustomSearchAttributes` virtual hook on the base). The base's default is a no-op; `DurableChatWorkflow` does not override it because chat sessions are not named.

These are deliberately scoped to the subclass and are not candidates for further base-class promotion.

**MAF-specific entry fields:** The `AgentSessionRequest` subclass adds `OrchestrationId`, `ResponseType`, and `ResponseSchema` to the shared `DurableSessionRequest` base. These are MAF-only because they correspond to MAF-specific concepts (sub-agent orchestration, structured-output format hints) that have no analog in the chat library today. They're a clean extension point — additive on the subclass, invisible to AI-library readers.

**`[Workflow]` attribute is not inheritable.** `[Workflow(Inherited = false)]` means the workflow attribute cannot be declared on a base class and inherited by a subclass. The shared base (`DurableChatWorkflowBase<TOutput>`) is therefore not itself a workflow type — only the concrete subclasses (`DurableChatWorkflow`, `AgentWorkflow`) carry `[Workflow(...)]`. Each subclass redeclares the attribute and the `[WorkflowRun]`-annotated entry-point method. This is a minor mechanical cost rather than a structural blocker; the inherited members (queries, signals, updates, validators) are picked up by the SDK's reflection scan on the concrete subclass.

### Compatibility with non-`ChatClientAgent` `AIAgent` subtypes

`Temporalio.Extensions.Agents` registers durable agents via `opts.AddDurableAgent(name, configure)`. The agent type produced by the library is `ChatClientAgent` (composed internally with `UseProvidedChatClientAsIs = true` and the user-supplied `IChatClient`). Direct registration of an arbitrary `AIAgent` subtype — `A2AAgent` from `Microsoft.Agents.AI.A2A`, graph-workflow agents from `Microsoft.Agents.AI.Workflows`, or a user-built custom `AIAgent` subclass — is not supported through `AddDurableAgent`. The v0.2 surface that allowed it was removed as part of the v0.3 API consolidation; see [`MIGRATION-v0.3.md`](../MIGRATION-v0.3.md) for the before/after.

v0.3 narrows registration to `ChatClientAgent` shape — `agent.ChatClient` is a required `Func<IServiceProvider, IChatClient>` slot, and the library composes the agent internally with `UseProvidedChatClientAsIs = true`. Users who need to plug in non-`ChatClientAgent` MAF agents (`A2AAgent`, graph-workflow agents from `Microsoft.Agents.AI.Workflows`, custom `AIAgent` subclasses) cannot do so through `AddDurableAgent` today. The legacy AIAgent-instance-shaped registration was the previous escape hatch; restoring direct support is tracked as a possible follow-on once the new surface stabilises.

The compatibility matrix below describes the v0.3 dispatch behavior. The same per-`ChatClientAgent` capability cliff applies — and is now structurally enforced, since `AddDurableAgent` only constructs `ChatClientAgent` instances.

| Feature | Mechanism | Works for non-`ChatClientAgent`? |
|---|---|---|
| Basic `RunAsync` / `RunStreamingAsync` dispatch as a Temporal activity | `RunDurableAgentStepAsync` activity drives the per-step loop; the workflow fans out per-tool `InvokeAgentToolAsync` activities | n/a — `AddDurableAgent` does not register non-`ChatClientAgent` shapes |
| Conversation history (`List<DurableSessionEntry>` with `ChatMessage` content) | The activity returns `AgentStepResult.AssistantMessage` per LLM call; the workflow accumulates and normalizes to `ChatMessage` | ✅ Yes (entry-shape concern; `ChatClientAgent`-shaped agents only) |
| HITL approval, search attributes, continue-as-new, reducer pipeline | `DurableChatWorkflowBase` — agent-type-agnostic | ✅ Yes |
| **Per-request tool filtering** (`RunRequest.EnableToolCalls`, `RunRequest.EnableToolNames`) | The activity rewrites `chatOptions.Tools` per step before calling `IChatClient.GetStreamingResponseAsync` | ⚠️ **No** — only `ChatClientAgent`-shaped registrations are supported via `AddDurableAgent`. |
| **Per-request response-format override** (`RunRequest.ResponseFormat`) | The activity stamps `chatOptions.ResponseFormat` per step before the LLM call | ⚠️ **No** — same as above. |
| Per-LLM-call interception via decorated `IChatClient` (the [LLM-call interception how-to](how-to/MAF/llm-call-interception.md)) | User returns a decorated client from the `agent.ChatClient` factory; the per-step activity invokes it directly | ⚠️ **No** — depends on an inner `IChatClient`, which non-`ChatClientAgent` subtypes don't have. |
| **Granular tool dispatch** | Built in. The workflow dispatches one `InvokeAgentTool` activity per pending `FunctionCallContent`, with per-tool `DurableToolOptions` for retry/timeout. | ✅ Default behavior in v0.3 for every `AddDurableAgent` registration. |

**No `DelegatingAIAgent` interposition in v0.3.** The v0.2 `AgentWorkflowWrapper` (a `DelegatingAIAgent` subclass) is removed. Per-request tool filtering, response-format selection, and identity propagation are now handled by directly mutating the per-step `ChatOptions` inside `RunDurableAgentStepAsync`. There is no run-options upgrade path that would need updating if MAF introduced an A2A- or graph-specific run-options subclass.

### Rejected directions (preserved for future maintainers)

Two larger redesigns were considered during the Layer-1/2/3 research and explicitly rejected. These are recorded here so a future maintainer evaluating a similar proposal does not have to rediscover the analysis.

**Drop `Microsoft.Agents.AI` for `Microsoft.Agents.AI.Abstractions`.** The TA library references a small set of concrete (non-abstractions) types from the package — `ChatClientAgent`, `ChatClientAgentOptions`, `ChatClientAgentRunOptions` — used in `AgentActivities.ComposeDurableAgent`, `TemporalAIAgent`, and `TemporalAIAgentProxy`. The dependency surface is genuinely narrow. Switching to Abstractions would shrink TA's transitive closure significantly. We rejected this because every TA sample registers agents via `chatClient.AsAIAgent("Name", ...)` — an extension method that lives in the concrete `Microsoft.Agents.AI` package. Forcing every user to add a separate package reference for a method they already use is a real migration cost paid by a whole user base for an upside (smaller transitive surface) that is operational, not architectural. Revisit this only if upstream MAF moves `ChatClientAgentRunOptions` (or an equivalent factory-options shape) to Abstractions.

**Reimplement `ChatClientAgent` as a custom `AIAgent` subclass owned by TA.** This was framed as a way to "own the agent loop" and shed the `ChatClientAgentRunOptions` dependency. We rejected this because `ChatClientAgent.cs` upstream is roughly 1,100 lines that handle `ChatHistoryProvider` resolution, `AIContextProvider` pipeline plumbing, per-service-call persistence, `ContinuationToken` resumption, `AllowBackgroundResponses`, multi-session `ConversationId` management, tool-option merging, and stop-sequence concatenation. Replicating this would either require porting all of it (ongoing maintenance liability) or omitting parts (TA-specific limitations vs. upstream behavior). Worse, owning the loop in TA locks the library out of future MAF features (Compaction, Skills, Evaluation, A2A, graph-workflow agents) — the MAF team is shipping these every quarter, and TA's polymorphic `RunStreamingAsync` dispatch path picks them up for free. Owning the loop is owning a maintenance liability, not a feature.

The pragmatic posture in v0.3: `AddDurableAgent` is `ChatClientAgent`-shaped only, the workflow owns the tool loop, and per-tool dispatch is the default.

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

### The Agents library in v0.3: workflow owns the tool loop

`AgentActivities.RunDurableAgentStepAsync` performs **one LLM call** per activity dispatch. The
agent is constructed with `UseProvidedChatClientAsIs = true`, which prevents MAF from auto-wrapping
the `IChatClient` in `FunctionInvokingChatClient`. The model returns `FunctionCallContent` items
directly to the activity, which returns them to the workflow. `AgentWorkflow.ExecuteDurableAgentTurnAsync`
then dispatches each pending tool call as a separate `InvokeAgentToolAsync` activity, fans them
out via `Workflow.WhenAllAsync`, and loops back for the next LLM step. The activity owns one LLM
call; the workflow owns the loop.

This is the v0.3 equivalent of the AI library's Mode 2 — workflow-managed loop, per-tool
durability, per-tool retry/timeout via `DurableToolOptions`. There is no remaining "full-loop
inside one activity" mode for MAF agents in v0.3; the v0.2 single-activity path was removed.

### How v0.3 resolves the `AIContextProvider` concern

The pre-v0.3 framing of this section worried that a workflow-managed loop would call
`AIAgent.RunStreamingAsync` multiple times per turn — once per LLM round — and trigger
`AIContextProvider.InvokingAsync` on each call, double-writing state for stateful providers like
`Mem0Provider`.

The v0.3 design avoids this by **not** going through `AIAgent.RunStreamingAsync` for the per-step
LLM call. `RunDurableAgentStepAsync` calls `IChatClient.GetStreamingResponseAsync` directly on the
cached `ChatClientAgent.ChatClient`, then runs `AIContextProvider.InvokingAsync` exactly once at
the start of the step (and `InvokedAsync` once at the end). Within a single turn that contains N
tool rounds, the providers fire N times — not once — but each invocation is for a distinct LLM
call with its own request/response messages, which is the correct semantic for stateful providers
that want to observe each round. The "fires multiple times in one MAF `RunStreamingAsync`"
double-write hazard does not apply because there is no enclosing `RunStreamingAsync` call.

For provider authors who explicitly want once-per-turn semantics rather than once-per-step, the
guidance is to gate side-effects on the `IsFirstStep` boundary or to read the request entry out of
the session's `StateBag`. The library does not paper over this for them.

### Per-LLM-call observability is orthogonal

Per-LLM-call observability — logs, spans, metrics, custom telemetry around each model call — is
addressed by the `IChatClient` decorator pattern: return a decorated client from the
`agent.ChatClient` factory and every `RunDurableAgentStepAsync` activity sees calls flow through
it. See the how-to guide at [`docs/how-to/MAF/llm-call-interception.md`](how-to/MAF/llm-call-interception.md).
This is a separate concern from per-tool durability — the two compose, and v0.3 supports both
directly.

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

- **The activity implementations themselves.** `DurableChatActivities` and `AgentActivities` have fundamentally different shapes — stateless `IChatClient.GetResponseAsync` vs. stateful per-step `IChatClient.GetStreamingResponseAsync` with session lifecycle, StateBag serialization, streaming, `AIContextProvider` hooks, and a separate `InvokeAgentToolAsync` activity for per-tool dispatch. The subclass owns activity-input construction in its `ExecuteTurnAsync` override, which is the right seam: the activity payload (`DurableChatInput` vs. `AgentStepInput` / `InvokeAgentToolInput`) reflects the activity shape.
- **Subclass-only workflow members on `AgentWorkflow`.** The `_currentStateBag` carry-forward, the fire-and-forget signal handler, and the `AgentName` search-attribute upsert. These are intentionally scoped to the subclass.

The argument for two libraries today is the activity-layer fork plus those subclass-only workflow members — not the type system, the workflow-loop body, or the workflow-input shape, all of which have converged.

**Compose MEAI features into MAF agents via `clientFactory` and DI — no deeper coupling required or recommended.**

A plain `IChatReducer` in `clientFactory` gives in-pipeline LLM-context reduction. An `IEmbeddingGenerator` injected into a tool class gives embeddings. Both patterns work without `AddDurableAI()`, without `DurableChatWorkflow`, and without any new abstractions. (Workflow-level history reduction is now the entry-shaped `Func<IList<DurableSessionEntry>, IList<DurableSessionEntry>>?` on `HistoryReducer`, symmetric across both libraries.)

**Forward-looking note (post-Layer-3).** Layer 3 collapsed the workflow-loop fork point. The remaining seams worth considering, in roughly increasing order of difficulty:

- **Workflow-update payload type unification.** `DurableChatWorkflow` accepts `DurableChatInput` from its update; `AgentWorkflow` accepts `RunRequest`. These shapes diverge intentionally (`RunRequest` carries `OrchestrationId`, `ResponseFormat`, etc.), but a shared base or a sealed-hierarchy approach could reduce duplication on the request side. Modest payoff; modest risk.
- **Granular tool dispatch in the Agents library.** Shipped in v0.3 — `AddDurableAgent` dispatches each LLM call as a `RunDurableAgentStep` activity and each tool call as an `InvokeAgentTool` activity, with per-tool retry/timeout via `DurableToolOptions`. The MEAI library has the analogous opt-in via `AddDurableTools()`. No further work outstanding.
- **A shared activity base class.** Hardest of the three. `DurableChatActivities.GetResponseAsync` and `AgentActivities.RunDurableAgentStepAsync` (plus its sibling `InvokeAgentToolAsync`) have very different shapes (the latter manages session lifecycle, StateBag, streaming, `AIContextProvider`, and a separate per-tool activity for fan-out). Unifying them would require abstracting over significantly more behavior than the workflow loops did. Possible in principle; not obviously a win.

This document does not commit to any of the above. They are listed for completeness; the natural pause point after Layer 3 is to let the new shared base settle in production before extracting more.

---

_Last updated: 2026-05-07 — refreshed for v0.3: `AgentWorkflowWrapper` removed, `AgentActivities.ExecuteAgentAsync` superseded by `RunDurableAgentStepAsync` + `InvokeAgentToolAsync`, granular tool dispatch is now the default behavior, `AIContextProvider` constraint resolved by per-step `IChatClient.GetStreamingResponseAsync` invocation. Earlier history: 2026-04-30 added exit criteria for granular tool dispatch, pointer to LLM-call interception how-to, the Tier 1 #3 timeout-name harmonization, a compatibility matrix for non-`ChatClientAgent` subtypes, and the "Rejected directions" subsection._
