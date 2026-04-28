# Getting Started with Temporalio.Extensions.AI

`Temporalio.Extensions.AI` makes any `IChatClient` (Microsoft.Extensions.AI / MEAI) durable using Temporal workflows — no Microsoft Agent Framework required. You keep your existing MEAI pipeline and conversation code; the library wraps each LLM call as a Temporal activity and stores conversation history inside a long-running workflow.

The key insight is the mapping between MEAI concepts and Temporal primitives. Each **conversation** becomes a **Temporal workflow** identified by a `conversationId` string you control. Each **LLM call** (a single `GetResponseAsync` invocation) becomes a **Temporal activity** with configurable timeouts, retry policy, and crash recovery. If the worker process crashes mid-call, Temporal retries the activity automatically from where it left off — the conversation history is safe in workflow state.

This is meaningfully different from a plain MEAI pipeline. A raw `IChatClient` call loses all state if the process crashes; the conversation history lives only in the caller's memory. With this library, history survives process restarts, worker redeploys, and network blips. The workflow's event history is the source of truth, and the `DurableChatSessionClient` is the external entry point for sending messages to it.

---

## Prerequisites

- .NET 10 SDK
- A running Temporal server (`temporal server start-dev` starts one on `localhost:7233`)
- An LLM provider — OpenAI, Azure OpenAI, Ollama, or any provider with an MEAI adapter
- The NuGet package:

```bash
dotnet add package Temporalio.Extensions.AI
```

---

## Step 1 — Connect the Temporal Client

```csharp
// Both: required in the worker process and in any external caller process
var temporalClient = await TemporalClient.ConnectAsync(
    new TemporalClientConnectOptions("localhost:7233")
    {
        DataConverter = DurableAIDataConverter.Instance,
        Namespace = "default",
    });

builder.Services.AddSingleton<ITemporalClient>(temporalClient);
```

> **Note:** `DurableAIDataConverter.Instance` is required whenever you use MEAI types in workflow history. MEAI's `AIContent` hierarchy is polymorphic — `TextContent`, `FunctionCallContent`, `FunctionResultContent`, and others all serialize with a `$type` discriminator field that tells the deserializer which concrete type to construct. Temporal's default `DefaultPayloadConverter` uses plain `System.Text.Json` without that discriminator support, so `FunctionCallContent` and `FunctionResultContent` instances round-trip through workflow history as base `AIContent` objects and lose all their data. `DurableAIDataConverter` wraps the payload converter with `AIJsonUtilities.DefaultOptions`, which includes the correct polymorphic type resolvers.
>
> **Auto-wiring:** When using `AddTemporalClient(addr, ns)` or the 3-arg `AddHostedTemporalWorker(addr, ns, queue)` overload, `AddDurableAI()` applies `DurableAIDataConverter` automatically — you do not need to set it manually. The explicit `DataConverter = DurableAIDataConverter.Instance` shown above is only required when creating the client via `TemporalClient.ConnectAsync` and registering it with `AddSingleton<ITemporalClient>`.

---

## Step 2 — Register IChatClient

Use the idiomatic MEAI DI pattern — `AddChatClient` returns a `ChatClientBuilder` for chaining middleware, and `Build()` registers the final `IChatClient` singleton:

```csharp
// Worker
builder.Services
    .AddChatClient(innerClient)
    .UseFunctionInvocation()   // handles tool call loops inside the activity
    .Build();
```

`DurableChatActivities` — the internal activity class that executes LLM calls — resolves the `IChatClient` per-invocation using the following priority order:

1. `ChatOptions.WithChatClientKey("key")` — per-call override set by the caller
2. `DurableExecutionOptions.DefaultChatClientKey` — worker-level default key set in `AddDurableAI`
3. Unkeyed `IChatClient` — existing fallback when no key is specified

> **Note:** If you use `AddKeyedChatClient` to manage multiple LLM clients in one application, set `DefaultChatClientKey` in `AddDurableAI` instead of registering a redundant unkeyed alias:
>
> ```csharp
> // Worker
> builder.Services
>     .AddKeyedChatClient("chat", gptClient)
>     .UseFunctionInvocation()
>     .Build();
>
> builder.Services
>     .AddHostedTemporalWorker("localhost:7233", "default", "durable-chat")
>     .AddDurableAI(opts =>
>     {
>         opts.DefaultChatClientKey = "chat";   // resolves AddKeyedChatClient("chat", ...)
>     });
> ```
>
> To switch to a different client for a single turn without changing the worker configuration, pass a per-call override via `ChatOptions`:
>
> ```csharp
> // Client
> var options = new ChatOptions().WithChatClientKey("routing");
> var response = await sessionClient.ChatAsync(conversationId, messages, options: options);
> ```

