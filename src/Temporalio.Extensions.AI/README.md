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

## Feature Highlights

- Durable multi-turn conversations — full history persisted in workflow state across turns and restarts
- `[WorkflowUpdate]` for synchronous request/response — send a message, get a reply, no polling
- Automatic continue-as-new — long sessions carry history forward to fresh workflow runs
- Durable tool functions — `AIFunction` invocations dispatched as individual Temporal activities
- Durable embeddings — `IEmbeddingGenerator` wrapped for deterministic workflow execution
- History reduction — sliding context window for the LLM while preserving full history durably
- Human-in-the-loop (HITL) — approval gates via `[WorkflowUpdate]` that block until a human responds
- Per-request overrides — timeout and retry policy set per `ChatOptions` call
- OpenTelemetry spans — conversation ID and token counts attached as span attributes

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

### Step 1: Connect the Temporal Client

Use `DurableAIDataConverter.Instance` so that MEAI's polymorphic `AIContent` types round-trip correctly through Temporal's payload converter.

```csharp
var temporalClient = await TemporalClient.ConnectAsync(new("localhost:7233")
{
    DataConverter = DurableAIDataConverter.Instance,
    Namespace = "default"
});

builder.Services.AddSingleton<ITemporalClient>(temporalClient);
```

### Step 2: Register IChatClient and AddDurableAI

Use `AddChatClient` — the idiomatic MEAI extension that returns a `ChatClientBuilder` for chaining middleware, then registers the built pipeline as `IChatClient` in DI:

```csharp
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
builder.Services
    .AddChatClient(innerClient)
    .UseFunctionInvocation()
    .UseDurableExecution(opts => opts.ActivityTimeout = TimeSpan.FromMinutes(5))
    .Build();
```

### Multiple Clients with AddKeyedChatClient

When a single host needs more than one `IChatClient` (e.g., different models for chat vs. routing), use `AddKeyedChatClient`:

```csharp
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
public class MyService([FromKeyedServices("chat")] IChatClient chatClient) { }
```

**Note:** `DurableChatActivities` injects the **unkeyed** `IChatClient`. If you register only keyed clients, the activities will fail to resolve the client. Either keep one unkeyed registration for the activities, or register an additional unkeyed alias:

```csharp
// Register keyed clients for your own services...
builder.Services.AddKeyedChatClient("chat", fastClient).UseFunctionInvocation().Build();

// ...and an unkeyed one for DurableChatActivities:
builder.Services.AddChatClient(fastClient).UseFunctionInvocation().Build();
```

### Step 3: Send a Message

```csharp
var sessionClient = services.GetRequiredService<DurableChatSessionClient>();

var response = await sessionClient.ChatAsync(
    "conversation-123",
    [new ChatMessage(ChatRole.User, "What is the capital of France?")]);

Console.WriteLine(response.Messages[0].Text);
```

Each call to `ChatAsync` appends the user messages to the persistent conversation history and returns the assistant's reply. Subsequent calls with the same `conversationId` continue the same session.

## Per-Request Overrides

Override the activity timeout and retry policy on a per-call basis using `ChatOptions` extension methods:

```csharp
var options = new ChatOptions()
    .WithActivityTimeout(TimeSpan.FromMinutes(10))
    .WithMaxRetryAttempts(5);

var response = await sessionClient.ChatAsync("conversation-123", messages, options);
```

You can also override the heartbeat timeout:

```csharp
var options = new ChatOptions()
    .WithHeartbeatTimeout(TimeSpan.FromMinutes(3));
```

These override the global values set in `DurableExecutionOptions` for that request only.

## Durable Tool Functions

Wrap any `AIFunction` so that its invocation is dispatched as a Temporal activity when called inside a workflow. This gives each tool call independent retry and timeout control.

**Option A — Register globally with `AddDurableTools`:**

```csharp
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

```csharp
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

Use `UseDurableReduction` to apply a sliding context window while preserving the full conversation history in workflow state. This lets you control token costs without losing history durability.

```csharp
builder.Services.AddSingleton<IChatClient>(sp =>
    openAiClient.GetChatClient("gpt-4o-mini")
        .AsBuilder()
        .UseDurableReduction(new MessageCountingChatReducer(20))  // 20-message window to LLM
        .UseDurableExecution()
        .Build());
```

`DurableChatReducer` stores the full unreduced history in workflow state and delegates only the sliding window to the LLM. Outside a workflow it behaves as a transparent pass-through to the inner reducer.

## Human-in-the-Loop

The workflow supports blocking approval gates. From inside a tool, send an approval request — the workflow pauses until a human responds or the approval timeout elapses.

**Request approval from a tool:**

```csharp
// Inside an AIFunction registered as a tool
var request = new DurableApprovalRequest
{
    RequestId = Guid.NewGuid().ToString(),
    FunctionName = "DeleteRecords",
    Description = "Permanently delete 42 customer records. This cannot be undone.",
};

// Resolved from DurableChatSessionClient via DI or passed through tool context
var decision = await sessionClient.SubmitApprovalAsync(conversationId, /* ... */);
```

**From an external system (e.g., an admin dashboard):**

```csharp
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
.AddDurableAI(opts =>
{
    opts.ApprovalTimeout = TimeSpan.FromDays(3);
});
```

## Data Converter

`DurableAIDataConverter.Instance` is a `DataConverter` whose JSON serializer uses `AIJsonUtilities.DefaultOptions`. MEAI types such as `ChatMessage` and `AIContent` subtypes use a `$type` discriminator for polymorphic serialization. Without this converter, type information for content subtypes like `FunctionCallContent` and `FunctionResultContent` may be lost when round-tripping through Temporal's workflow history.

Register it on the `TemporalClient` (and ensure the same converter is used by any hosted workers):

```csharp
var client = await TemporalClient.ConnectAsync(new("localhost:7233")
{
    DataConverter = DurableAIDataConverter.Instance
});
```

Or on a hosted worker directly:

```csharp
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
| `DurableChatActivities` | Activity host — executes `IChatClient.GetResponseAsync` on the worker |
| `DurableExecutionOptions` | Configuration — task queue, activity timeout, retry policy, session TTL, approval timeout |
| `DurableAIDataConverter` | Data converter — wraps `AIJsonUtilities.DefaultOptions` for correct MEAI type round-trips |
| `DurableAIFunction` | `DelegatingAIFunction` — dispatches tool calls as Temporal activities when inside a workflow |
| `DurableEmbeddingGenerator` | `DelegatingEmbeddingGenerator` — dispatches embedding generation as Temporal activities |
| `DurableChatReducer` | `IChatReducer` — preserves full history in workflow state while passing a reduced window to the LLM |
| `TemporalChatOptionsExtensions` | Per-request overrides via `ChatOptions` — `WithActivityTimeout`, `WithMaxRetryAttempts`, `WithHeartbeatTimeout` |
| `DurableApprovalRequest` | HITL — request sent to block the workflow pending human review |
| `DurableApprovalDecision` | HITL — human decision that unblocks the workflow |

## License

[MIT](../../LICENSE)
