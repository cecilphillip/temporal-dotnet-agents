# TemporalAgentSession, AgentSessionStateBag, and AIContextProvider

This document explains how these three collaborating concepts fit together, why the design works the way it does, and how data flows and is restored across conversation turns.

---

## The Three Collaborators

| Class | Layer | Responsibility |
|---|---|---|
| `TemporalAgentSession` | TemporalAgents | Temporal-specific `AgentSession`; carries `SessionId` and `StateBag`; created fresh per activity |
| `AgentSessionStateBag` | Microsoft Agent Framework | Thread-safe key-value store; holds per-session state for singleton providers |
| `AIContextProvider` | Microsoft Agent Framework | Pluggable component that enriches (or stores) context before/after each LLM call |

They are not a chain — they are **three roles** inside a single execution pipeline. The `TemporalAgentSession` *contains* the `StateBag`; the `AIContextProvider` *reads from and writes to* that `StateBag`.

---

## Why the StateBag Exists

`AIContextProvider` implementations (like `Mem0Provider`) are registered as **singletons**. One instance is shared across every active session in the process. This means a provider cannot store per-session data in its own fields — doing so would mix up state across users.

The `AgentSessionStateBag` is the solution: each `AgentSession` owns its own bag, and each provider uses a unique string key to store its slice of data there.

```
process memory
├── Mem0Provider (singleton, shared)
├── AgentSession for user-A
│     └── StateBag { "Mem0Provider": { UserId:"A", AgentId:"bot" } }
└── AgentSession for user-B
      └── StateBag { "Mem0Provider": { UserId:"B", AgentId:"bot" } }
```

The provider's singleton instance reads the right state by keying into the session's bag — it never holds session-specific data itself.

---

## The AIContextProvider Two-Phase Lifecycle

Every `AIContextProvider` participates in two named hooks, called by `ChatClientAgent` during `RunAsync`:

### Phase 1 — `InvokingAsync` (before the LLM call)

The provider reads state from the `StateBag`, contacts any external service if needed, and **returns additional context** (messages, instructions, tools) that gets merged into the prompt before it's sent to the model.

### Phase 2 — `InvokedAsync` (after the LLM call)

The provider receives the full request + response messages, does any post-processing (store new memories, update state), and writes updated state back into the `StateBag`.

```
agent.RunAsync("What's the weather?", session)
│
├── ChatHistoryProvider.InvokingAsync   ← inject prior conversation
│
├── AIContextProvider.InvokingAsync     ← enrich with external memory/context
│     ├── read StateBag["Mem0Provider"] → get scoping params
│     ├── search external Mem0 service  → get relevant memories
│     └── return injected ChatMessages  → merged into prompt
│
├── IChatClient.GetResponseAsync()      ← LLM call (enriched prompt)
│
├── AIContextProvider.InvokedAsync      ← store results
│     ├── read StateBag["Mem0Provider"] → get scoping params
│     └── POST messages to Mem0 service → store new memories
│
└── return AgentResponse
```

---

## What Gets Stored Where

This is the most important distinction in the whole system:

| Data | Where it lives | Why |
|---|---|---|
| Provider scoping params (e.g. `UserId`, `AgentId`) | `StateBag` | Lightweight; needed to address the external service; must survive across turns |
| Actual memories / extracted facts | External Mem0 service | Heavy; semantically indexed; cross-session and cross-agent |
| Conversation turn history | `AgentWorkflow._history` → Temporal event history | Temporal-native durability; rebuilds context window each turn |
| Raw LLM prompt + response | `TemporalAgentStateEntry` in event history | Used to reconstruct `allMessages` at activity start |

The `StateBag` is **not a memory store** — it is a persistent address book that tells providers where to find data in their respective external services.

---

## Mem0Provider: A Concrete Walkthrough

`Mem0Provider` is a `MessageAIContextProvider` (which extends `AIContextProvider`) that backs long-term memory with the Mem0 API.

### What it stores in the StateBag

