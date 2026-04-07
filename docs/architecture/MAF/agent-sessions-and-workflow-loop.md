# Agent Sessions, the Workflow Loop, and Resilience

This document explains how `TemporalAgentSession` bridges the Microsoft Agent Framework and Temporal, how the agent execution loop works inside `AgentWorkflow`, how `WorkflowUpdate` delivers messages, and how the system handles crashes, heartbeats, and timeouts.

---

## Table of Contents

1. [TemporalAgentSession: Bridging Two Worlds](#temporalagentsession-bridging-two-worlds)
2. [The Agent Loop Inside AgentWorkflow](#the-agent-loop-inside-agentworkflow)
3. [Sending Messages via WorkflowUpdate](#sending-messages-via-workflowupdate)
4. [Crashes, Heartbeats, and Timeouts](#crashes-heartbeats-and-timeouts)

---

## TemporalAgentSession: Bridging Two Worlds

### The Problem

The **Microsoft Agent Framework** (`Microsoft.Agents.AI`) uses an `AgentSession` to track conversation state between turns. Sessions are short-lived, in-memory objects — they have no built-in persistence model.

**Temporal**, on the other hand, models long-lived processes as *workflows*. Every workflow has a globally unique workflow ID and an immutable event history. Workflow state survives process crashes and is replayed deterministically.

The challenge: make a Microsoft Agent Framework session **durable** by tying it to a Temporal workflow, without either framework knowing about the other.

### The Solution: TemporalAgentSessionId

`TemporalAgentSessionId` is a `readonly struct` that encodes a session's identity as a Temporal workflow ID:

```
Format: ta-{agentName}-{key}

Examples:
  ta-weatherassistant-a1b2c3d4e5f6...     (random key, from proxy)
  ta-weatherassistant-7f8a9b0c1d2e...     (deterministic key, from workflow)
```

The struct has two factory methods, and the choice between them is critical for **workflow determinism**:

| Factory | Key Source | Used By | Why |
|---------|-----------|---------|-----|
| `WithRandomKey(agentName)` | `Guid.NewGuid()` | `TemporalAIAgentProxy` (external callers) | External callers run outside workflows — randomness is safe |
| `WithDeterministicKey(agentName, guid)` | `Workflow.NewGuid()` | `TemporalAIAgent` (inside workflows) | Workflow code must be deterministic — `Workflow.NewGuid()` returns the same GUID on replay |

This distinction exists because Temporal replays workflow code from history. If a workflow used `Guid.NewGuid()`, it would generate a *different* GUID on replay, breaking determinism and causing a non-determinism error. `Workflow.NewGuid()` is replay-safe.

### TemporalAgentSession

`TemporalAgentSession` extends the framework's `AgentSession` and wraps a `TemporalAgentSessionId`:

```csharp
public sealed class TemporalAgentSession : AgentSession
{
    public TemporalAgentSessionId SessionId { get; }

    // Service locator pattern — allows agents and tools to discover the session ID
    public override object? GetService(Type serviceType, object? serviceKey = null)
    {
        if (serviceType == typeof(TemporalAgentSessionId))
            return this.SessionId;
        return base.GetService(serviceType, serviceKey);
    }
}
```

**Key behaviors:**

- **Serialization**: Serializes to/from JSON as `{ "sessionId": "ta-name-key", "stateBag": { ... } }`. The workflow ID string is the canonical representation.
- **Implicit conversions**: `TemporalAgentSessionId` converts implicitly to/from `string`, so it can be passed anywhere a workflow ID is expected.
- **ToString**: Returns the workflow ID, making it easy to log and debug.

### How Session Maps to Workflow

The mapping is 1:1:

```
TemporalAgentSession
    └─ SessionId: TemporalAgentSessionId
         └─ WorkflowId: "ta-weatherassistant-a1b2c3d4"
              └─ Maps to: AgentWorkflow instance with this workflow ID
```

When `DefaultTemporalAgentClient` receives a session ID, it calls `StartWorkflowAsync` with `IdConflictPolicy = UseExisting`. This means:
- **First call**: Creates a new `AgentWorkflow` with this workflow ID
- **Subsequent calls**: No-ops — the workflow already exists

The session effectively *is* the workflow. Creating a session doesn't start the workflow; the first `RunAsync` call does.

---

## The Agent Loop Inside AgentWorkflow

### Lifecycle Overview

`AgentWorkflow` is the durable backbone of every agent session. It is a long-lived Temporal workflow that:

1. **Starts** when the first message is sent to an agent session
2. **Waits** for incoming messages (via Update or Signal)
3. **Dispatches** each message to an activity that runs the real AI agent
4. **Accumulates** conversation history as workflow state
5. **Continues-as-new** when history grows too large
6. **Shuts down** when a Shutdown signal arrives or the TTL expires

### The Main Run Loop

```csharp
[WorkflowRun]
public async Task RunAsync(AgentWorkflowInput input)
{
    _input = input;
    _history.AddRange(input.CarriedHistory);      // Restore history from continue-as-new

    TimeSpan ttl = input.TimeToLive ?? TimeSpan.FromDays(14);

    bool conditionMet = await Workflow.WaitConditionAsync(
        () => _shutdownRequested || (!_isProcessing && Workflow.ContinueAsNewSuggested),
        timeout: ttl);

    if (!conditionMet)
        // TTL expired — workflow completes naturally
    else if (Workflow.ContinueAsNewSuggested && !_shutdownRequested)
        // History too large — continue-as-new with carried history
    // else: shutdown was requested — workflow completes
}
```

This is **not** a tight polling loop. `WaitConditionAsync` is an event-driven primitive that parks the workflow until one of these conditions becomes true:

- `_shutdownRequested` — set by the `Shutdown` signal
- `Workflow.ContinueAsNewSuggested` — set by Temporal when history approaches size limits
- The TTL timeout elapses

While the workflow is parked, it is **not consuming compute resources**. It sits in Temporal's persistence layer and only wakes when a message (Update or Signal) arrives.

### The Processing Gate: `_isProcessing`

The workflow serializes concurrent requests with a boolean gate:

```csharp
private bool _isProcessing;

[WorkflowUpdate("Run")]
public async Task<AgentResponse> RunAgentAsync(RunRequest request)
{
    await Workflow.WaitConditionAsync(() => !_isProcessing);   // Wait if busy
    _isProcessing = true;

    try
    {
        // ... execute activity ...
        return response;
    }
    finally
    {
        _isProcessing = false;     // Release the gate
    }
}
```

If two Updates arrive simultaneously, the second one **blocks** on `WaitConditionAsync(() => !_isProcessing)` until the first completes. This ensures:

- Conversation history is appended in order
- The activity receives a consistent snapshot of prior messages
- No race conditions on `_history`

### Conversation History as Workflow State

Every request/response pair is recorded in `_history` as a `TemporalAgentStateEntry`:

```
_history: [
    TemporalAgentStateRequest  { correlationId: "abc", messages: [User: "Hi"] },
    TemporalAgentStateResponse { correlationId: "abc", messages: [Assistant: "Hello!"], usage: {...} },
    TemporalAgentStateRequest  { correlationId: "def", messages: [User: "Weather?"] },
    TemporalAgentStateResponse { correlationId: "def", messages: [Assistant: "It's sunny"], usage: {...} },
]
```

Each entry contains:

| Field | Purpose |
|-------|---------|
| `CorrelationId` | Links a request to its response (GUID, assigned by the caller) |
| `CreatedAt` | Timestamp for ordering |
| `Messages` | Serialized `ChatMessage` objects (user text, assistant text, tool calls, tool results) |
| `Usage` (response only) | Token counts from the LLM |
| `OrchestrationId` (request only) | Workflow ID of the orchestrating workflow, if this was a sub-agent call |

When the activity executes, it receives the **entire history** flattened into a list of `ChatMessage` objects. This gives the LLM full conversational context on every turn:

```csharp
// Inside AgentActivities.ExecuteAgentAsync
int messageCount = 0;
foreach (var entry in input.ConversationHistory)
    messageCount += entry.Messages.Count;

var allMessages = new List<ChatMessage>(messageCount);
foreach (var entry in input.ConversationHistory)
    foreach (var msg in entry.Messages)
        allMessages.Add(msg.ToChatMessage());

// allMessages now contains: [User: "Hi", Assistant: "Hello!", User: "Weather?"]
// The LLM sees the full conversation
```

### Continue-as-New: History Carryover

Temporal workflows have a practical limit on event history size (typically ~50K events). When the history grows large, `Workflow.ContinueAsNewSuggested` becomes true. The workflow then:

1. Snapshots `_history` into a list
2. Throws `Workflow.CreateContinueAsNewException(...)` with the history as input
3. Temporal starts a **new run** of the same workflow ID
4. The new run restores `_history` from `input.CarriedHistory`

From the caller's perspective, nothing changes — the workflow ID is the same, and the conversation continues seamlessly.

---

## Sending Messages via WorkflowUpdate

### Why Updates Instead of Signal + Query

The traditional Temporal pattern for request/response is:

```
Client → Signal(request) → Workflow processes → Client polls Query until result ready
```

This works but requires a polling loop on the client side. `WorkflowUpdate`, introduced in Temporal SDK 1.x, provides a synchronous alternative:

```
Client → Update(request) → Workflow processes → Response returned directly
```

No polling. The caller blocks until the workflow handler returns.

### The Full Message Flow

Here is the complete path a message takes from an external caller to the LLM and back:

```
┌──────────────────────────┐
│   External Caller        │  var response = await proxy.RunAsync("Hello", session);
│   (TemporalAIAgentProxy) │
└───────────┬──────────────┘
            │
            │  1. Builds RunRequest { Messages, CorrelationId, ... }
            │  2. Calls ITemporalAgentClient.RunAgentAsync(sessionId, request)
            ↓
┌──────────────────────────────────────────┐
│   DefaultTemporalAgentClient             │
│                                          │
│   3. StartWorkflowAsync(AgentWorkflow)   │  ← Idempotent: creates or no-ops
│      IdConflictPolicy = UseExisting      │
│                                          │
│   4. GetWorkflowHandle(sessionId)        │  ← Unpinned: follows continue-as-new
│                                          │
│   5. handle.ExecuteUpdateAsync(          │  ← Blocks until handler returns
│        wf => wf.RunAgentAsync(request))  │
└───────────┬──────────────────────────────┘
            │
            ↓
┌──────────────────────────────────────────────────────────────┐
│   AgentWorkflow  [WorkflowUpdate("Run")]                     │
│                                                              │
│   6. await WaitConditionAsync(() => !_isProcessing)          │  ← Serialize
│   7. _isProcessing = true                                    │
│   8. _history.Add(TemporalAgentStateRequest)                 │  ← Record request
│                                                              │
│   9. Workflow.ExecuteActivityAsync(                           │
│        (AgentActivities a) => a.ExecuteAgentAsync(input))    │
│                                                              │
└───────────┬──────────────────────────────────────────────────┘
            │
            ↓
┌──────────────────────────────────────────────────────────────┐
│   AgentActivities.ExecuteAgentAsync  [Activity]              │
│                                                              │
│   10. Resolve real AIAgent from factory dictionary           │
│   11. Parse sessionId from ctx.Info.WorkflowId               │
│   12. Create AgentWorkflowWrapper(realAgent, request, ...)   │
│   13. Rebuild allMessages from ConversationHistory           │
│   14. Set TemporalAgentContext.Current (for tools)           │
│                                                              │
│   15. wrapper.RunStreamingAsync(allMessages, session, ...)   │
│       ├─ AgentWorkflowWrapper applies tool/format filters    │
│       ├─ DelegatingAIAgent delegates to real agent           │
│       └─ Real AIAgent calls IChatClient → LLM inference      │
│                                                              │
│   16. Stream chunks, ctx.Heartbeat(update.Text) on each      │  ← Heartbeat (always)
│       ├─ Without handler: collect into AgentResponse         │
│       └─ With handler: also call OnStreamingResponseUpdateAsync│
│                                                              │
│   18. Return AgentResponse                                   │
└───────────┬──────────────────────────────────────────────────┘
            │
            ↓
┌──────────────────────────────────────────────────────────────┐
│   AgentWorkflow (continued)                                  │
│                                                              │
│   19. _history.Add(TemporalAgentStateResponse)               │  ← Record response
│   20. _isProcessing = false                                  │  ← Release gate
│   21. return response                                        │  ← Update returns
└───────────┬──────────────────────────────────────────────────┘
            │
            ↓
┌──────────────────────────┐
│   External Caller        │  response.Text == "Hello! How can I help?"
└──────────────────────────┘
```

### Fire-and-Forget Path

For cases where the caller does not need the response:

```csharp
await proxy.RunAsync("Do this in the background", session,
    new TemporalAgentRunOptions { IsFireAndForget = true });
```

This uses a `WorkflowSignal` instead of a `WorkflowUpdate`:

```
Client → SignalAsync(RunFireAndForget) → Workflow receives signal
                                        → Kicks off ProcessFireAndForgetAsync as detached task
                                        → Returns immediately (no response to caller)
```

The signal handler starts a background task inside the workflow that follows the same pattern (serialize via `_isProcessing`, execute activity, record history) but with no return value.

---

## Crashes, Heartbeats, and Timeouts

### Architecture Summary for Resilience

```
┌──────────────────────────────────────────────────────────────────┐
│                        TEMPORAL SERVER                           │
│   Persists: workflow event history, timer state, task queues     │
└──────────────────────────┬───────────────────────────────────────┘
                           │
              ┌────────────┴────────────┐
              ↓                         ↓
   ┌──────────────────┐     ┌──────────────────┐
   │   Worker A        │     │   Worker B        │
   │   (running)       │     │   (standby)       │
   │                   │     │                   │
   │   AgentWorkflow   │     │   Can pick up     │
   │   AgentActivities │     │   any workflow    │
   └──────────────────┘     └──────────────────┘
```

The Temporal server is the single source of truth. Workers are stateless executors. Any worker can resume any workflow.

### Timeout Configuration

There are three timeouts that affect agent execution, all configurable via `TemporalAgentsOptions` or `AgentWorkflowInput`:

#### 1. Activity `StartToCloseTimeout` (default: 30 minutes)

```csharp
new ActivityOptions
{
    StartToCloseTimeout = _input.ActivityStartToCloseTimeout ?? TimeSpan.FromMinutes(30),
}
```

**What it controls**: Maximum wall-clock time for a single `ExecuteAgentAsync` activity execution, measured from when the worker starts executing the activity to when it must return a result.

**What happens on timeout**: Temporal marks the activity as failed. The workflow's `ExecuteActivityAsync` call throws a `TimeoutException`. Since there is no retry policy configured by default, the activity is **not** automatically retried — the workflow itself fails.

**When to increase**: If your LLM calls are slow (large context, complex tool chains), or if you use streaming with `IAgentResponseHandler` and the full response takes a long time.

**When to decrease**: If you want faster failure detection for stuck LLM calls.

```csharp
// Configure via options
builder.Services.AddHostedTemporalWorker("task-queue")
    .AddTemporalAgents(opts =>
    {
        opts.ActivityStartToCloseTimeout = TimeSpan.FromMinutes(60);
        opts.AddAIAgent(myAgent);
    });
```

#### 2. Activity `HeartbeatTimeout` (default: 5 minutes)

```csharp
new ActivityOptions
{
    HeartbeatTimeout = _input.ActivityHeartbeatTimeout ?? TimeSpan.FromMinutes(5),
}
```

**What it controls**: Maximum time between consecutive heartbeats. If the activity does not heartbeat within this window, Temporal considers the activity — and by extension, the worker — to be dead.

**How heartbeats are sent**:

Heartbeats are sent on **every streaming chunk regardless of whether an `IAgentResponseHandler` is registered**. The two branches differ only in whether the handler's callback is invoked, not in whether heartbeating occurs:

- **Without `IAgentResponseHandler`**: Each chunk is collected into a list and a heartbeat is fired unconditionally:

```csharp
// Heartbeat on each streamed chunk even when no handler is registered,
// so that long-running LLM calls don't hit the heartbeat timeout.
List<AgentResponseUpdate> collectedUpdates = [];
await foreach (var update in responseStream.WithCancellation(ct))
{
    collectedUpdates.Add(update);
    ctx.Heartbeat(update.Text);    // ← Heartbeat fired on every chunk
}
response = collectedUpdates.ToAgentResponse();
```

- **With `IAgentResponseHandler`**: Every streaming chunk also triggers a heartbeat, and the chunk is additionally forwarded to the handler:

```csharp
async IAsyncEnumerable<AgentResponseUpdate> StreamWithHeartbeat()
{
    await foreach (var update in responseStream)
    {
        updates.Add(update);
        ctx.Heartbeat(update.Text);    // ← Heartbeat fired on every chunk
        yield return update;
    }
}
await responseHandler.OnStreamingResponseUpdateAsync(StreamWithHeartbeat(), ct);
```

**What happens on heartbeat timeout**: Temporal cancels the activity's `CancellationToken` and marks it as timed out. This is the primary mechanism for detecting a dead worker during long LLM calls.

**Key insight**: `HeartbeatTimeout` is always active for agent activities because heartbeats are sent unconditionally on each streaming chunk. Registering an `IAgentResponseHandler` adds real-time streaming delivery to an external consumer — it does not change whether heartbeats are sent.

#### 3. Workflow `TimeToLive` (default: 14 days)

```csharp
TimeSpan ttl = input.TimeToLive ?? TimeSpan.FromDays(14);

bool conditionMet = await Workflow.WaitConditionAsync(
    () => _shutdownRequested || (!_isProcessing && Workflow.ContinueAsNewSuggested),
    timeout: ttl);
```

**What it controls**: How long the workflow stays alive waiting for new messages. This is not a Temporal-enforced timeout — it is the `timeout` parameter to `WaitConditionAsync`.

**What happens when TTL expires**: The wait returns `false`, the workflow logs "TTL expired", and completes normally. The session is done. Any subsequent message to this session ID will start a **new** workflow (because `IdReusePolicy = AllowDuplicate`).

**When to adjust**: Set shorter TTLs for ephemeral sessions (chatbots, one-off queries). Set longer TTLs for persistent agents that should stay alive across days or weeks.

```csharp
opts.AddAIAgent(myAgent, timeToLive: TimeSpan.FromHours(1));
// or
opts.DefaultTimeToLive = TimeSpan.FromDays(7);
```

### Crash Scenarios

#### Scenario A: Worker Crashes During Activity (LLM Call In Progress)

```
AgentWorkflow → ExecuteActivityAsync → AgentActivities running → [WORKER DIES]
```

**Timeline:**

1. Activity is executing (LLM call in progress)
2. Worker process crashes (OOM, hardware failure, deployment)
3. Temporal detects the failure via one of:
   - **HeartbeatTimeout**: Because heartbeats are sent on every streaming chunk, Temporal notices when the window passes with no heartbeat
   - **Worker disconnect**: Temporal detects the worker's gRPC connection dropped
4. Temporal marks the activity task as failed
5. The workflow is now blocked on `ExecuteActivityAsync`, waiting for a result
6. A new worker picks up the workflow task from the task queue
7. The new worker **replays** the workflow from the beginning:
   - All prior completed activities return cached results from history
   - The failed activity is **rescheduled** (new execution attempt)
8. The activity runs again on the new worker (fresh LLM call)
9. If it succeeds, the result is recorded and the workflow continues

**Data loss**: None. The conversation history up to the failed turn is in `_history` (reconstructed during replay from activity results in the event history). The failed turn's request was already appended to `_history` before `ExecuteActivityAsync` was called, but since the activity never completed, the response entry was never added. On retry, the request is re-appended (during replay) and the activity re-executes.

#### Scenario B: Worker Crashes Between Activities (Workflow Code Running)

```
AgentWorkflow: Activity1 ✓ → Activity2 ✓ → [doing workflow logic] → [WORKER DIES]
```

**Timeline:**

1. Activities 1 and 2 completed and their results are in the event history
2. Worker crashes while running workflow code between activity calls
3. New worker picks up the workflow task
4. Replays from the beginning:
   - `ExecuteActivityAsync(Activity1)` → returns cached result (**not re-executed**)
   - `ExecuteActivityAsync(Activity2)` → returns cached result (**not re-executed**)
   - Workflow code continues from where it left off

**Data loss**: None.

#### Scenario C: Worker Crashes During WorkflowUpdate Handler

```
Client waiting on ExecuteUpdateAsync → AgentWorkflow.RunAgentAsync running → [WORKER DIES]
```

**Timeline:**

1. Client is blocking on `handle.ExecuteUpdateAsync(wf => wf.RunAgentAsync(request))`
2. Worker crashes mid-update
3. New worker picks up the workflow, replays, and the update handler re-executes
4. Once the update completes on the new worker, the response is delivered to the waiting client

**Client experience**: The `ExecuteUpdateAsync` call blocks until the update completes (even across worker failures). The client does not need retry logic — Temporal handles the handoff transparently.

**Important caveat**: If the client's own connection to Temporal drops during the wait, the client will need to re-send the update. Since `_isProcessing` serializes updates, this is safe — a duplicate update will simply queue behind the in-progress one.

#### Scenario D: Temporal Server Restarts

If the Temporal server itself restarts:

1. All workflow state is persisted in the server's database (Cassandra, PostgreSQL, MySQL, or SQLite for dev)
2. Workers reconnect automatically
3. Workflows resume from their persisted state
4. No data loss

### Heartbeat Detail: What Gets Sent

On every streaming chunk, the chunk's text is sent as the heartbeat detail — regardless of whether an `IAgentResponseHandler` is registered:

```csharp
ctx.Heartbeat(update.Text);
```

This has two benefits:

1. **Liveness**: Temporal knows the activity is still alive
2. **Progress visibility**: The heartbeat detail is visible in the Temporal UI and via `DescribeWorkflowExecution`, so operators can see the LLM's partial output in real time

### Timeout Interaction Diagram

```
                    0 min          5 min         30 min        14 days
                    │              │              │              │
                    ├──────────────┤              │              │
                    │ Heartbeat    │              │              │
                    │ Timeout      │              │              │
                    │ (5 min)      │              │              │
                    │              │              │              │
                    ├──────────────┴──────────────┤              │
                    │ StartToClose Timeout         │              │
                    │ (30 min)                     │              │
                    │                              │              │
                    ├──────────────────────────────┴──────────────┤
                    │ Workflow TTL                                 │
                    │ (14 days)                                    │
Activity start ─────┘                                              └── Workflow ends

• HeartbeatTimeout: Dead-worker detection during streaming (active unconditionally — fired on every chunk)
• StartToCloseTimeout: Hard limit on any single agent turn
• Workflow TTL: How long the session stays alive between messages
```

### Summary Table

| Timeout | Default | Scope | Detection | Configurable Via |
|---------|---------|-------|-----------|------------------|
| `HeartbeatTimeout` | 5 min | Single activity | Worker death during streaming | `TemporalAgentsOptions.ActivityHeartbeatTimeout` |
| `StartToCloseTimeout` | 30 min | Single activity | Stuck/slow LLM call | `TemporalAgentsOptions.ActivityStartToCloseTimeout` |
| `TimeToLive` | 14 days | Entire workflow | Session inactivity | `TemporalAgentsOptions.DefaultTimeToLive` or per-agent |

| Crash Scenario | Data Loss | Recovery | Automatic? |
|---------------|-----------|----------|------------|
| Worker dies during activity | None | Activity retried on new worker | Yes |
| Worker dies between activities | None | Workflow replayed, cached results returned | Yes |
| Worker dies during update | None | Update re-executes on new worker, client blocks until done | Yes |
| Temporal server restarts | None | Workers reconnect, workflows resume | Yes |
| Client disconnects during update | Possible duplicate request | Client re-sends update; serialized via `_isProcessing` | Manual |

---

## Related Documentation

- [durability-and-determinism.md](./durability-and-determinism.md) — Step-by-step walkthrough of deterministic replay with agent calls
- [CLAUDE.md](../../CLAUDE.md) — Project architecture overview and quick reference
