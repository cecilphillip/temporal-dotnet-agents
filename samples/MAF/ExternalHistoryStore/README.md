# ExternalHistoryStore

A multi-tenant customer support session that demonstrates **three concerns
working together** in a single runnable sample:

1. **Layer 1 — `IAgentHistoryStore`** (workflow-level): conversation history
   lives in an external store (here, an in-memory `ConcurrentDictionary`) so PII
   never enters the Temporal `ActivityScheduled` event log.
2. **Layer 2 — `AIContextProvider`** (MAF-level): a `TenantContextProvider`
   injects per-call tenant metadata as a system message before each LLM call.
3. **Reduction strategy** (inside Layer 1): `LoadAsync` returns only the most
   recent N entries, while a separate `SnapshotFull` retains the complete
   audit trail. This is the documented workaround for the in-process
   `HistoryReducer` not applying to external storage.

## Prerequisites

- `temporal server start-dev` running on `localhost:7233`
- `OPENAI_API_KEY` set via user-secrets:
  ```bash
  dotnet user-secrets set "OPENAI_API_KEY" "sk-..." \
      --project samples/MAF/ExternalHistoryStore
  ```

## Run

```bash
dotnet run --project samples/MAF/ExternalHistoryStore/ExternalHistoryStore.csproj
```

## Expected Output

The sample drives a six-turn conversation against tenant **Acme Corp** and
prints, after the conversation:

```
=== Tenant: Acme Corp (Enterprise tier) — reduction window: 4 entries ===

Turn 1: "What's the status of order ORD-001?"
Agent: Hello Acme Corp! Order ORD-001 has shipped — estimated delivery ...
       (As an Enterprise-tier customer, you're entitled to expedited support ...)

Turn 2: "And ORD-002?"
Agent: Order ORD-002 was delivered on April 28 ...

...

Turn 6: "What was my very first question?"
Agent: I don't have visibility into the earlier part of our conversation ...

=== Full History (audit trail via SnapshotFull) ===
Session 'support-acme-...': 12 entries (6 request + 6 response)

=== Reduction Statistics ===
[Reduction] LoadAsync called 6 times. Window applied 4 times ...

=== Temporal Activity Payload Inspection ===
  Last ExecuteAgent input contains 'ConversationHistory' key: False
  Last ExecuteAgent input contains turn-1 marker (ORD-001):    False
  ✓ Payload omits ConversationHistory — PII / O(n²) growth mitigated.

=== Layer Cooperation ===
[Layer 1]  IAgentHistoryStore.LoadAsync       called 6 times
[Layer 1]  IAgentHistoryStore reductions       applied 4 times
[Layer 1]  IAgentHistoryStore.SnapshotFull     12 entries retained for audit
[Layer 2]  TenantContextProvider.InvokingAsync called 6 times
```

The exact LLM phrasing varies, but the structural assertions are stable:

- **Reduction proven**: turn 6 cannot recall turn 1 (it falls outside the
  4-entry window). Turn 4 *can* recall turn 2 (still inside the window).
- **Tenant injection proven**: replies reference the tenant tier or SLA
  because `TenantContextProvider.InvokingCalls` rises by one per turn.
- **PII out of Temporal**: the last `ActivityScheduled` event payload
  contains neither the `ConversationHistory` key nor turn-1's order ID.

## Two-Layer Architecture