```csharp
// This is the ONLY thing in the StateBag for Mem0Provider
public sealed class Mem0Provider.State
{
    public Mem0ProviderScope StorageScope { get; }  // where to write memories
    public Mem0ProviderScope SearchScope  { get; }  // where to search memories
}

// A scope is just scoping identifiers
public class Mem0ProviderScope
{
    public string? UserId        { get; set; }
    public string? AgentId       { get; set; }
    public string? ThreadId      { get; set; }
    public string? ApplicationId { get; set; }
}
```

No messages. No embeddings. Just the parameters that tell the Mem0 API which user's memories to look up.

### InvokingAsync — fetch memories

```csharp
protected override async ValueTask<IEnumerable<ChatMessage>> ProvideMessagesAsync(
    InvokingContext context, CancellationToken ct)
{
    // Read (or initialize) the State from the session's StateBag
    var state = this._sessionState.GetOrInitializeState(context.Session);

    // Use scoping params to search the external Mem0 service
    var memories = await this._client.SearchAsync(
        state.SearchScope.ApplicationId,
        state.SearchScope.AgentId,
        state.SearchScope.ThreadId,
        state.SearchScope.UserId,
        queryText, ct);

    // Return them as injected ChatMessages (merged into the prompt)
    return [new ChatMessage(ChatRole.User,
        $"## Memories\n{string.Join('\n', memories)}")];
}
```

### InvokedAsync — store new messages

```csharp
protected override async ValueTask StoreAIContextAsync(InvokedContext context, CancellationToken ct)
{
    var state = this._sessionState.GetOrInitializeState(context.Session);

    // Write request + response messages to the external Mem0 service
    // Mem0 extracts and stores meaningful facts automatically
    await this.PersistMessagesAsync(state.StorageScope,
        context.RequestMessages.Concat(context.ResponseMessages ?? []), ct);
}
```

### Turn-by-turn: what actually changes in the StateBag

On **Turn 1**, the `stateInitializer` delegate runs — the `State` object is created and written into the bag for the first time. On **Turn 2+**, `GetOrInitializeState` finds it already in the bag and returns it without calling the initializer again.

So the `StateBag` entry for `Mem0Provider` is set once and then **read-only for the rest of the session**. The interesting mutation is happening entirely inside the Mem0 service.

---

## How StateBag Survives Across Turns in TemporalAgents

### The Problem Without Persistence

In Temporal, each activity execution is a separate method call. The `TemporalAgentSession` object is constructed fresh at the start of each activity. Without explicit persistence, the `StateBag` starts empty every time — `GetOrInitializeState` would call the initializer on every single turn, as if the session had just started.

### The Solution: Serialize → Store in Workflow → Restore

The `StateBag` is passed explicitly through three layers as a `JsonElement?`:

```
AgentActivities.ExecuteAgentAsync
  │
  ├── START: TemporalAgentSession.FromStateBag(sessionId, input.SerializedStateBag)
  │          └── if SerializedStateBag is non-null: AgentSessionStateBag.Deserialize(bagEl)
  │          └── if null (first turn):              new AgentSessionStateBag()
  │
  ├── ... AIContextProvider runs, reads/writes the restored StateBag ...
  │
  └── END: session.SerializeStateBag()
           └── if StateBag.Count == 0: returns null  (no wasted bytes)
           └── if StateBag.Count > 0: returns JsonElement via StateBag.Serialize()
```

The serialized `JsonElement?` is wrapped in `ExecuteAgentResult` and returned to `AgentWorkflow`:

```csharp
// AgentWorkflow stores the serialized bag after each turn
_currentStateBag = result.SerializedStateBag;

// AgentWorkflow passes it to the next activity call
var input = new ExecuteAgentInput(
    agentName:           _agentName,
    request:             update,
    conversationHistory: _history,
    serializedStateBag:  _currentStateBag);   // ← restored in next activity
```

### Across Continue-as-New

When Temporal workflow history grows too large, `AgentWorkflow` triggers a continue-as-new. The `StateBag` survives this via `AgentWorkflowInput.CarriedStateBag`:

```csharp
// On continue-as-new
var carriedStateBag = _currentStateBag;

throw Workflow.CreateContinueAsNewException<AgentWorkflow>(
    wf => wf.RunAsync(new AgentWorkflowInput
    {
        AgentName      = _agentName,
        CarriedHistory = trimmedHistory,
        CarriedStateBag = carriedStateBag,   // ← survives workflow restart
        // ...
    }));
```

