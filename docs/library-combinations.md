# Library Combinations Guide

This project ships two libraries — `Temporalio.Extensions.AI` and `Temporalio.Extensions.Agents` — and the choices you make at registration time determine which Temporal primitives back your AI calls, what operational features are available, and what constraints you inherit. There are three meaningful combinations. Two of them are designed happy paths. One of them is a transitional posture that should be migrated away from.

---

## The Three Combinations at a Glance

| | Combination 1 | Combination 2 | Combination 3 |
|---|---|---|---|
| **Stack** | MAF + `Extensions.AI` | MEAI + `Extensions.AI` | MAF + `Extensions.Agents` |
| **Entry point** | `DurableChatSessionClient` | `DurableChatSessionClient` | `ITemporalAgentClient` / `TemporalAIAgentProxy` |
| **Registration** | `AddDurableAI()` | `AddDurableAI()` | `AddTemporalAgents()` |
| **NuGet package** | `Temporalio.Extensions.AI` | `Temporalio.Extensions.AI` | `Temporalio.Extensions.Agents` |
| **`Microsoft.Agents.AI` required** | Yes (but unused by the library) | No | Yes |
| **Named agents** | No | No | Yes |
| **Temporal UI search attributes** | No | No | Yes — `AgentName`, `SessionCreatedAt`, `TurnCount` |
| **LLM-powered routing** | No | No | Yes |
| **Parallel fan-out** | No | No | Yes |
| **StateBag / AIContextProvider** | No | No | Yes |
| **HITL** | Yes | Yes | Yes |
| **Embeddings** | Yes | Yes | No (not wired into `Extensions.Agents`) |
| **Recommended** | Migration path only | Yes | Yes |

---

## Combination 2 — MEAI + `Temporalio.Extensions.AI`

**The designed happy path for `Temporalio.Extensions.AI`.**

`DurableChatWorkflow` wraps an `IChatClient` directly. Every turn becomes a Temporal activity; every conversation becomes a workflow identified by a `conversationId` string you control. No Microsoft Agent Framework is required.

### Registration

```csharp
// Worker + client in the same host (common pattern)
builder.Services
    .AddChatClient(innerClient)
    .UseFunctionInvocation()
    .Build();

builder.Services
    .AddHostedTemporalWorker("localhost:7233", "default", "durable-chat")
    .AddDurableAI(opts =>
    {
        opts.ActivityTimeout   = TimeSpan.FromMinutes(5);
        opts.SessionTimeToLive = TimeSpan.FromHours(24);
    });
```

### Usage

```csharp
var sessionClient = host.Services.GetRequiredService<DurableChatSessionClient>();

var response = await sessionClient.ChatAsync(
    "conv-user-42",
    [new ChatMessage(ChatRole.User, "What is the capital of France?")]);
```

### What you get

- Crash recovery for every LLM call — if the worker restarts mid-activity, Temporal retries and returns the result from history on replay.
- Full conversation history stored in workflow state, surviving restarts and `ContinueAsNew` transitions.
- Tool call durability via `AsDurable()` / `AddDurableTools()` — each tool invocation becomes its own activity with its own retry policy.
- Durable embedding generation via `DurableEmbeddingGenerator`.
- HITL approval gates via `DurableApprovalRequest` / `DurableApprovalDecision`.
- `DurableAIDataConverter` auto-wired when using the managed registration overloads (`AddTemporalClient` + `AddDurableAI`, or the 3-arg `AddHostedTemporalWorker` overload). Manual `TemporalClient.ConnectAsync` callers must set `DataConverter = DurableAIDataConverter.Instance` explicitly.
- Custom workflow output — subclass `DurableChatWorkflowBase<TOutput>` to return domain-specific types from workflow Update handlers. The session loop, history, HITL, and continue-as-new are all inherited. See [custom-workflow-output.md](how-to/MEAI/custom-workflow-output.md).

### Limitations

Conversations are identified by an opaque string ID. There are no named agents, no Temporal UI search attributes, no LLM-powered routing, no `StateBag`, no `AIContextProvider` support, and no parallel orchestration primitives. If your use case needs any of these, move to Combination 3.

---

## Combination 3 — MAF + `Temporalio.Extensions.Agents`

**The designed happy path for `Temporalio.Extensions.Agents`.**

`AgentActivities` wraps an `AIAgent` (from `Microsoft.Agents.AI`) with a full session — structured history, `AgentSessionStateBag`, `AIContextProvider` runs, and agent-semantic OTel spans. Each agent gets its own `AgentWorkflow` instance, identified by name and a session key, with search attributes that make the Temporal Web UI genuinely useful.