```
┌───────────────────────── Workflow (durable, deterministic) ─────────────────────┐
│                                                                                 │
│  [WorkflowUpdate("Ask")]  ──►  GetAgent("SupportAgent").RunAsync(messages)      │
│                                       │                                         │
│                                       │  ExecuteActivityAsync(ExecuteAgentInput)│
│                                       │  - UseExternalStore = true              │
│                                       │  - ConversationHistory = null  ◄── Layer 1│
│                                       ▼                                         │
└─────────────────────────────────────  │  ──────────────────────────────────────┘
                                        │  (activity boundary — Temporal event written here)
┌─────────────────────────────────────  │  ──────── Activity (inside worker process) ──┐
│                                       ▼                                              │
│  IAgentHistoryStore.LoadAsync(sessionId)   ◄── Layer 1: returns recent N entries     │
│         │                                                                            │
│         │  rebuilt messages[]                                                        │
│         ▼                                                                            │
│  ChatClientAgent (MAF) — invokes AIContextProviders before the IChatClient call:     │
│    └─►  TenantContextProvider.InvokingAsync(ctx)  ◄── Layer 2: emits system context  │
│              │                                                                       │
│              ▼                                                                       │
│    IChatClient.GetStreamingResponseAsync(messages + tenant-system, options)          │
│                                                                                      │
│  IAgentHistoryStore.AppendAsync(sessionId, [request, response])  ◄── Layer 1         │
└──────────────────────────────────────────────────────────────────────────────────────┘
```

**Key insight:** the two layers are independent.

- `IAgentHistoryStore` decides **where conversation history lives** (and
  whether it enters Temporal's event log). It runs at the *workflow boundary*,
  before the activity-scheduled event is written.
- `AIContextProvider` decides **what additional context gets injected per
  call** (tenant metadata, retrieval results, RBAC-filtered tools). It runs
  *inside the activity*, after the event is already written.

The store doesn't know about the context provider. The context provider
doesn't know about the store. Reduction is a third concern that lives inside
the store implementation.

## Reduction Strategy

When `opts.HistoryStore` (or `agent.HistoryStore`) is configured, the
in-process `HistoryReducer` delegate is **not** applied — the reducer is
`[JsonIgnore]` and cannot cross the activity boundary. The recommended pattern
is to implement reduction inside the store itself:

| Pattern | Where | Trade-off |
|--------|-------|-----------|
| **Recent-N truncation** (this sample) | Inside `LoadAsync` | Cheap, deterministic. Loses the earliest context — turn-6 cannot recall turn-1 in the demo. |
| **Summarize-and-keep-recent** | Inside `LoadAsync` | An LLM call inside `LoadAsync` compresses older turns into a single summary entry, then prepends it to the recent window. Costs an extra LLM call per turn. |
| **At-rest reduction in `ReplaceAsync`** | When continue-as-new fires | The store collapses its on-disk view in-place; subsequent loads start from the reduced set. Best when reducing the on-disk *write* matters (regulatory retention, hot-storage cost). |

The sample uses recent-N (entries 4 by default). To switch to a different
strategy, change the body of `InMemoryHistoryStore.LoadAsync` — the contract
with the activity is unchanged.

## Plumbing the Tenant ID

The workflow needs to tell the activity-side `TenantContextProvider` which
tenant is active for *this* turn. The sample stamps the active tenant onto
`ChatMessage.AdditionalProperties[TenantContextProvider.TenantIdProperty]`
on the outgoing user message. The provider's `ProvideAIContextAsync` walks
the input messages most-recent-first and looks up the tenant in the
DI-registered `TenantDirectory`.

This is a deliberate use of `ChatMessage.AdditionalProperties` as a sideband
for orchestration metadata that travels alongside the message but is not
itself prompt content. Alternative plumbing:

- **Workflow-scoped resolver** (an `AsyncLocal<TenantInfo>` set by the
  workflow before calling the agent): does **not** survive the activity
  boundary in this codebase — workflow `AsyncLocal`s do not flow into
  activity DI.
- **System message injected by the workflow**: works but mixes orchestration
  metadata with prompt text the LLM sees.

`AdditionalProperties` is the cleanest path the existing API allows.

## See Also

- [`docs/how-to/MAF/external-history-store.md`](../../../docs/how-to/MAF/external-history-store.md) — full how-to with database backend examples
- [`docs/architecture/MAF/agent-sessions-and-workflow-loop.md`](../../../docs/architecture/MAF/agent-sessions-and-workflow-loop.md) — the durable session loop
- [`docs/architecture/MAF/session-statebag-and-context-providers.md`](../../../docs/architecture/MAF/session-statebag-and-context-providers.md) — `AIContextProvider` and StateBag persistence