---

## Step 3 — Register the Worker

Chain `AddDurableAI` onto the hosted worker builder:

```csharp
// Worker
builder.Services
    .AddHostedTemporalWorker("localhost:7233", "default", "durable-chat")
    .AddDurableAI(opts =>
    {
        opts.ActivityTimeout   = TimeSpan.FromMinutes(5);
        opts.SessionTimeToLive = TimeSpan.FromHours(24);
    });
```

`AddDurableAI` registers everything needed on the worker:

| Registered type | Role |
|---|---|
| `DurableChatWorkflow` | The workflow that owns conversation history and dispatches LLM calls |
| `DurableChatActivities` | The activity that calls `IChatClient.GetResponseAsync` |
| `DurableFunctionActivities` | The activity that resolves and invokes durable tool functions by name |
| `DurableEmbeddingActivities` | The activity that calls `IEmbeddingGenerator.GenerateAsync` |
| `DurableChatSessionClient` | The external entry point injected into your application code |

Nothing else needs to be wired up manually. The `TaskQueue` is automatically read from the worker builder and set on `DurableExecutionOptions`.

---

## Step 4 — Send a Message

Resolve `DurableChatSessionClient` from DI and call `ChatAsync`:

```csharp
// Client
var sessionClient = host.Services.GetRequiredService<DurableChatSessionClient>();

var conversationId = "user-42-session-1";   // any stable string you control

var response = await sessionClient.ChatAsync(
    conversationId,
    [new ChatMessage(ChatRole.User, "What is the capital of France?")]);

Console.WriteLine(response.Text);   // "Paris."
```

`ChatAsync` starts the `DurableChatWorkflow` if it is not already running (using `WorkflowIdConflictPolicy.UseExisting`), then sends the messages via a `[WorkflowUpdate]`. The update blocks until the LLM activity completes and returns the response. If the workflow is already running from a previous turn, the update is routed to the existing instance.

### Multi-turn conversations

Pass the same `conversationId` on every turn. The workflow accumulates history internally across calls:

```csharp
// Client
var conversationId = "user-42-session-1";

var r1 = await sessionClient.ChatAsync(conversationId,
    [new ChatMessage(ChatRole.User, "What is the capital of France?")]);
Console.WriteLine(r1.Text);   // "Paris."

// The workflow already holds the first exchange in its state.
var r2 = await sessionClient.ChatAsync(conversationId,
    [new ChatMessage(ChatRole.User, "What is the population of that city?")]);
Console.WriteLine(r2.Text);   // "Approximately 2.1 million..."
```

Each `ChatAsync` call only needs to send the new message — the workflow maintains the running history and passes the full context to the LLM on every turn.

### Retrieving history

`GetHistoryAsync` sends a Temporal Query to the running workflow and returns every message accumulated so far — user messages, assistant responses, tool calls, and tool results:

```csharp
// Client
var history = await sessionClient.GetHistoryAsync(conversationId);

foreach (var msg in history)
{
    var text = string.Concat(msg.Contents.OfType<TextContent>().Select(c => c.Text));
    Console.WriteLine($"[{msg.Role}] {text}");
}
```

---

## Per-Request Overrides

The extension methods on `ChatOptions` let you override the global `DurableExecutionOptions` for a single turn without changing the worker configuration:

```csharp
// Client
var options = new ChatOptions()
    .WithActivityTimeout(TimeSpan.FromMinutes(10))
    .WithMaxRetryAttempts(5)
    .WithHeartbeatTimeout(TimeSpan.FromMinutes(3));

var response = await sessionClient.ChatAsync(conversationId, messages, options: options);
```

These values are stored in `ChatOptions.AdditionalProperties` under well-known string keys (`temporal.activity.timeout`, `temporal.retry.max_attempts`, `temporal.heartbeat.timeout`). The workflow reads them when scheduling the activity and applies them for that invocation only.

---

## Reducing the LLM Context Window

For long-running sessions the full conversation history can grow large enough to make LLM calls expensive. Chain a plain stateless `IChatReducer` — such as MEAI's `MessageCountingChatReducer` — onto your inner chat client so each turn only sends the LLM a sliding window. The reducer runs inside the Temporal activity, never in workflow context, so it does not need to be replay-safe:

