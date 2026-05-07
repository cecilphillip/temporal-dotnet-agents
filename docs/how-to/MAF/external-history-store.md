# External History Store

How to opt the Agents library out of in-Temporal conversation history and into an external store you control. This is the recommended path for regulated workloads (HIPAA, PCI) and for long sessions where Temporal event size becomes a concern.

---

## Table of Contents

1. [What It Is](#what-it-is)
2. [Why Use It](#why-use-it)
3. [Two-Layer Architecture](#two-layer-architecture)
4. [Quick Start](#quick-start)
5. [Per-Agent History Store](#per-agent-history-store)
6. [Implementing `IAgentHistoryStore`](#implementing-iagenthistorystore)
7. [Migration Behavior](#migration-behavior)
8. [What Changes When Opted In](#what-changes-when-opted-in)
9. [When NOT to Use It](#when-not-to-use-it)
10. [References](#references)

---

## What It Is

`IAgentHistoryStore` is an opt-in interface that lets the Agents library persist conversation history into a backend you provide — Cosmos DB, PostgreSQL, Redis, or anything else — instead of inside the Temporal workflow's event history.

When you do **not** configure an `IAgentHistoryStore`, the library's behavior is unchanged from prior releases: conversation history lives on the `AgentWorkflow` instance as `_history`, is serialized into Temporal's event log on each activity dispatch, and is carried across continue-as-new transitions via `AgentWorkflowInput.CarriedHistory`. This default is appropriate for many workloads and is preserved indefinitely.

When you **do** configure an `IAgentHistoryStore` factory on the worker (`opts.HistoryStore = sp => ...`) or on a specific agent (`agent.HistoryStore = sp => ...`), the workflow stops including conversation history in the activity input payload, and the activity loads history from your store at the start of each turn.

In v0.3 there is **no boolean opt-in flag**. Opt-in is implicit in setting a non-null `HistoryStore` factory — at the worker level via `TemporalAgentsOptions.HistoryStore`, at the agent level via `DurableAgentBuilder.HistoryStore`, or both (per-agent wins).

---

## Why Use It

Two production problems motivate the feature:

### 1. PII and compliance

In the default configuration, every activity dispatch serializes the full conversation history into the `ActivityScheduled` Temporal event. Tool call arguments containing personally identifiable information — Social Security numbers, credit card numbers, patient identifiers — flow into Temporal's durable event log and remain there for the lifetime of the workflow execution.

For regulated workloads (HIPAA, PCI, SOC 2 with strict data-residency clauses) this is often a non-starter. An external history store keeps conversation content in a system you control, with your encryption, retention, and access-audit posture, and out of Temporal's persistence layer entirely.

### 2. O(n²) Temporal event growth

At turn N, the `ActivityScheduled` event for the Nth turn carries all N − 1 prior entries plus the new request. Across a session of N turns, total bytes written to Temporal history grow as O(n²). For a chat session of a hundred turns this is bearable; for a long-running agent session that processes thousands of messages, the cost grows quickly.

External storage decouples conversation length from Temporal event size: each activity-scheduled event carries only the agent name, the new request entry, and a session ID. The activity reads history from the store directly.

---

## Two-Layer Architecture

`IAgentHistoryStore` exists alongside Microsoft Agent Framework's `AIContextProvider` / `ChatHistoryProvider`. They solve different problems at different boundaries — they are complementary, not alternatives.

### `IAgentHistoryStore` is a Temporal-level coordination interface

When the Agents library detects that an effective `HistoryStore` is set for an agent, the workflow code path changes: prior turns are stripped from the in-workflow history (`ShouldStripMessagesFromHistoryEntry` returns `true` while building the per-step `AccumulatedMessages`), so the activity input that ships in the `ActivityScheduled` event carries only the current request entry plus any messages produced inside the same turn. Prior turns are no longer included in the Temporal event log.

When the activity runs with `AgentStepInput.IsFirstStep == true` and the resolved agent has a configured `IAgentHistoryStore`, the activity loads prior history from the store and prepends it to `AccumulatedMessages` before calling the LLM. This decision must live at the workflow boundary: anything that runs inside the activity — including any `AIContextProvider` — runs *after* the event is already written. By that point the PII would already be recorded.

### `ChatHistoryProvider` is the right abstraction inside the activity

MAF's `ChatHistoryProvider` (a subtype of `AIContextProvider`) is the idiomatic way to inject historical messages into the LLM context for a given call. When external history is opted in, the activity uses a `TemporalChatHistoryProvider` adapter — provided by the library — that loads from `IAgentHistoryStore` and feeds the agent's pipeline. You don't need to write that adapter yourself.

The split:

| Layer | Interface | Runs in | Purpose |
|---|---|---|---|
| Workflow coordination | `IAgentHistoryStore` | `AgentWorkflow` | Decide whether to put history in the activity payload at all |
| LLM context injection | `ChatHistoryProvider` (via `TemporalChatHistoryProvider`) | `AgentActivities` | Surface history to the model for a single call |

If you only register a custom `ChatHistoryProvider` without `IAgentHistoryStore`, the workflow still serializes history into the activity-scheduled event — the PII problem is unsolved. If you only configure `HistoryStore`, the library's built-in adapter handles the LLM-context-injection side automatically.

---

## Quick Start

Register a store implementation in DI and assign the worker-level `HistoryStore` factory:

```csharp
using Temporalio.Extensions.Agents;
using Temporalio.Extensions.Agents.HistoryStore;

var builder = Host.CreateApplicationBuilder(args);

// 1. Register your store implementation in DI.
builder.Services.AddSingleton<CosmosAgentHistoryStore>();
builder.Services.AddChatClient(chatClient);

// 2. Set the worker-level HistoryStore factory.
builder.Services
    .AddHostedTemporalWorker("localhost:7233", "default", "agents")
    .AddTemporalAgents(opts =>
    {
        opts.HistoryStore = sp => sp.GetRequiredService<CosmosAgentHistoryStore>();

        opts.AddDurableAgent("MyAgent", agent =>
        {
            agent.ChatClient = sp => sp.GetRequiredService<IChatClient>();
            // No agent.HistoryStore set — inherits opts.HistoryStore.
        });
    });

var host = builder.Build();
await host.RunAsync();
```

That's the entire opt-in. After this, every new agent session created on this worker stores history through `CosmosAgentHistoryStore` rather than in the workflow's `_history` list, and conversation messages no longer appear in Temporal event payloads.

---

## Per-Agent History Store

The worker-level factory applies to every durable agent that does not override it. To use a different store for one agent — for example, a regulated agent that needs PHI segregation — set `agent.HistoryStore` on its builder. The per-agent factory wins over the worker-level default.

```csharp
builder.Services.AddSingleton<MyStore>();
builder.Services.AddSingleton<HipaaStore>();
builder.Services.AddChatClient(chatClient);

builder.Services
    .AddHostedTemporalWorker(taskQueue)
    .AddTemporalAgents(opts =>
    {
        // Worker-level default — every durable agent without an override uses MyStore.
        opts.HistoryStore = sp => sp.GetRequiredService<MyStore>();

        opts.AddDurableAgent("StandardAgent", agent =>
        {
            agent.ChatClient = sp => sp.GetRequiredService<IChatClient>();
            // Inherits opts.HistoryStore (MyStore).
        });

        opts.AddDurableAgent("ComplianceAgent", agent =>
        {
            agent.ChatClient = sp => sp.GetRequiredService<IChatClient>();
            // Per-agent override — this one agent uses the regulated store instead.
            agent.HistoryStore = sp => sp.GetRequiredService<HipaaStore>();
        });
    });
```

> **Per-agent opt-out is not supported.** If you need one agent on a worker to keep history in workflow state while another uses a store, register them on separate worker builders (different task queues). The store factory applies uniformly to every durable agent on a single worker that doesn't override.

The factory runs once at first activity dispatch (the same lifecycle as `agent.ChatClient`); the resolved instance is cached for the lifetime of the worker process.

---

## Implementing `IAgentHistoryStore`

The interface is small:

```csharp
namespace Temporalio.Extensions.Agents.HistoryStore;

public interface IAgentHistoryStore
{
    Task<IReadOnlyList<DurableSessionEntry>> LoadAsync(
        string sessionId,
        CancellationToken cancellationToken = default);

    Task AppendAsync(
        string sessionId,
        IReadOnlyList<DurableSessionEntry> entries,
        CancellationToken cancellationToken = default);

    // Called at continue-as-new time to bound the store with a deterministic tail-trim.
    Task ReplaceAsync(
        string sessionId,
        IReadOnlyList<DurableSessionEntry> trimmedEntries,
        CancellationToken cancellationToken = default);
}
```

| Method | Called from | When |
|---|---|---|
| `LoadAsync` | `AgentActivities.RunDurableAgentStepAsync` | First step of every turn (`IsFirstStep == true`) — must return entries in append order |
| `AppendAsync` | `AgentActivities.RunDurableAgentStepAsync` | Final step of every turn (when the LLM produces a non-tool-calling response) — appends `[requestEntry, responseEntry]` |
| `ReplaceAsync` | `AgentActivities.ReduceHistoryInStoreAsync` (dispatched by `AgentWorkflow`) | Continue-as-new — replaces store contents with a tail-trim to `MaxEntryCount` |

`sessionId` is `TemporalAgentSessionId.WorkflowId` (a string of the form `ta-{agentName}-{key}`). It is always provided by the library; you don't generate it.

### Serialization requirements

Store `DurableSessionEntry` (or its concrete subclasses `AgentSessionRequest` / `AgentSessionResponse`), **not** raw `ChatMessage` objects. The entry graph carries:

- The `$type` polymorphic discriminator (`agent_request`, `agent_response`)
- `CorrelationId` — required for HITL approval flows and request/response pairing
- `CreatedAt` — preserves ordering across restarts
- `OrchestrationId` — set on requests issued by an orchestrating workflow
- `ResponseSchema` / `ResponseType` — structured-output format hints

Use `TemporalAgentJsonUtilities.DefaultOptions` for serialization. It is configured with the runtime polymorphism modifier that emits the discriminators correctly:

```csharp
using Temporalio.Extensions.Agents;
using System.Text.Json;

var json = JsonSerializer.Serialize(entry, TemporalAgentJsonUtilities.DefaultOptions);
var roundTripped = JsonSerializer.Deserialize<DurableSessionEntry>(
    json, TemporalAgentJsonUtilities.DefaultOptions);
```

Deserializing through any other `JsonSerializerOptions` will likely produce a base `DurableSessionEntry` with the MAF-specific fields silently dropped.

### Minimal in-memory reference implementation

This is suitable for tests and as a starting template — not production:

```csharp
using System.Collections.Concurrent;
using Temporalio.Extensions.Agents.HistoryStore;
using Temporalio.Extensions.AI;

public sealed class InMemoryAgentHistoryStore : IAgentHistoryStore
{
    private readonly ConcurrentDictionary<string, List<DurableSessionEntry>> _store = new();

    public Task<IReadOnlyList<DurableSessionEntry>> LoadAsync(
        string sessionId, CancellationToken cancellationToken = default)
    {
        var entries = _store.GetOrAdd(sessionId, _ => new());
        lock (entries)
        {
            return Task.FromResult<IReadOnlyList<DurableSessionEntry>>(entries.ToArray());
        }
    }

    public Task AppendAsync(
        string sessionId,
        IReadOnlyList<DurableSessionEntry> entries,
        CancellationToken cancellationToken = default)
    {
        var bucket = _store.GetOrAdd(sessionId, _ => new());
        lock (bucket)
        {
            bucket.AddRange(entries);
        }
        return Task.CompletedTask;
    }

    public Task ReplaceAsync(
        string sessionId,
        IReadOnlyList<DurableSessionEntry> reducedEntries,
        CancellationToken cancellationToken = default)
    {
        var bucket = _store.GetOrAdd(sessionId, _ => new());
        lock (bucket)
        {
            bucket.Clear();
            bucket.AddRange(reducedEntries);
        }
        return Task.CompletedTask;
    }
}
```

For production backends, the same shape applies — the only differences are the IO layer and the indexing strategy on `sessionId`. Cosmos DB users typically partition on `sessionId` and stamp each entry with a monotonically increasing sequence number; PostgreSQL users typically use a `(session_id, seq)` composite primary key with an `INSERT ... ON CONFLICT DO NOTHING` for `AppendAsync` idempotency under retry.

> **Idempotency**: `AppendAsync` may be called more than once with the same entries if the activity retries after a crash that occurred between `AppendAsync` and the activity's commit acknowledgement. Use the `CorrelationId` on each `DurableSessionEntry` as a deduplication key, or check for an existing `(sessionId, correlationId)` row before inserting.

---

## Migration Behavior

Existing sessions and new sessions coexist seamlessly across a deployment that flips the worker default. There is no data-migration step.

| Session state at deploy | Behavior after the deploy |
|---|---|
| Workflow started before `opts.HistoryStore` was set, still running | Continues using in-memory history — the `AgentWorkflowInput.UseExternalStoreMode` flag travels with the workflow |
| Workflow started before `opts.HistoryStore` was set, completes naturally | Done — the next message creates a new workflow that reads the current options |
| Workflow started before `opts.HistoryStore` was set, hits continue-as-new | Carries history forward via `CarriedHistory` (in-memory path), as it always has |
| New workflow started after `opts.HistoryStore` is set | Reads the current configuration, uses the store from turn 1 |

This is intentional. In-flight sessions cannot be retroactively migrated to the store without a workflow-replay rewrite of historical events, which Temporal does not support. Letting them complete on the original code path is the only safe option.

If you need to migrate every session immediately — for example, after a security incident — the operational sequence is:

1. Deploy the new build with `opts.HistoryStore` set.
2. Send a `RequestShutdown` signal to all running sessions, OR wait for them to time out via TTL.
3. New sessions started by clients after step 1 use the store from turn 1.

---

## What Changes When Opted In

### `GetHistoryAsync()` workflow query

The `[WorkflowQuery("GetHistory")]` handler still works, but each returned entry has `Messages` set to an empty list. The metadata (`CorrelationId`, `CreatedAt`, `OrchestrationId`) is preserved so callers can still see the shape of the conversation. To retrieve full message content, query your store directly:

```csharp
// Before opting in
var history = await client.GetHistoryAsync(sessionId);
foreach (var entry in history)
    Console.WriteLine(entry.Messages[0].Text);

// After opting in — query the store
var store = host.Services.GetRequiredService<IAgentHistoryStore>();
var entries = await store.LoadAsync(sessionId.WorkflowId);
foreach (var entry in entries)
    Console.WriteLine(entry.Messages[0].Text);
```

This is acceptable for the regulated-workload use case: the same caller that opted into external storage has access to the store and is the appropriate principal to read PII from it.

### Activity-scheduled events

Inspect a workflow's history after opting in (`temporal workflow show -w ta-myagent-...`):

- The `RunDurableAgentStep` activity input (`AgentStepInput`) no longer contains the prior conversation in `AccumulatedMessages` — those messages are stripped at the workflow boundary.
- The activity input still carries the new request entry on the wire — the *current* turn's user message is in the activity payload. Only prior turns are absent.
- On the first step of a turn, the activity loads prior history from the store via `IAgentHistoryStore.LoadAsync` (using `IsFirstStep == true` as the trigger).

If you need *zero* message content in Temporal events, including the current turn, you must additionally redact request content at the caller before sending it to `RunAgentAsync`.

### Continue-as-new

When external history is opted in:

- `CarriedHistory` is set to `null` on the new run's `AgentWorkflowInput`. There is nothing to carry — the store owns it.
- The workflow dispatches a `ReduceHistoryInStoreAsync` activity *before* throwing the continue-as-new exception. That activity loads the history from the store and, if `prior.Count > MaxEntryCount`, calls `IAgentHistoryStore.ReplaceAsync` with the **most recent `MaxEntryCount` entries** (a deterministic tail-trim — `prior.Skip(prior.Count - MaxEntryCount)`). If the store is already within `MaxEntryCount`, the activity is a no-op.
- The activity does **not** apply the user's `TemporalAgentsOptions.DefaultHistoryReducer` (or per-agent `agent.HistoryReducer`) delegate to the external store. The reducer is annotated `[JsonIgnore]` and cannot be transported into a Temporal activity, so the external-store path performs a fixed tail-trim instead. The in-memory `HistoryReducer` delegate continues to work as documented in the [Usage Guide](./usage.md) for the in-memory mode (no `HistoryStore` set); only the external-store path differs.

#### Workaround: custom store-side reduction

If you need richer reduction logic for the external store (summarisation, role-aware pruning, retention-by-CorrelationId, etc.), implement it in one of two places:

1. **Inside `IAgentHistoryStore.ReplaceAsync`** — the activity calls this with the tail-trimmed list, but your implementation is free to ignore the input list, re-load the full history from the underlying store, apply your own reduction strategy, and write the result. This is the simplest path and runs synchronously with continue-as-new.

   ```csharp
   public async Task ReplaceAsync(
       string sessionId,
       IReadOnlyList<DurableSessionEntry> trimmedEntries,
       CancellationToken cancellationToken = default)
   {
       // Ignore the library's tail-trim; apply our own summarisation policy instead.
       var full = await LoadFromBackendAsync(sessionId, cancellationToken);
       var reduced = MyCustomReducer.Apply(full);
       await OverwriteBackendAsync(sessionId, reduced, cancellationToken);
   }
   ```

2. **From a separate background process** — schedule a periodic job (cron, Temporal schedule, etc.) that calls `LoadAsync` + `ReplaceAsync` against your store outside the agent workflow's continue-as-new path. Decoupling reduction from the agent loop avoids tying turn latency to your reducer's cost.

---

## When NOT to Use It

External history adds operational complexity — a database to provision, a schema to maintain, retries and idempotency to think about. The default in-memory path is a perfectly good answer for many workloads.

Skip external history when:

- **Single-tenant demos and prototypes.** The provisioning overhead is larger than the problem.
- **Short conversations without PII.** A customer-support bot that exchanges five turns over a half-hour window has no O(n²) problem worth solving.
- **No regulatory constraint on Temporal's event store.** If your Temporal cluster is already in scope for the same compliance perimeter as the rest of your data plane, moving history out of it gains you nothing.
- **You want a single source of truth.** Temporal's event log is replayable, durable, and operationally well-understood. Splitting state across Temporal and another store doubles the failure modes.

For sessions of fewer than ~50 turns with no PII concerns, the default path is the recommendation.

---

## See Also

- [`samples/MAF/ExternalHistoryStore/`](../../../samples/MAF/ExternalHistoryStore/) — runnable end-to-end sample wiring `IAgentHistoryStore`, an `AIContextProvider`, and a recent-N reduction strategy in one host.

## References

- [Agent Sessions and the Workflow Loop — External History Store](../../architecture/MAF/agent-sessions-and-workflow-loop.md#external-history-store) — architectural diagram and rationale
- [Usage Guide](./usage.md) — full `TemporalAgentsOptions` and `DurableAgentBuilder` reference
- [Session StateBag and Context Providers](../../architecture/MAF/session-statebag-and-context-providers.md) — how `AIContextProvider` integrates with the session loop
- `src/Temporalio.Extensions.Agents/HistoryStore/IAgentHistoryStore.cs` — interface definition
- `src/Temporalio.Extensions.Agents/TemporalAgentsOptions.cs` — `HistoryStore` worker-level factory
- `src/Temporalio.Extensions.Agents/DurableAgentBuilder.cs` — `HistoryStore` per-agent factory

---

_Last updated: 2026-05-06_
