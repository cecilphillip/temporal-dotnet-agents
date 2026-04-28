# MEAI Middleware Sample

This sample demonstrates how to use Microsoft.Extensions.AI (MEAI) middleware patterns with Temporal Agents, specifically showing the `UseChatReducer()` pattern for managing LLM context windows.

## Architecture

```
OpenAI ChatClient (plain)
    ↓
Cast to IChatClient
    ↓
AsAIAgent(...)                ← Create durable agent
    ↓
AddTemporalAgents()           ← Register with Temporal worker
    ↓
[Workflow] AgentWorkflow      ← Conversation history owned by workflow state
    ↓
AgentActivities              ← Call the chat client on each turn
```

### Key Pattern

This sample demonstrates the **correct architectural relationship** between MEAI middleware and Temporal Agents:

1. **MEAI Middleware** (like `UseChatReducer()` or `UseFunctionInvocation()`) can be applied to the `IChatClient` before passing it to `AsAIAgent()`
2. **AgentWorkflow** owns the **full conversation history** and persists it across turns (not delegated to activities)
3. The middleware operates at the activity level — each turn through the agent uses the middleware stack you provide
4. History reconstruction after worker crashes is deterministic — history is replayed from workflow state

This is distinct from the `Temporalio.Extensions.AI` library pattern, which uses `UseDurableReduction()` to outsource history management entirely to Temporal activities. With Agents, history stays in workflow state (more efficient for multi-agent scenarios and stateful agent patterns).

## What It Does

The sample:
- Registers a `DocumentAnalyzer` agent that analyzes documents
- Runs a multi-turn conversation over a Temporal workflow
- Demonstrates context window management via the MEAI reducer middleware
- Shows how each turn maintains full conversation context (owned by the workflow)

## Running the Sample

### Prerequisites

1. Start a local Temporal development server:
   ```bash
   temporal server start-dev
   ```

2. Set your OpenAI API key:
   ```bash
   export OPENAI_API_KEY="sk-..."
   ```

### Execute

```bash
dotnet run --project samples/MAF/MEAIMiddleware/MEAIMiddleware.csproj
```

### Expected Output

```
=== Document Analyzer (MEAI Reducer Middleware) ===

User: Please analyze this document abstract: 'Climate change...'

Agent: [Full analysis response from Claude]
...
```

The sample runs 4 turns of conversation, with each turn receiving the full history from the workflow state, but the MEAI reducer limits what the LLM actually sees to stay within token budgets.

## Key Code Sections

**Creating an agent with ChatClient** (Program.cs):
```csharp
// Cast OpenAI ChatClient to IChatClient for extension method
var chatClient = (IChatClient)openAiClient.GetChatClient(model);

// Create agent — the ChatClient can use any MEAI middleware
var agent = chatClient.AsAIAgent(
    name: "DocumentAnalyzer",
    description: "...",
    instructions: "..."
);

// Register with Temporal
builder.Services
    .AddHostedTemporalWorker(...)
    .AddTemporalAgents(options => options.AddAIAgent(agent));
```

**Applying middleware before agent creation** (best practice):
```csharp
// If you want to apply middleware like UseChatReducer:
var chatClient = (IChatClient)openAiClient.GetChatClient(model);
var wrappedClient = chatClient
    .AsBuilder()
    .UseFunctionInvocation()
    .UseChatReducer()
    .Build();

var agent = wrappedClient.AsAIAgent(...);
```

**History ownership**:
- Full history is in `AgentWorkflow._history` (Temporal workflow state)
- Each turn passes the complete history to the activity
- MEAI middleware operates at the activity boundary — it can trim context, invoke tools, etc.

## Related Samples

- **[BasicAgent](../BasicAgent/)** — Minimal agent setup (no middleware)
- **[EvaluatorOptimizer](../EvaluatorOptimizer/)** — Multi-turn conversation pattern
- **[DurableChat](../../MEAI/DurableChat/)** — Using `Temporalio.Extensions.AI` for MEAI-only scenarios (with `UseDurableReduction()`)

## Architecture References

- `docs/architecture/MAF/agent-sessions-and-workflow-loop.md` — How agents maintain state across turns
- `docs/how-to/MEAI/usage.md` — MEAI middleware patterns and composition