```csharp
// Worker
builder.Services
    .AddChatClient(innerClient)
    .UseChatReducer(new MessageCountingChatReducer(20))   // 20-message window to the LLM
    .UseFunctionInvocation()
    .UseDurableExecution()
    .Build();
```

With this configuration:

- The `DurableChatWorkflow` retains every message ever exchanged in the conversation as part of its event history — that is the durable, replay-safe source of truth.
- The reducer passes only the most recent 20 messages to the LLM on each turn.
- `GetHistoryAsync` still returns the full unreduced log straight from workflow state.

To retrieve the complete conversation at any point, query the workflow:

```csharp
// Client — full, durable history straight from the workflow
var history = await sessionClient.GetHistoryAsync("conversation-123");
```

> **Design rationale:** Full conversation history is kept on the workflow itself (`DurableChatWorkflow._history`), where it is replay-safe and durable via Temporal event history. Reducers are stateless and only shape what is sent to the LLM on each turn — they never own conversation state.

> **Note:** `MessageCountingChatReducer` is provided by the MEAI library (`Microsoft.Extensions.AI`). Any `IChatReducer` implementation works here — token-counting reducers, summarization reducers, etc. — as long as it is stateless or scoped per-call.

---

## Tool Functions

Tools passed via `ChatOptions.Tools` are handled by the `UseFunctionInvocation()` middleware in the existing pipeline. The entire tool call loop (LLM request → tool invocation → LLM request with result) runs inside the single Temporal activity — the tool function executes on the worker process.

For tool functions that need their own durability guarantees — individual retry policies, separate timeouts, or independent crash recovery — the library provides a durable tool model where each tool call becomes its own Temporal activity. See [tool-functions.md](tool-functions.md) for both execution models and guidance on choosing between them.

---

## Session Lifetime

`SessionTimeToLive` (default: 14 days) controls how long a session workflow remains open while idle. After this period without a new `ChatAsync` call, the workflow exits cleanly. If you then call `ChatAsync` with the same `conversationId`, a new workflow starts — history from the completed workflow is not automatically carried over.