On the other side, the new workflow run restores it immediately:

```csharp
// AgentWorkflow constructor
_currentStateBag = input.CarriedStateBag;
```

### Full Persistence Diagram

```
Turn 1                   Turn 2                   Continue-as-New       Turn N
─────────────────────    ─────────────────────    ──────────────────    ─────────────────
Activity starts          Activity starts           Workflow restarts     Activity starts
  session = FromStateBag   session = FromStateBag    _currentStateBag      session = FromStateBag
  (null → empty bag)       (bag restored ✓)          → CarriedStateBag     (bag restored ✓)
                                                      → _currentStateBag
Provider runs            Provider runs
  initializer called       bag already has State    Provider runs         Provider runs
  State written to bag     no initializer call      bag still intact      no initializer call

Activity ends            Activity ends
  bag serialized           bag serialized
  → ExecuteAgentResult     → ExecuteAgentResult

Workflow stores          Workflow stores
  _currentStateBag =       _currentStateBag =
  result.SerializedStateBag result.SerializedStateBag
```

---

## Configuration: Wiring It All Together

### Step 1 — Build the agent with an AIContextProvider

`AIContextProvider` instances are attached to a `ChatClientAgent` via `ChatClientAgentOptions`:

```csharp
using var httpClient = new HttpClient
{
    BaseAddress = new Uri("https://api.mem0.ai")
};
httpClient.DefaultRequestHeaders.Authorization =
    new AuthenticationHeaderValue("Token", "<your-api-key>");

var mem0Provider = new Mem0Provider(
    httpClient,
    stateInitializer: session =>
    {
        // Extract the session ID from the TemporalAgentSession
        // This runs once per session (first turn only)
        var sessionId = session?.GetService<TemporalAgentSessionId>();
        return new Mem0Provider.State(
            new Mem0ProviderScope
            {
                AgentId = "weather-bot",
                UserId  = sessionId?.Key ?? "anonymous"
            });
    });

var agent = new ChatClientAgent(
    chatClient,
    new ChatClientAgentOptions
    {
        Name             = "WeatherAgent",
        Instructions     = "You are a helpful weather assistant.",
        AIContextProviders = [mem0Provider]
    });
```

### Step 2 — Register the agent with TemporalAgents

```csharp
builder.Services
    .AddHostedTemporalWorker("localhost:7233", "default", "agents")
    .AddTemporalAgents(options =>
    {
        options.AddAIAgent(
            agent,
            timeToLive: TimeSpan.FromHours(4));
    });
```

Because `Mem0Provider` is attached to the `ChatClientAgent` instance passed to `AddAIAgent`, it is automatically invoked by `ChatClientAgent.RunAsync` inside the activity. No TemporalAgents-specific configuration is needed to enable it — the StateBag persistence happens transparently.

### Step 3 — Call the agent (no special handling needed)

```csharp
// External caller
var proxy = services.GetTemporalAgentProxy("WeatherAgent");
var session = await proxy.CreateSessionAsync();

// Turn 1 — StateBag empty, stateInitializer runs, Mem0 has no memories yet
var r1 = await proxy.RunAsync("I live in Seattle.", session);

// Turn 2 — StateBag restored, Mem0 now has "User lives in Seattle"
var r2 = await proxy.RunAsync("What should I wear tomorrow?", session);
// LLM receives: "## Memories\nUser lives in Seattle" injected into the prompt
```

---

## Calling CreateSessionAsync More Than Once

`CreateSessionAsync` is a **pure, stateless factory** — it builds a new `TemporalAgentSession` in local memory and returns it. The agent holds no reference to the sessions it has created. No Temporal API is called. No workflow is started. Calling it N times creates N independent session objects, each with a unique `WorkflowId`, and nothing else happens until `RunAsync` is called.

