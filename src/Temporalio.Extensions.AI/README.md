# Temporalio.Extensions.AI

A [Temporal](https://temporal.io/) integration for [Microsoft.Extensions.AI (MEAI)](https://learn.microsoft.com/en-us/dotnet/ai/ai-extensions). This library makes any plain `IChatClient` durable using Temporal workflows — no Microsoft Agent Framework required.

## Overview

`Temporalio.Extensions.AI` is a lightweight middleware layer that bridges MEAI's `IChatClient` abstraction with Temporal's durable execution engine. Every conversation maps to a long-lived Temporal workflow that persists history across process restarts, worker crashes, and deployments. LLM calls are dispatched as Temporal activities — automatically retried on transient failures and never re-executed after completion.

**How it differs from `Temporalio.Extensions.Agents`:**

| | `Temporalio.Extensions.AI` | `Temporalio.Extensions.Agents` |
|---|---|---|
| Dependency | `Microsoft.Extensions.AI` only | `Microsoft.Agents.AI` (Agent Framework) |
| Entry point | Any `IChatClient` | `AIAgent` / `ChatClientAgent` |
| Routing | Not included | `IAgentRouter` / `AIModelAgentRouter` |
| Complexity | Lower — direct chat pipeline | Higher — full agent session model |

Use this package when you want Temporal durability on top of a standard MEAI pipeline without adopting the full Agent Framework.

> **Using both libraries together?** `Temporalio.Extensions.Agents` takes a package dependency on `Temporalio.Extensions.AI`, so you only need to reference `Temporalio.Extensions.Agents` in your project. The HITL types (`DurableApprovalRequest`, `DurableApprovalDecision`) defined here are also used by Extensions.Agents, and `DurableAIDataConverter` is auto-wired by both `AddDurableAI()` and `AddTemporalAgents()` for the standard registration patterns.

## Feature Highlights

- Durable multi-turn conversations — full history persisted in workflow state across turns and restarts
- Durable tool functions — `AIFunction` invocations dispatched as individual Temporal activities
- Durable embeddings — `IEmbeddingGenerator` wrapped for deterministic workflow execution
- History reduction — chain any `IChatReducer` for a sliding LLM context window; full conversation history is preserved durably by the workflow and read via `GetHistoryAsync`
- Human-in-the-loop (HITL) — approval gates via `[WorkflowUpdate]` that block until a human responds
- Plugin composition — inject OTel tracing, encryption, or any Temporal SDK plugin via `.AddWorkerPlugin()` / `.AddClientPlugin()`
- OpenTelemetry spans — conversation ID and token counts attached as span attributes
- Extensible workflow base class — subclass `DurableChatWorkflowBase<TOutput>` to return domain-specific data from Update handlers while inheriting the full session loop

## How It Works

```
External Caller
    │
    │  ExecuteUpdateAsync (Chat update)
    ▼
DurableChatWorkflow (long-lived workflow)
    │  persists conversation history in workflow state
    │  serializes concurrent turns
    │
    │  ExecuteActivityAsync
    ▼
DurableChatActivities.GetResponseAsync
    │
    └─► IChatClient (e.g., OpenAI, Azure OpenAI, Ollama)
```

The **worker process** hosts `DurableChatWorkflow` and `DurableChatActivities` and executes LLM calls. Any process that holds an `ITemporalClient` — including the worker itself or a completely separate service — can act as the external caller via `DurableChatSessionClient`.

`DurableChatSessionClient` is the external entry point. It starts the workflow if it is not already running (using `UseExisting` conflict policy) and then sends each chat turn as a Temporal Update. The workflow accumulates history and dispatches each LLM call as an activity. When workflow history grows large, continue-as-new transparently transfers history to a new run.

`DurableChatClient` is the middleware that makes this work inside any MEAI pipeline — it detects whether it is running inside a workflow and either dispatches the call as an activity or passes through to the inner client directly.

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download) or later
- A running [Temporal server](https://docs.temporal.io/cli#start-dev) (`temporal server start-dev`)
- An LLM provider (e.g., Azure OpenAI, OpenAI, Ollama)

Install the NuGet package:

```bash
dotnet add package Temporalio.Extensions.AI
```

## Getting Started

> **Deployment note:** Steps 1–3 show a single-process setup (worker and caller in the same host) — the pattern used by all included samples. In production you can split them: the **worker process** needs Steps 1 and 2; a separate **client process** needs only Step 1 (the `ITemporalClient` connection with `DurableAIDataConverter`) and Step 3 (`DurableChatSessionClient.ChatAsync`). `DurableChatSessionClient` requires only an `ITemporalClient` — it does not require a hosted worker.

### Step 1: Connect the Temporal Client

Use `DurableAIDataConverter.Instance` so that MEAI's polymorphic `AIContent` types round-trip correctly through Temporal's payload converter.

This step applies to **both** the worker process and any external caller process.

```csharp
// Both: required in the worker process and in any external caller process
var temporalClient = await TemporalClient.ConnectAsync(new("localhost:7233")
{
    DataConverter = DurableAIDataConverter.Instance,
    Namespace = "default"
});

builder.Services.AddSingleton<ITemporalClient>(temporalClient);
```

### Step 2: Register IChatClient and AddDurableAI

Use `AddChatClient` — the idiomatic MEAI extension that returns a `ChatClientBuilder` for chaining middleware, then registers the built pipeline as `IChatClient` in DI:

This step runs in the **worker process** only. External callers do not register `IChatClient`, `AddHostedTemporalWorker`, or `AddDurableAI`.

```csharp
// Worker
IChatClient innerClient = (IChatClient)new OpenAIClient(apiKey).GetChatClient("gpt-4o-mini");

// AddChatClient registers IChatClient and opens a pipeline builder.
// Chain middleware, then call Build() to seal and register the singleton.
builder.Services
    .AddChatClient(innerClient)
    .UseFunctionInvocation()  // handles tool call loops
    .Build();

// Register the durable AI workflow, activities, and session client on a Temporal worker.
// DurableChatActivities constructor-injects the IChatClient registered above.
builder.Services
    .AddHostedTemporalWorker("my-task-queue")
    .AddDurableAI(opts =>
    {
        opts.ActivityTimeout = TimeSpan.FromMinutes(5);
        opts.SessionTimeToLive = TimeSpan.FromHours(24);
    });
```

`AddDurableAI` registers `DurableChatWorkflow`, `DurableChatActivities`, and `DurableChatSessionClient` automatically. The `DurableChatSessionClient` is available from DI as a singleton.

You can also chain `UseDurableExecution()` directly onto `AddChatClient` if you want the client to dispatch as Temporal activities when used *outside* `DurableChatSessionClient` (e.g., from custom workflow code):

```csharp
// Worker (optional): use when calling IChatClient directly from custom workflow code
builder.Services
    .AddChatClient(innerClient)
    .UseFunctionInvocation()
    .UseDurableExecution(opts => opts.ActivityTimeout = TimeSpan.FromMinutes(5))
    .Build();
```

### Multiple Clients with AddKeyedChatClient

When a single host needs more than one `IChatClient` (e.g., different models for chat vs. routing), use `AddKeyedChatClient`:

This pattern applies to the **worker process** only.

```csharp
// Worker
IChatClient fastClient = (IChatClient)openAiClient.GetChatClient("gpt-4o-mini");
IChatClient powerfulClient = (IChatClient)openAiClient.GetChatClient("gpt-4o");

builder.Services
    .AddKeyedChatClient("chat", fastClient)
    .UseFunctionInvocation()
    .Build();

builder.Services
    .AddKeyedChatClient("routing", powerfulClient)
    .Build();
```

Inject by key with `[FromKeyedServices]`:

```csharp
// Worker
public class MyService([FromKeyedServices("chat")] IChatClient chatClient) { }
```

**Worker-level default key:** Set `DefaultChatClientKey` on `DurableExecutionOptions` so `DurableChatActivities` resolves a specific keyed client — no unkeyed alias required:

```csharp
// Worker
builder.Services
    .AddHostedTemporalWorker("my-task-queue")
    .AddDurableAI(opts =>
    {
        opts.DefaultChatClientKey = "chat";   // resolves AddKeyedChatClient("chat", ...)
    });
```

`DurableChatActivities` uses the following resolution order, from highest to lowest priority:

1. `ChatOptions.WithChatClientKey("key")` — per-call override set by the caller
2. `DurableExecutionOptions.DefaultChatClientKey` — worker-level default
3. Unkeyed `IChatClient` — existing fallback when no key is specified

**Per-call override:** Use `WithChatClientKey` on `ChatOptions` to switch models for a single turn without changing the worker configuration:

```csharp
// Client
var options = new ChatOptions().WithChatClientKey("routing");
var response = await sessionClient.ChatAsync("conversation-123", messages, options);
```

`WithChatClientKey` throws `ArgumentException` on a null or empty key. Overriding back to the unkeyed client via `WithChatClientKey` is not supported in v1 — omit the call to use the worker-level default instead.

### Step 3: Send a Message

This step runs in the **client process** (or in the same process as the worker if co-located). A separate client process needs only `ITemporalClient` and `DurableChatSessionClient` — no hosted worker registration.

In a split deployment, register `DurableChatSessionClient` manually in the client process — `AddDurableAI` is not called there:

```csharp
// Client: in a split deployment, register DurableChatSessionClient manually —
// AddDurableAI() is not called in the client process.
builder.Services.AddSingleton(sp =>
    new DurableChatSessionClient(
        sp.GetRequiredService<ITemporalClient>(),
        new DurableExecutionOptions { TaskQueue = "my-task-queue" },
        sp.GetService<ILogger<DurableChatSessionClient>>()));
```

```csharp
// Client
var sessionClient = services.GetRequiredService<DurableChatSessionClient>();

var response = await sessionClient.ChatAsync(
    "conversation-123",
    [new ChatMessage(ChatRole.User, "What is the capital of France?")]);

Console.WriteLine(response.Messages[0].Text);
```

Each call to `ChatAsync` appends the user messages to the persistent conversation history and returns the assistant's reply. Subsequent calls with the same `conversationId` continue the same session.

## Per-Request Overrides

Override the activity timeout and retry policy on a per-call basis using `ChatOptions` extension methods:

These overrides are passed by the **caller** on each request.

```csharp
// Client
var options = new ChatOptions()
    .WithActivityTimeout(TimeSpan.FromMinutes(10))
    .WithMaxRetryAttempts(5);

var response = await sessionClient.ChatAsync("conversation-123", messages, options);
```

You can also override the heartbeat timeout:

```csharp
// Client
var options = new ChatOptions()
    .WithHeartbeatTimeout(TimeSpan.FromMinutes(3));
```

These override the global values set in `DurableExecutionOptions` for that request only.

## Durable Tool Functions

Wrap any `AIFunction` so that its invocation is dispatched as a Temporal activity when called inside a workflow. This gives each tool call independent retry and timeout control.

**Option A — Register globally with `AddDurableTools`:**

```csharp
// Worker: register tools globally — the worker dispatches each invocation as an activity
var getWeather = AIFunctionFactory.Create(
    (string city) => $"Sunny, 22°C in {city}",
    name: "GetWeather");

builder.Services
    .AddHostedTemporalWorker("my-task-queue")
    .AddDurableAI()
    .AddDurableTools(getWeather);           // params — pass one or more tools
```

**Option B — Wrap inline with `AsDurable()`:**

```csharp
// Client: wrap tools inline per-request; AsDurable() is a no-op outside a workflow
var tools = new[]
{
    getWeather.AsDurable(),
    getStock.AsDurable(new DurableExecutionOptions { ActivityTimeout = TimeSpan.FromSeconds(30) }),
};

var options = new ChatOptions { Tools = [.. tools] };
var response = await sessionClient.ChatAsync("conversation-123", messages, options);
```

`AsDurable()` is context-aware: outside a workflow it passes through to the underlying function unchanged.

## Durable Embeddings

Wrap an `IEmbeddingGenerator` for workflow-safe execution using `UseDurableExecution`:

This registration belongs in the **worker process**.

```csharp
// Worker
builder.Services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(sp =>
    new OpenAIEmbeddingGenerator(openAiClient, "text-embedding-3-small")
        .AsBuilder()
        .UseDurableExecution(opts =>
        {
            opts.ActivityTimeout = TimeSpan.FromMinutes(2);
        })
        .Build());
```

When `GenerateAsync` is called inside a Temporal workflow, the call is dispatched as an activity. Outside a workflow it passes through to the inner generator directly.

## History Reduction

Apply a sliding context window with a plain stateless `IChatReducer` — for example MEAI's `MessageCountingChatReducer` — chained onto the inner chat client. The reducer runs inside the activity, so it doesn't need to be replay-safe. The full unreduced history is already preserved by `DurableChatWorkflow` and is accessible via `DurableChatSessionClient.GetHistoryAsync`.

This pipeline registration belongs in the **worker process**.

```csharp
// Worker
builder.Services
    .AddChatClient(openAiClient.GetChatClient("gpt-4o-mini"))
    .UseChatReducer(new MessageCountingChatReducer(20))   // 20-message window to the LLM
    .UseFunctionInvocation()
    .UseDurableExecution()
    .Build();
```

Retrieve the complete conversation history from workflow state at any time:

```csharp
// Client
var history = await sessionClient.GetHistoryAsync("conversation-123");
```

For a full walk-through of the reduction pattern, see [Reducing the LLM Context Window](../../docs/how-to/MEAI/usage.md#reducing-the-llm-context-window).

## Human-in-the-Loop

The workflow supports blocking approval gates. From inside a tool, send an approval request — the workflow pauses until a human responds or the approval timeout elapses.

**Request approval from a tool:**

From inside a tool (running as a Temporal activity on the worker), construct a `DurableApprovalRequest` and send it to the workflow — execution blocks until a human responds or the approval timeout elapses:

```csharp
// Worker (activity context): this code runs inside a Temporal activity on the worker

// Inside an AIFunction registered as a tool
var request = new DurableApprovalRequest
{
    RequestId = Guid.NewGuid().ToString(),
    FunctionName = "DeleteRecords",
    Description = "Permanently delete 42 customer records. This cannot be undone.",
};

// Send the approval request to the workflow and block until a human responds.
// The workflow's RequestApprovalAsync update handler pauses execution until
// SubmitApprovalAsync is called externally with a matching RequestId.
var handle = temporalClient.GetWorkflowHandle(conversationId);
var decision = await handle.ExecuteUpdateAsync<DurableApprovalDecision>(
    "RequestApproval",
    new object[] { request });

if (!decision.Approved)
    throw new InvalidOperationException($"Action rejected: {decision.Reason}");
```

**From an external system (e.g., an admin dashboard):**

The polling and submission calls are made by an **external client** (or any process with access to an `ITemporalClient`).

```csharp
// Client
// Poll for a pending request
var pending = await sessionClient.GetPendingApprovalAsync("conversation-123");
if (pending is not null)
{
    var decision = await sessionClient.SubmitApprovalAsync(
        "conversation-123",
        new DurableApprovalDecision
        {
            RequestId = pending.RequestId,
            Approved = true,
            Reason = "Reviewed and approved by admin."
        });
}
```

The workflow's `RequestApprovalAsync` update blocks on `WaitConditionAsync` until `SubmitApprovalAsync` is called with a matching `RequestId`. If the `ApprovalTimeout` elapses with no response, the decision returns `Approved = false` automatically.

Configure the approval timeout in `DurableExecutionOptions`:

```csharp
// Worker: configure via AddDurableAI on the worker builder
.AddDurableAI(opts =>
{
    opts.ApprovalTimeout = TimeSpan.FromDays(3);
});
```

## Plugin Support

> **Experimental:** These APIs carry the `TAI001` diagnostic. Suppress with `#pragma warning disable TAI001`.

Use `.AddWorkerPlugin()` and `.AddClientPlugin()` to compose any Temporal SDK plugin (OTel tracing, encryption codecs, custom interceptors) with the hosted worker:

Worker plugins apply to the **worker process**; client plugins apply to the Temporal client, which may live in the worker or in a separate caller process.

**Worker plugin on a hosted worker:**

```csharp
// Worker
builder.Services
    .AddHostedTemporalWorker("localhost:7233", "default", "my-queue")
    .AddDurableAI()
    .AddWorkerPlugin(new TracingPlugin());   // ITemporalWorkerPlugin
```

**Client plugin on a hosted worker** (only applies when the worker creates its own client via the 3-arg overload):

```csharp
// Worker
builder.Services
    .AddHostedTemporalWorker("localhost:7233", "default", "my-queue")
    .AddDurableAI()
    .AddClientPlugin(new EncryptionPlugin());   // ITemporalClientPlugin
```

**Client plugin on a standalone client** (no hosted worker):

```csharp
// Client: use this when the caller runs in a separate process with no hosted worker
builder.Services
    .AddTemporalClient("localhost:7233", "default")
    .AddClientPlugin(new EncryptionPlugin());
```

### DurableAIDataConverter auto-wiring

`AddDurableAI()` automatically applies `DurableAIDataConverter` to the Temporal client for the two most common registration patterns:

| Pattern | How it's applied |
|---|---|
| `AddTemporalClient(addr, ns)` | Via `IConfigureOptions<TemporalClientConnectOptions>` |
| `AddHostedTemporalWorker(addr, ns, queue)` | Via `IPostConfigureOptions<TemporalWorkerServiceOptions>` |
| Manual `TemporalClient.ConnectAsync` + `AddSingleton` | **Not** auto-wired — set `DataConverter = DurableAIDataConverter.Instance` explicitly |

## Data Converter

`DurableAIDataConverter.Instance` is a `DataConverter` whose JSON serializer uses `AIJsonUtilities.DefaultOptions`. MEAI types such as `ChatMessage` and `AIContent` subtypes use a `$type` discriminator for polymorphic serialization. Without this converter, type information for content subtypes like `FunctionCallContent` and `FunctionResultContent` may be lost when round-tripping through Temporal's workflow history.

Register it on the `TemporalClient` (and ensure the same converter is used by any hosted workers):

```csharp
// Both: required on any process that communicates with Temporal
var client = await TemporalClient.ConnectAsync(new("localhost:7233")
{
    DataConverter = DurableAIDataConverter.Instance
});
```

Or on a hosted worker directly:

```csharp
// Worker: alternatively, apply on the hosted worker options directly
services.AddHostedTemporalWorker(opts =>
{
    opts.DataConverter = DurableAIDataConverter.Instance;
});
```

## Core Components

| Component | Description |
|-----------|-------------|
| `DurableChatClient` | `DelegatingChatClient` middleware — dispatches `GetResponseAsync` as a Temporal activity when inside a workflow |
| `DurableChatSessionClient` | External entry point — starts or reuses a session workflow and sends chat turns as `[WorkflowUpdate]` |
| `DurableChatWorkflow` | Long-lived workflow — persists conversation history, serializes turns, handles continue-as-new and HITL |
| `DurableChatWorkflowBase<TOutput>` | Abstract base class for custom durable chat workflows with typed Update output |
| `DurableChatActivities` | Activity host — executes `IChatClient.GetResponseAsync` on the worker |
| `DurableExecutionOptions` | Configuration — task queue, activity timeout, retry policy, session TTL, approval timeout |
| `DurableAIDataConverter` | Data converter — wraps `AIJsonUtilities.DefaultOptions` for correct MEAI type round-trips |
| `DurableAIFunction` | `DelegatingAIFunction` — dispatches tool calls as Temporal activities when inside a workflow |
| `DurableEmbeddingGenerator` | `DelegatingEmbeddingGenerator` — dispatches embedding generation as Temporal activities |
| `TemporalChatOptionsExtensions` | Per-request overrides via `ChatOptions` — `WithActivityTimeout`, `WithMaxRetryAttempts`, `WithHeartbeatTimeout`, `WithChatClientKey` |
| `DurableApprovalRequest` | HITL — request sent to block the workflow pending human review |
| `DurableApprovalDecision` | HITL — human decision that unblocks the workflow |

## License

[MIT](../../LICENSE)