When the Temporal event history for a session grows large (Temporal's per-workflow limit), the library triggers `ContinueAsNew` automatically. The conversation history is serialized into the new workflow run's input and restored before the next turn. This is transparent to callers — the same `conversationId` continues to work.

---

## DurableExecutionOptions Reference

| Option | Type | Default | Description |
|---|---|---|---|
| `TaskQueue` | `string?` | _(required)_ | Temporal task queue. Set automatically by `AddDurableAI` from the worker builder. |
| `ActivityTimeout` | `TimeSpan` | 5 minutes | Start-to-close timeout for LLM call activities. |
| `HeartbeatTimeout` | `TimeSpan` | 2 minutes | Heartbeat timeout for LLM call activities. |
| `RetryPolicy` | `RetryPolicy?` | `null` (Temporal defaults) | Retry policy for activities. When null, Temporal's default unlimited retry applies. |
| `SessionTimeToLive` | `TimeSpan` | 14 days | Inactivity period after which the session workflow exits. |
| `ApprovalTimeout` | `TimeSpan` | 7 days | Maximum time to wait for a human to respond to a HITL tool approval request. |
| `WorkflowIdPrefix` | `string` | `"chat-"` | Prefix prepended to `conversationId` when constructing the Temporal workflow ID. |
| `EnableSessionManagement` | `bool` | `false` | When false, middleware wraps individual calls as activities only. When true, session history is managed in the workflow. |

---

## Composing with Temporal SDK Plugins

> **Experimental:** These APIs carry the `TAI001` diagnostic. Suppress with `#pragma warning disable TAI001` at the call site.

`AddDurableAI()` returns the same `ITemporalWorkerServiceOptionsBuilder`, so you can chain `.AddWorkerPlugin()` and `.AddClientPlugin()` to inject any Temporal SDK plugin (OTel tracing, encryption, custom interceptors) into the registration:

**Worker plugin** — e.g., adding distributed tracing via the OpenTelemetry extension:

```csharp
// Worker
#pragma warning disable TAI001
builder.Services
    .AddHostedTemporalWorker("localhost:7233", "default", "my-queue")
    .AddDurableAI()
    .AddWorkerPlugin(new TracingPlugin());
#pragma warning restore TAI001
```

**Client plugin** — only applies when the worker creates its own client (3-arg overload):

```csharp
// Worker
#pragma warning disable TAI001
builder.Services
    .AddHostedTemporalWorker("localhost:7233", "default", "my-queue")
    .AddDurableAI()
    .AddClientPlugin(new EncryptionPlugin());
#pragma warning restore TAI001
```

For client-only registrations that have no hosted worker, use the `OptionsBuilder<TemporalClientConnectOptions>` overload:

```csharp
// Client
#pragma warning disable TAI001
builder.Services
    .AddTemporalClient("localhost:7233", "default")
    .AddClientPlugin(new EncryptionPlugin());
#pragma warning restore TAI001
```

---

## DurableChatSessionClient

`DurableChatSessionClient` is the external entry point for all session interactions. Every method maps to a specific Temporal mechanism on the `DurableChatWorkflow`.

| Method | Temporal mechanism | Purpose |
|--------|--------------------|---------|
| `ChatAsync` | `[WorkflowUpdate]` | Send messages; starts the session workflow if not already running |
| `GetHistoryAsync` | `[WorkflowQuery]` | Retrieve the full conversation history accumulated across all turns |
| `GetPendingApprovalAsync` | `[WorkflowQuery]` | Poll for a blocked HITL approval request; returns `null` if none is pending |
| `SubmitApprovalAsync` | `[WorkflowUpdate]` | Submit a human decision to unblock a pending approval gate |

### ChatAsync

Starts the session workflow on first call using `WorkflowIdConflictPolicy.UseExisting` — subsequent calls with the same `conversationId` reuse the running workflow. Each call delivers messages as a `[WorkflowUpdate]` and blocks until the LLM responds.

```csharp
// Client
var response = await sessionClient.ChatAsync(
    "conv-123",
    [new ChatMessage(ChatRole.User, "Hello")]);
```

### GetHistoryAsync

Returns all `ChatMessage` entries accumulated across every turn in workflow state order. Useful for displaying conversation history or auditing.

```csharp
// Client
var history = await sessionClient.GetHistoryAsync("conv-123");
```

### GetPendingApprovalAsync and SubmitApprovalAsync

Used together to implement human-in-the-loop approval gates. Poll `GetPendingApprovalAsync` until a request appears, then call `SubmitApprovalAsync` with a matching `RequestId` to unblock the workflow.

```csharp
// Client
var pending = await sessionClient.GetPendingApprovalAsync("conv-123");
if (pending is not null)
{
    await sessionClient.SubmitApprovalAsync("conv-123", new DurableApprovalDecision
    {
        RequestId = pending.RequestId,
        Approved  = true,
    });
}
```

See [Human-in-the-Loop patterns](hitl-patterns.md) for the full approval flow.

---

## Runnable Example

The `samples/MEAI/DurableChat/` directory contains a complete working sample that demonstrates multi-turn conversation, tool calls, and history queries against a local Temporal server. Start it with:

```bash
temporal server start-dev          # terminal 1
dotnet run --project samples/MEAI/DurableChat/DurableChat.csproj   # terminal 2
```

Set the OpenAI API key using `dotnet user-secrets`:

```bash
dotnet user-secrets set "OPENAI_API_KEY" "sk-..." --project samples/MEAI/DurableChat
```

Non-sensitive settings (`OPENAI_API_BASE_URL`, `OPENAI_MODEL`) remain in `samples/MEAI/DurableChat/appsettings.json`. Secrets stored via `dotnet user-secrets` are kept in `~/.microsoft/usersecrets/` (outside the repo) and are automatically loaded by `Host.CreateApplicationBuilder()` in the Development environment.

---

## When to Use Extensions.Agents Instead

`Temporalio.Extensions.AI` is the right choice when you need durable `IChatClient` execution without taking a dependency on the Microsoft Agent Framework. It works with any MEAI-compatible provider and adds Temporal durability to a standard chat pipeline with minimal overhead.

Reach for `Temporalio.Extensions.Agents` when your use case calls for any of the following:

- **LLM-powered routing** — automatically dispatching user messages to a specialist agent based on intent
- **Multi-agent orchestration** — running sub-agents from inside a workflow with `GetAgent` and parallel fan-out with `ExecuteAgentsInParallelAsync`
- **The Microsoft Agent Framework model** — building with `AIAgent`, `ChatClientAgent`, `AgentSessionStateBag`, and `AIContextProvider`
- **Scheduled agent runs** — recurring or deferred agent invocations managed by Temporal Schedules

Both libraries share the same HITL types (`DurableApprovalRequest`, `DurableApprovalDecision`) and use the same approval protocol, so an external approval system works against either workflow type.

See [Temporalio.Extensions.Agents Usage Guide](../MAF/usage.md) for the full feature set.