`Temporalio.Extensions.Agents` depends on `Temporalio.Extensions.AI` — installing the Agents NuGet package pulls in the AI package automatically.

### Registration

```csharp
var weatherAgent = chatClient.AsAIAgent(
    name: "WeatherAgent",
    description: "Handles weather queries and forecasts.",
    instructions: "You are a weather specialist...");

builder.Services
    .AddHostedTemporalWorker("localhost:7233", "default", "agents")
    .AddTemporalAgents(opts =>
    {
        opts.AddAIAgent(weatherAgent);
    });
```

### External caller usage

```csharp
// Resolved from DI — one proxy per registered agent name
var proxy = services.GetTemporalAgentProxy("WeatherAgent");

var session = await proxy.CreateSessionAsync();
var response = await proxy.RunAsync("Will it rain in Boston tomorrow?", session);
```

### Workflow orchestration usage

```csharp
[Workflow]
public class ResearchWorkflow
{
    [WorkflowRun]
    public async Task<string> RunAsync(string topic)
    {
        var researcher = TemporalWorkflowExtensions.GetAgent("ResearchAgent");
        var session    = await researcher.CreateSessionAsync();
        var result     = await researcher.RunAsync($"Research: {topic}", session);
        return result.Messages[0].Text;
    }
}
```

### What you get

On top of everything in Combination 2:

- **Named agents** — each `AIAgent` is registered under a name; the Temporal workflow ID encodes the agent name and session key (`ta-weatheragent-{key}`).
- **Temporal UI search attributes** — `AgentWorkflow` upserts `AgentName`, `SessionCreatedAt`, and `TurnCount` on every run, enabling queries like `AgentName = "BillingAgent" AND TurnCount > 10` in the Web UI.
- **LLM-powered routing** — `SetRouterAgent` registers `AIModelAgentRouter`, which classifies an incoming message and dispatches it to the right specialist automatically.
- **Parallel fan-out** — `ExecuteAgentsInParallelAsync` dispatches multiple agent calls concurrently inside a `[Workflow]` via `Workflow.WhenAllAsync`.
- **StateBag / `AIContextProvider`** — `AgentSessionStateBag` state is serialized and carried across turns, restarts, and `ContinueAsNew` transitions.
- **`TemporalAgentContext.Current`** inside tools — direct access to the current session and HITL helpers without building a workflow handle manually.
- **Structured output** — `RunAsync<T>` deserializes the agent's response into a typed object, with retry-on-failure.
- **Scheduling** — built-in primitives for recurring and deferred agent runs.
- **Streaming** — `IAgentResponseHandler` for server-sent events or SignalR push.

### Limitations

- Requires `Microsoft.Agents.AI`.
- Custom search attributes (`AgentName`, `SessionCreatedAt`, `TurnCount`) must be pre-registered with the Temporal server before the worker starts. With `temporal server start-dev` this is automatic; on a production cluster it requires a one-time CLI command. Integration tests use `TestEnvironmentHelper.StartLocalAsync()` to handle this.
- Two proxy types exist for the same agent: `TemporalAIAgentProxy` for external callers and `TemporalAIAgent` (via `GetAgent()`) for workflow code. Using the wrong one in the wrong context raises an exception.
- Custom `[Workflow]` classes must follow Temporal determinism rules (`Workflow.UtcNow`, `Workflow.NewGuid()`, no `ActivitySource.StartActivity()` inside workflow code).

---

## Combination 1 — MAF + `Temporalio.Extensions.AI`

**A transitional posture, not a destination.**

This combination uses `AIAgent` / `ChatClientAgent` from `Microsoft.Agents.AI` but registers with `AddDurableAI()` instead of `AddTemporalAgents()`. The result is that `DurableChatWorkflow` injects `IChatClient` and calls `GetResponseAsync` directly — exactly as in Combination 2. The agent's session, `AgentSessionStateBag`, `AIContextProvider` hooks, `AgentResponse`, and `TemporalAgentContext` have no Temporal backing here.

### What happens at runtime

```csharp
// What you register
builder.Services
    .AddChatClient(myAgentAsIChatClient)  // surfacing the AIAgent as a bare IChatClient
    .Build();

builder.Services
    .AddHostedTemporalWorker("localhost:7233", "default", "durable-chat")
    .AddDurableAI();
```

`DurableChatActivities` resolves the unkeyed `IChatClient` from DI and calls `GetResponseAsync`. It does not know anything about `AIAgent`, `AgentSession`, or `AgentSessionStateBag`. The MAF agent runs as a plain chat client — no routing, no StateBag serialization, no search attributes, no `TemporalAgentContext`.