```csharp
// All three are just in-memory objects — no Temporal calls yet
var s1 = await proxy.CreateSessionAsync();  // ta-WeatherAgent-a1b2c3...
var s2 = await proxy.CreateSessionAsync();  // ta-WeatherAgent-d4e5f6...
var s3 = await proxy.CreateSessionAsync();  // ta-WeatherAgent-7g8h9i...
```

A workflow only starts when `RunAsync` is called with a given session for the first time. The unused sessions are simply garbage collected — no cleanup needed, no orphaned workflows on the server.

### Behavior differs between the two agent types

#### `TemporalAIAgentProxy` — fully isolated per session

Each session maps to a separate `AgentWorkflow` instance via `WorkflowId`. Two sessions = two independent workflows = completely isolated conversation histories, StateBags, and TTL timers:

```csharp
var proxy = services.GetTemporalAgentProxy("WeatherAgent");
var s1 = await proxy.CreateSessionAsync();
var s2 = await proxy.CreateSessionAsync();

// s1 starts its own AgentWorkflow (ta-WeatherAgent-aaa)
await proxy.RunAsync("I'm planning a ski trip to Whistler.", s1);

// s2 starts a completely separate AgentWorkflow (ta-WeatherAgent-bbb)
await proxy.RunAsync("I need beach recommendations.", s2);

// s1's workflow only knows about ski trips
// s2's workflow only knows about beaches
await proxy.RunAsync("What gear do I need?", s1);  // "ski gear..."
await proxy.RunAsync("What gear do I need?", s2);  // "swimwear, snorkel..."
```

This is the correct way to serve multiple concurrent users from the same agent proxy instance: one session per user conversation, created independently, never shared.

#### `TemporalAIAgentProxy` + external memory: isolated conversation, shared or isolated memories

Conversation history is always isolated per session. Whether the **external memory** (e.g. Mem0) is isolated depends entirely on your `stateInitializer` scoping:

```csharp
// ⚠️ SHARED external memories — both sessions resolve to the same userId
stateInitializer: session => new Mem0Provider.State(
    new Mem0ProviderScope { UserId = "user-alice" })
//  ↑ static value — both s1 and s2 read/write the same Mem0 memories

// ✅ ISOLATED external memories — each session gets its own thread scope
stateInitializer: session => new Mem0Provider.State(
    new Mem0ProviderScope {
        UserId   = "user-alice",
        ThreadId = session?.GetService<TemporalAgentSessionId>()?.Key
        //         ↑ unique per session — different Mem0 memory thread each time
    })
```

Choosing between shared vs. isolated memory scopes is a product decision: shared memories let one session "remember" facts learned in another (good for a single user across multiple conversations), while isolated memories give each session a clean slate.

#### `TemporalAIAgent` — history is on the instance, not the session

This is the critical difference for workflow-internal agents. `TemporalAIAgent.RunCoreAsync` accumulates history in a `_history` field on the **instance itself**, not in the session object. The session parameter is only used to avoid being `null` — it is not used to route or isolate history:

```csharp
// TemporalAIAgent.RunCoreAsync (simplified)
session ??= await CreateSessionAsync(cancellationToken);
// session is never referenced again below this line

_history.Add(request);                              // ← instance field
var input = new ExecuteAgentInput(_agentName, request, [.. _history]);
```

This means calling `CreateSessionAsync` twice on the same `TemporalAIAgent` and using both sessions produces **shared, interleaved history**:

```csharp
// ⚠️ FOOTGUN — do not do this
var agent = GetAgent("WeatherAgent");  // one TemporalAIAgent instance
var s1 = await agent.CreateSessionAsync();
var s2 = await agent.CreateSessionAsync();

await agent.RunAsync("Question A", s1);
// _history: [A_req, A_resp]

await agent.RunAsync("Question B", s2);
// _history: [A_req, A_resp, B_req, B_resp]
//            ↑ s2 turn sees Question A — they are NOT isolated
```

`TemporalAIAgent` is designed for a single conversation thread inside an orchestrating workflow. If you need two truly independent sub-agents, get two separate instances via `GetAgent`:

