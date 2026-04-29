# History Management and Token Optimization

How conversation history grows in TemporalAgents, how the framework manages it across turns and continue-as-new boundaries, and strategies for controlling token costs.

---

## Table of Contents

1. [Overview](#overview)
2. [How History Grows](#how-history-grows)
3. [History Serialization Format](#history-serialization-format)
4. [Continue-as-New: Automatic History Transfer](#continue-as-new-automatic-history-transfer)
5. [Token Usage Monitoring](#token-usage-monitoring)
6. [Strategies for Reducing Token Costs](#strategies-for-reducing-token-costs)
7. [External Memory with AIContextProvider](#external-memory-with-aicontextprovider)
8. [StateBag Persistence](#statebag-persistence)

---

## Overview

Every agent session in TemporalAgents maintains a conversation history — the full sequence of user messages, assistant responses, tool calls, and tool results. This history is:

1. **Stored in workflow state** (`AgentWorkflow._history`) as a `List<TemporalAgentStateEntry>`
2. **Passed to the activity** on every turn as part of `ExecuteAgentInput.ConversationHistory`
3. **Rebuilt into `ChatMessage` objects** inside `AgentActivities.ExecuteAgentAsync` and sent to the LLM
4. **Carried across continue-as-new boundaries** via `AgentWorkflowInput.CarriedHistory`

This means that by default, the LLM sees the **complete conversation** on every turn. For long-running sessions, this can lead to significant token costs and eventually hit context window limits.

---

## How History Grows

Each turn adds two entries to the history:

```
Turn 1:  [Request₁]  →  [Response₁]                    = 2 entries
Turn 2:  [Request₁] [Response₁] [Request₂]  →  [Response₂]  = 4 entries
Turn 3:  ... = 6 entries
Turn N:  ... = 2N entries
```

The history is serialized as the activity input, so the Temporal event payload grows with each turn. More importantly, the full history is rebuilt and sent to the LLM on every call:

```csharp
// Inside AgentActivities.ExecuteAgentAsync
// Pre-allocate to the total message count to avoid list resizing.
int messageCount = 0;
foreach (var entry in input.ConversationHistory)
    messageCount += entry.Messages.Count;

var allMessages = new List<ChatMessage>(messageCount);
foreach (var entry in input.ConversationHistory)
    foreach (var msg in entry.Messages)
        allMessages.Add(msg.ToChatMessage());
```

**Token cost grows quadratically** with turn count: turn N sends all N previous exchanges plus the new message. A 20-turn conversation sends ~40 messages to the LLM on the final turn.

---

## History Serialization Format

History entries use a polymorphic JSON format with a `$type` discriminator:

```json
[
  {
    "$type": "request",
    "correlationId": "abc123",
    "createdAt": "2026-03-13T10:00:00Z",
    "messages": [
      {
        "role": "user",
        "contents": [{ "$type": "text", "text": "What is the weather?" }]
      }
    ]
  },
  {
    "$type": "response",
    "correlationId": "abc123",
    "createdAt": "2026-03-13T10:00:01Z",
    "messages": [
      {
        "role": "assistant",
        "contents": [{ "$type": "text", "text": "The weather is sunny." }]
      }
    ],
    "usage": {
      "inputTokenCount": 42,
      "outputTokenCount": 8,
      "totalTokenCount": 50
    }
  }
]
```

The serialization captures all content types: text, function calls, function results, data, reasoning, errors, and more. Token usage is preserved per-response for monitoring.

---

## Continue-as-New: Automatic History Transfer

Temporal workflows accumulate event history over time. When the history gets too large, `AgentWorkflow` transfers the conversation to a fresh workflow run via continue-as-new:

```csharp
// Inside AgentWorkflow.RunAsync
if (Workflow.ContinueAsNewSuggested && !_shutdownRequested)
{
    var carriedHistory = _history.ToList();
    var carriedStateBag = _currentStateBag;

    throw Workflow.CreateContinueAsNewException(
        (AgentWorkflow wf) => wf.RunAsync(new AgentWorkflowInput
        {
            AgentName = input.AgentName,
            CarriedHistory = carriedHistory,
            CarriedStateBag = carriedStateBag,
            // ... other config propagated
        }));
}
```

**What survives continue-as-new:**
- Full conversation history (`CarriedHistory`)
- StateBag state (`CarriedStateBag`) — including AIContextProvider state like Mem0 thread IDs
- All configuration: TTL, activity timeouts, approval timeout

**What resets:**
- Temporal event history (the Temporal-level history, not conversation history)
- Run ID (changes to a new value)
- Workflow timers

The conversation is seamless from the user's perspective — the workflow ID stays the same, and the next `RunAsync` call routes to the new run automatically.

---

## Token Usage Monitoring

Token counts are captured at two levels:

### Per-Turn: OTel Span Attributes

The `agent.turn` span includes token metrics from the LLM response:

```
agent.turn
  agent.input_tokens  = 1542
  agent.output_tokens = 87
  agent.total_tokens  = 1629
```

These are only set when the underlying LLM provider reports usage data.

### Per-Turn: Structured Logs

`AgentActivities` logs token counts on each turn:

```
Agent activity completed for 'WeatherAgent' (workflow: ta-weatheragent-abc123).
  Input tokens: 1542, Output tokens: 87, Total tokens: 1629
```

### Per-Response: History State

Token usage is stored in the serialized history as `TemporalAgentStateUsage`, making it available for retrospective analysis via the `GetHistory` workflow query:

```csharp
var handle = client.GetWorkflowHandle<AgentWorkflow>(workflowId);
var history = await handle.QueryAsync(wf => wf.GetHistory());

foreach (var entry in history.OfType<TemporalAgentStateResponse>())
{
    Console.WriteLine($"Turn: {entry.Usage?.TotalTokenCount} tokens");
}
```

### Aggregate: Search Attributes

When `EnableSearchAttributes = true`, the `TurnCount` search attribute lets you find high-activity sessions in the Temporal UI:

```
AgentName = "ResearchAgent" AND TurnCount > 20
```

---

## Strategies for Reducing Token Costs

### 1. Use Short-Lived Sessions

Set a low `timeToLive` so sessions expire before history grows too large:

```csharp
opts.AddAIAgent(agent, timeToLive: TimeSpan.FromHours(1));
```

After TTL expires, the next message starts a fresh session with empty history.

### 2. Summarize History Before Sending

Create a summarization step inside a workflow that condenses long histories before passing to a specialist agent:

```csharp
[WorkflowRun]
public async Task<string> RunAsync(string question)
{
    var researcher = GetAgent("Researcher");
    var session = await researcher.CreateSessionAsync();

    // Multiple turns build up history on the researcher agent
    for (int i = 0; i < 5; i++)
    {
        await researcher.RunAsync($"Research step {i}", session);
    }

    // Summarize the researcher's findings with a fresh agent (no history baggage)
    var summarizer = GetAgent("Summarizer");
    var sumSession = await summarizer.CreateSessionAsync();
    var summary = await summarizer.RunAsync(
        $"Summarize these findings concisely: {lastResponse.Text}",
        sumSession);

    return summary.Text ?? string.Empty;
}
```

The summarizer sees only the final output, not the full 5-turn research history.

### 3. Use External Memory Instead of Full History

`AIContextProvider` implementations (like Mem0) store memories externally and inject only relevant context on each turn. This decouples "what the agent remembers" from "the full conversation transcript":

```csharp
var mem0Provider = new Mem0ContextProvider(mem0Client, userId: "user-001");

var agent = new ChatClientAgent(chatClient, "MemoryAgent")
{
    Instructions = "You are a helpful assistant with long-term memory.",
    ContextProviders = [mem0Provider]
};
```

The provider injects a small set of relevant memories instead of the full history, keeping token counts low even across many turns.

### 4. Use One-Shot Sessions for Independent Tasks

For tasks that don't need conversational context, use a fresh session per request:

```csharp
// Each call starts fresh — no history accumulation
var session = new TemporalAgentSessionId("AnalystAgent", Guid.NewGuid().ToString("N"));
var response = await client.RunAgentAsync(session, new RunRequest("Analyze this data: ..."));
```

Or use `AgentJobWorkflow` via scheduling, which always starts with empty history.

> **Note:** The `RunAgentAsync(string agentName, string message)` convenience overload is deprecated. Use `RunAgentAsync(TemporalAgentSessionId, RunRequest)` directly.

### 5b. Cap History at a Fixed Size with MaxHistorySize

`TemporalAgentsOptions.MaxHistorySize` sets a hard cap on the number of history entries kept in the workflow. When the cap is reached, the workflow triggers continue-as-new, discarding the oldest entries:

```csharp
builder.Services
    .AddHostedTemporalWorker("localhost:7233", "default", "agents")
    .AddTemporalAgents(opts =>
    {
        opts.AddAIAgent(agent);
        opts.MaxHistorySize = 50;  // keep at most 50 entries across continue-as-new
    });
```

Pair with a `HistoryReducer` to control which entries are retained at the boundary:

```csharp
opts.MaxHistorySize = 50;
opts.HistoryReducer = new SummarizingHistoryReducer(summarizerAgent);
```

`HistoryReducer` is called with the full history immediately before continue-as-new. The returned subset becomes the initial history for the new run. A `null` reducer (the default) retains all entries up to `MaxHistorySize`, dropping the oldest.

### 5. Use ResponseFormat to Get Structured Output

Structured output (JSON) is typically more token-efficient than natural language:

```csharp
var report = await agent.RunAsync<WeatherReport>(messages, session);
```

The LLM generates compact JSON instead of verbose prose, reducing output tokens.

### 6. Filter Tools per Request

Disable unnecessary tools to reduce the system prompt size (each tool definition adds tokens):

```csharp
var options = new TemporalAgentRunOptions
{
    EnableToolNames = ["get_weather"],  // only this tool is available
    // EnableToolCalls = false          // or disable all tools
};
```

---

## External Memory with AIContextProvider

For a detailed explanation of how `AIContextProvider` and `AgentSessionStateBag` work, see [Session StateBag & Context Providers](../architecture/MAF/session-statebag-and-context-providers.md).

The key insight for token optimization: providers run inside `AgentActivities.ExecuteAgentAsync` (the activity, not the workflow), so they can make external I/O calls safely. The provider decides what context to inject — it could be a few relevant memories from a vector database rather than the entire conversation history.

---

## StateBag Persistence

`AgentSessionStateBag` carries provider state (like Mem0 thread IDs) across turns without serializing the provider's full data store:

```
Turn 1: Activity starts → empty StateBag
        Provider writes: bag["mem0_thread_id"] = "t-abc"
        Activity ends → bag serialized → workflow stores it

Turn 2: Activity starts → bag restored from workflow state
        Provider reads: bag["mem0_thread_id"] → "t-abc" (skips re-init)
        Activity ends → bag re-serialized

Continue-as-New:
        carriedStateBag = _currentStateBag  → new workflow run
        Bag restored seamlessly in the next turn
```

**Optimization detail:** Empty bags serialize to `null` (checked via `StateBag.Count == 0`), so sessions without providers incur zero serialization overhead.

---

## References

- `src/Temporalio.Extensions.Agents/AgentWorkflow.cs` — history storage and continue-as-new
- `src/Temporalio.Extensions.Agents/AgentActivities.cs` — history rebuild and token logging
- `src/Temporalio.Extensions.Agents/State/` — serialization types for conversation history
- [Session StateBag & Context Providers](../architecture/MAF/session-statebag-and-context-providers.md) — AIContextProvider deep dive
- [Observability](./observability.md) — token usage monitoring via OTel spans
- [Usage Guide](./usage.md) — structured output and tool filtering

---

_Last updated: 2026-03-13_