You pay the `Microsoft.Agents.AI` dependency cost and receive exactly Combination 2's capabilities.

### The only valid use

Combination 1 makes sense as a **transitional posture** when migrating an existing MAF project to Temporal before committing to `Temporalio.Extensions.Agents`. It gets the project onto the Temporal execution model quickly, with minimal registration changes, while Combination 3 features are evaluated or ported.

### Exit path

Moving from Combination 1 to Combination 3 is a two-line registration change:

```csharp
// Before (Combination 1)
builder.Services
    .AddChatClient(myAgentAsIChatClient).Build();
builder.Services
    .AddHostedTemporalWorker("localhost:7233", "default", "agents")
    .AddDurableAI();

// After (Combination 3)
builder.Services
    .AddHostedTemporalWorker("localhost:7233", "default", "agents")
    .AddTemporalAgents(opts => opts.AddAIAgent(myAgent));
```

`Temporalio.Extensions.Agents` already depends on `Temporalio.Extensions.AI` transitively — no new NuGet package is needed.

---

## HITL Portability Across Combinations

`DurableApprovalRequest` and `DurableApprovalDecision` are defined in `Temporalio.Extensions.AI` and used by both libraries as the shared wire protocol for approval flows. An external approval system (a UI dashboard, a monitoring tool, an admin API) built against Combination 2 works unchanged with Combination 3 — the approval query and update mechanism is identical.

```csharp
// Works against both DurableChatSessionClient (Combination 2)
// and ITemporalAgentClient (Combination 3)

var pending = await sessionClient.GetPendingApprovalAsync(conversationId);
if (pending is not null)
{
    await sessionClient.SubmitApprovalAsync(conversationId, new DurableApprovalDecision
    {
        RequestId = pending.RequestId,
        Approved  = true,
    });
}
```

The difference is the entry point: `DurableChatSessionClient` in Combination 2, `ITemporalAgentClient` in Combination 3. The types themselves are the same.

---

## Adopting Extensions.AI Incrementally

Some projects build the correct Combination 2 pattern independently — a `[WorkflowUpdate]`-based request/response loop, `WaitConditionAsync` for turn gating, `IChatClient` + `UseFunctionInvocation()` for tools — before encountering these libraries. These projects fit Combination 2 and can adopt `Temporalio.Extensions.AI` selectively rather than wholesale.

Incremental adoption paths:

- **Replace the custom workflow** — swap the hand-rolled workflow and activities for `DurableChatWorkflow` + `DurableChatActivities` by registering `AddDurableAI()` and updating the external entry point to `DurableChatSessionClient`.
- **Adopt specific components** — keep the custom workflow and add `DurableEmbeddingGenerator` + `EmbeddingGeneratorBuilderExtensions.UseDurableExecution()`, `DurableAIDataConverter`, or `UseDurableReduction()` individually. These components are each independently composable and do not require the full workflow replacement.

---

## Which Combination Should I Use?

```
Do you use Microsoft.Agents.AI (AIAgent, ChatClientAgent)?
│
├── No
│   └── Combination 2 — plain IChatClient + AddDurableAI()
│
└── Yes
    │
    ├── Are you migrating an existing MAF project to Temporal
    │   and not yet ready to adopt AddTemporalAgents()?
    │   │
    │   └── Yes → Combination 1 (transitional only)
    │           Plan the move to Combination 3
    │
    └── No (new project, or ready to commit)
        └── Combination 3 — AIAgent + AddTemporalAgents()
```

In short:

- No `Microsoft.Agents.AI` in your project — use Combination 2.
- `Microsoft.Agents.AI` in your project — use Combination 3.
- Already in Combination 1 — migrate to Combination 3; the registration change is two lines.

---

## Further Reading

- [Getting Started — `Temporalio.Extensions.AI`](how-to/MEAI/usage.md)
- [Usage Guide — `Temporalio.Extensions.Agents`](how-to/MAF/usage.md)
- [Tool Functions](how-to/MEAI/tool-functions.md) — Model 1 vs Model 2 tool execution
- [Human-in-the-Loop Patterns (MEAI)](how-to/MEAI/hitl-patterns.md)
- [Human-in-the-Loop Patterns (MAF)](how-to/MAF/hitl-patterns.md)
- [Durable Chat Pipeline Architecture](architecture/MEAI/durable-chat-pipeline.md)
- [Durability and Determinism](architecture/MAF/durability-and-determinism.md)