```csharp
// ✅ CORRECT — two separate TemporalAIAgent instances, each with its own _history
var researchAgent = GetAgent("ResearchAgent");
var summaryAgent  = GetAgent("SummaryAgent");

var researchSession = await researchAgent.CreateSessionAsync();
var summarySession  = await summaryAgent.CreateSessionAsync();

var findings = await researchAgent.RunAsync("Research quantum computing.", researchSession);
var summary  = await summaryAgent.RunAsync(findings.Text!, summarySession);
// Each agent sees only its own conversation — no cross-contamination
```

### Summary table

| Scenario | Conversation history | External memory (e.g. Mem0) |
|---|---|---|
| `TemporalAIAgentProxy`, two sessions | Fully isolated (separate workflows) | Depends on `stateInitializer` scoping |
| `TemporalAIAgentProxy`, same session reused | Shared (same workflow) | Shared |
| `TemporalAIAgent`, two sessions, **same instance** | ⚠️ Shared (same `_history` field) | Depends on `stateInitializer` scoping |
| `TemporalAIAgent`, two sessions, **two instances** | Fully isolated (separate `_history` fields) | Depends on `stateInitializer` scoping |

---

## Multiple Providers and Key Collisions

Multiple `AIContextProvider` instances can be attached to the same agent. Each uses `StateKey` (defaults to the type name) to store independently:

```csharp
new ChatClientAgentOptions
{
    AIContextProviders =
    [
        new Mem0Provider(...),                      // StateKey = "Mem0Provider"
        new TextSearchProvider(...),                // StateKey = "TextSearchProvider"
        new Mem0Provider(...) { StateKey = "mem0-second" }  // custom key for a second instance
    ]
}
```

The bag for this session would contain:

```json
{
  "Mem0Provider":       { "storageScope": { "userId": "u1" }, "searchScope": { "userId": "u1" } },
  "TextSearchProvider": { ... },
  "mem0-second":        { "storageScope": { "userId": "u1", "agentId": "alt" }, ... }
}
```

Each provider reads only its own key — they are fully independent.

---

## Key Invariants to Remember

1. **The StateBag is not a memory store.** It stores configuration/identity state so providers can find external data. The actual data lives elsewhere (Mem0 API, vector DB, etc.).

2. **`stateInitializer` runs exactly once per session.** After the first turn, `GetOrInitializeState` finds the key in the bag and skips initialization. If the bag is lost (e.g., a bug in TemporalAgents serialization), initialization runs again — which is why `stateInitializer` must be idempotent.

3. **An empty bag serializes to `null`.** `TemporalAgentSession.SerializeStateBag()` returns `null` when `StateBag.Count == 0`, avoiding unnecessary bytes in Temporal's event history on turns where no provider has written anything.

4. **The bag survives continue-as-new.** `AgentWorkflowInput.CarriedStateBag` carries it across workflow history resets — providers never need to reinitialize after a long-running session triggers continue-as-new.

5. **Providers run in the activity, not in the workflow.** `ChatClientAgent.RunAsync` is called inside `AgentActivities.ExecuteAgentAsync`. All I/O (external Mem0 calls, LLM calls) is safe here. Never call `RunAsync` directly inside a `[Workflow]` class.

---

## References

- `src/Temporalio.Extensions.Agents/TemporalAgentSession.cs` — `FromStateBag`, `SerializeStateBag`
- `src/Temporalio.Extensions.Agents/AgentActivities.cs` — activity entry point; restore/serialize lifecycle
- `src/Temporalio.Extensions.Agents/AgentWorkflow.cs` — `_currentStateBag`, continue-as-new transfer
- `src/Temporalio.Extensions.Agents/State/ExecuteAgentInput.cs` — `SerializedStateBag` field
- `src/Temporalio.Extensions.Agents/State/AgentWorkflowInput.cs` — `CarriedStateBag` field
- `agent-framework/dotnet/src/Microsoft.Agents.AI.Abstractions/AgentSessionStateBag.cs`
- `agent-framework/dotnet/src/Microsoft.Agents.AI.Abstractions/AIContextProvider.cs`
- `agent-framework/dotnet/src/Microsoft.Agents.AI.Abstractions/ProviderSessionState{TState}.cs`
- `agent-framework/dotnet/src/Microsoft.Agents.AI.Mem0/Mem0Provider.cs`
