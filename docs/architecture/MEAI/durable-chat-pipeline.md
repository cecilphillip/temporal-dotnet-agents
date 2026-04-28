# Durable Chat Pipeline Architecture

`Temporalio.Extensions.AI` is a thin middleware layer that wraps MEAI's `IChatClient` abstraction with Temporal's durable execution engine. Each conversation maps to a long-lived Temporal workflow. LLM calls and tool invocations run as Temporal activities — independently retried, checkpointed to durable history, and never re-executed after completion.

This document covers the internal architecture of the pipeline: how the components relate, why the design choices were made, and what guarantees the system provides.

---

## Table of Contents

1. [Component Map](#1-component-map)
2. [Call Flow — A Single Chat Turn](#2-call-flow--a-single-chat-turn)
3. [The `Workflow.InWorkflow` Dispatch Guard](#3-the-workflowinworkflow-dispatch-guard)
4. [`[WorkflowUpdate]` — Why Not Signal + Query?](#4-workflowupdate--why-not-signal--query)
5. [Conversation History Lifecycle](#5-conversation-history-lifecycle)
6. [Turn Serialization](#6-turn-serialization)
7. [`DurableAIDataConverter` — Why It's Required](#7-durableaidataconverter--why-its-required)
8. [`DurableFunctionRegistry` — How Tools Are Resolved](#8-durablefunctionregistry--how-tools-are-resolved)
9. [Streaming Strategy](#9-streaming-strategy)
10. [Observability](#10-observability)
11. [Configuration Reference](#11-configuration-reference)

---

## 1. Component Map

| Component | Kind | Role |
|---|---|---|
| `DurableChatSessionClient` | External entry point | Starts or reuses the session workflow; sends chat turns as `[WorkflowUpdate]`; exposes history query and HITL methods to external callers |
| `DurableChatWorkflow` | `[Workflow]` | Long-lived durable session; accumulates conversation history in workflow state; serializes concurrent turns; handles ContinueAsNew and HITL |
| `DurableChatActivities` | `[Activity]` host | Runs on a worker; calls the real `IChatClient.GetResponseAsync`; emits OTel span |
| `DurableChatClient` | `DelegatingChatClient` middleware | Intercepts `GetResponseAsync` and `GetStreamingResponseAsync`; dispatches as activity when `Workflow.InWorkflow == true`; passes through otherwise |
| `DurableAIFunction` | `DelegatingAIFunction` | Same dispatch guard for tool calls; serializes arguments and dispatches `DurableFunctionActivities.InvokeFunctionAsync` |
| `DurableFunctionActivities` | `[Activity]` host | Receives `DurableFunctionInput` with function name; resolves from `DurableFunctionRegistry`; invokes the real `AIFunction` |
| `DurableEmbeddingGenerator` | `DelegatingEmbeddingGenerator` | Same dispatch guard for `IEmbeddingGenerator.GenerateAsync` |
| `DurableEmbeddingActivities` | `[Activity]` host | Calls the real `IEmbeddingGenerator` on the worker side |
| `DurableFunctionRegistry` | Internal singleton dictionary | Populated at startup by `AddDurableTools`; maps function name to `AIFunction` (case-insensitive) |
| `DurableAIDataConverter` | `DataConverter` | Wraps Temporal's `DefaultPayloadConverter` with `AIJsonUtilities.DefaultOptions` to handle `AIContent` polymorphism |
| `DurableExecutionOptions` | Configuration | `TaskQueue`, `ActivityTimeout`, `HeartbeatTimeout`, `ApprovalTimeout`, `SessionTimeToLive`, `RetryPolicy`, `WorkflowIdPrefix` |

### Middleware Chain (MEAI Builder Pattern)

The middleware components compose via MEAI's `ChatClientBuilder` API:

```csharp
services
    .AddChatClient(innerClient)           // OpenAI / Azure OAI / Ollama
    .UseChatReducer(                       // optional: sliding window for the LLM (stateless reducer)
        new MessageCountingChatReducer(20))
    .UseFunctionInvocation()               // MEAI built-in: calls AIFunction from FunctionCallContent
    .UseDurableExecution()                 // DurableChatClient middleware
    .Build();
```

`UseDurableExecution` inserts `DurableChatClient` into the pipeline nearest to the caller. Because MEAI pipelines are innermost-last, `DurableChatClient` intercepts first: inside a workflow it fires the activity; outside a workflow the entire pipeline (including `UseFunctionInvocation`) runs normally.

---

## 2. Call Flow — A Single Chat Turn

The diagram below traces the complete path from an external caller through to the LLM and back.

```
External Caller (API server, CLI, test)
  │
  │  sessionClient.ChatAsync("conv-123", [new ChatMessage(ChatRole.User, "Hello")])
  │
  ▼
DurableChatSessionClient.ChatAsync
  │  workflowId = "{WorkflowIdPrefix}{conversationId}"   e.g. "chat-conv-123"
  │  span: durable_chat.send  (OTel)
  │
  │  StartWorkflowAsync(DurableChatWorkflow.RunAsync, input,
  │      IdConflictPolicy = UseExisting)      ← no-op if already running
  │
  │  handle = GetWorkflowHandle(workflowId)  ← no pinned RunId
  │              (follows ContinueAsNew chain automatically)
  │
  │  ExecuteUpdateAsync → [WorkflowUpdate("Chat")]
  │      blocks until the workflow handler completes and returns DurableChatOutput
  │
  ▼
DurableChatWorkflow.ChatAsync   [WorkflowUpdate]
  │  ValidateChat() runs first (validator rejects empty messages or shut-down sessions)
  │
  │  WaitConditionAsync(() => !_isProcessing)   ← wait for any concurrent turn to finish
  │  _isProcessing = true
  │
  │  foreach msg in input.Messages → _history.Add(msg)   ← append user turn to history
  │  _turnCount++
  │
  │  activityInput = DurableChatInput
  │      { Messages = [.._history],    ← FULL history sent to activity
  │        Options  = input.Options,
  │        ConversationId = WorkflowId,
  │        TurnNumber = _turnCount }
  │
  │  ExecuteActivityAsync(DurableChatActivities.GetResponseAsync, activityInput,
  │      StartToCloseTimeout = _input.ActivityTimeout,
  │      HeartbeatTimeout    = _input.HeartbeatTimeout)
  │
  ▼
DurableChatActivities.GetResponseAsync   [Activity]
  │  span: durable_chat.turn  (OTel)
  │  ctx.Heartbeat("turn-N")             ← prevents heartbeat timeout during long LLM calls
  │
  │  chatClient.GetResponseAsync(input.Messages, input.Options, ct)
  │      ↓ Workflow.InWorkflow == false here (inside an activity, not a workflow)
  │        → passes through to the real LLM client
  │
  ▼
LLM (OpenAI / Azure OpenAI / Ollama / etc.)
  │
  ◄  ChatResponse
  │
DurableChatActivities
  │  return DurableChatOutput { Response = chatResponse }
  │  (result checkpointed to Temporal event history)
  │
DurableChatWorkflow.ChatAsync  (resumes from ExecuteActivityAsync)
  │  foreach msg in output.Response.Messages → _history.Add(msg)  ← append response turn
  │  _isProcessing = false
  │  return DurableChatOutput
  │
DurableChatSessionClient.ChatAsync  (ExecuteUpdateAsync returns)
  │  span tags: response model, input tokens, output tokens
  │  return output.Response   ← ChatResponse to original caller
  │
External Caller
```

### Crash Recovery

If the worker crashes at any point after `ExecuteActivityAsync` has started, Temporal replays the workflow from history. If the activity completed before the crash, Temporal returns the stored result from history — the LLM is not called again. If the activity had not yet completed, Temporal schedules it on a healthy worker and retries according to the `RetryPolicy`.

---

## 3. The `Workflow.InWorkflow` Dispatch Guard

All middleware components share a single dispatch pattern: check `Workflow.InWorkflow`, dispatch as a Temporal activity when `true`, and pass through to the inner implementation when `false`.

```csharp
// DurableChatClient.GetResponseAsync
public override async Task<ChatResponse> GetResponseAsync(
    IEnumerable<ChatMessage> messages,
    ChatOptions? options = null,
    CancellationToken cancellationToken = default)
{
    if (!Workflow.InWorkflow)
    {
        // Outside a workflow — pass through directly.
        return await base.GetResponseAsync(messages, options, cancellationToken)
            .ConfigureAwait(false);
    }

    // Inside a workflow — dispatch as an activity.
    var input = CreateInput(messages, options);
    var output = await Workflow.ExecuteActivityAsync(
        (DurableChatActivities a) => a.GetResponseAsync(input),
        CreateActivityOptions(options)).ConfigureAwait(false);

    return output.Response;
}
```

`DurableAIFunction` and `DurableEmbeddingGenerator` follow the exact same pattern.

### Why This Matters

Temporal workflows replay from event history when a worker restarts. During replay, workflow code re-executes deterministically: every `await Workflow.ExecuteActivityAsync(...)` call that already has a corresponding `ActivityTaskCompleted` event in history returns the stored result immediately — no network call, no LLM cost. If you called `IChatClient.GetResponseAsync` directly from workflow code, you would make a live LLM call on every replay. Beyond the cost, the response would differ from the original, causing a non-deterministic history mismatch and a workflow failure.

The `Workflow.InWorkflow` guard enforces the correct call path automatically:

| Context | `Workflow.InWorkflow` | What happens |
|---|---|---|
| Inside `[Workflow]` code | `true` | Dispatched as `ExecuteActivityAsync` — durable, retryable, never re-executed after completion |
| Inside `[Activity]` code | `false` | Passes through to inner `IChatClient` — the real LLM call happens here |
| External code (API server, tests) | `false` | Passes through — the pipeline behaves as a plain `IChatClient` |

The same `IChatClient` instance wired up in DI is used in all three contexts. The middleware makes the right call automatically; callers do not need to know whether they are inside a workflow.

---

## 4. `[WorkflowUpdate]` — Why Not Signal + Query?

Temporal provides three primitives for communicating with a running workflow from external code:

- **Signal** — fire-and-forget; no return value; no acknowledgement that the workflow has processed it
- **Query** — reads current workflow state synchronously; cannot trigger side effects or wait for an activity
- **Update** — send a request AND wait for a durable, acknowledged response in one call

A chat turn is inherently a request/response operation: the caller sends messages and needs to wait for the LLM's reply before proceeding. Signal cannot return a response. Query cannot trigger an LLM call. Update is the correct primitive.

`[WorkflowUpdate]` gives additional guarantees beyond simple request/response:

**Validation before history entry.** The `[WorkflowUpdateValidator]` runs before the update is written to workflow history. Validation failures are returned to the caller without modifying history — no side effects, no wasted event records.

```csharp
[WorkflowUpdateValidator(nameof(ChatAsync))]
public void ValidateChat(DurableChatInput input)
{
    if (_shutdownRequested)
        throw new InvalidOperationException("Session has been shut down.");
    if (input?.Messages is null || input.Messages.Count == 0)
        throw new ArgumentException("At least one message is required.");
}
```

**Durability across crashes.** Once an update is accepted (past validation), it is written to history. If the worker crashes after accepting the update but before the handler completes and returns, Temporal replays the workflow on a healthy worker and re-executes the update handler from history. The caller's `ExecuteUpdateAsync` call continues blocking until the response arrives. The caller never sees a lost request.

**Structured response.** The update handler returns `DurableChatOutput` — a typed value carrying the `ChatResponse`. The caller gets a strongly typed result directly from `ExecuteUpdateAsync`, with no polling, no separate query, and no conversion layer.

---

## 5. Conversation History Lifecycle

### Accumulation Per Turn

History is stored as `List<ChatMessage> _history` in the workflow's in-memory state. Each chat update handler appends the incoming user messages, executes the LLM activity with the full history, then appends the response messages:

```
Turn 1:  _history = [User("Hello")]
         → activity receives [User("Hello")]
         → LLM returns Assistant("Hi there!")
         _history = [User("Hello"), Assistant("Hi there!")]

Turn 2:  _history = [User("Hello"), Assistant("Hi there!"), User("Tell me more")]
         → activity receives all 3 messages
         → LLM returns Assistant("Sure, ...")
         _history = [..., User("Tell me more"), Assistant("Sure, ...")]
```

The full history is always sent to the activity. The LLM always has complete context. There is no implicit truncation in the workflow.

### ContinueAsNew — Never Losing History

Temporal's event history has a practical limit of approximately 50,000 events. A long-running conversation will eventually approach this limit. The workflow's `RunAsync` loop monitors `Workflow.ContinueAsNewSuggested`:

```csharp
bool conditionMet = await Workflow.WaitConditionAsync(
    () => _shutdownRequested || (!_isProcessing && Workflow.ContinueAsNewSuggested),
    timeout: ttl);

if (Workflow.ContinueAsNewSuggested && !_shutdownRequested)
{
    var carriedHistory = _history.ToList();
    throw Workflow.CreateContinueAsNewException(
        (DurableChatWorkflow wf) => wf.RunAsync(new DurableChatWorkflowInput
        {
            TimeToLive       = input.TimeToLive,
            CarriedHistory   = carriedHistory,   // ← history carried forward
            ActivityTimeout  = input.ActivityTimeout,
            HeartbeatTimeout = input.HeartbeatTimeout,
            ApprovalTimeout  = input.ApprovalTimeout,
        }));
}
```

`ContinueAsNew` atomically completes the current workflow run and starts a fresh one with the same `workflowId`. The `DurableChatWorkflowInput.CarriedHistory` list is passed as the new run's start input. On startup, `RunAsync` restores from it:

```csharp
if (input.CarriedHistory is { Count: > 0 })
{
    _history.AddRange(input.CarriedHistory);
}
```

From `DurableChatSessionClient`'s perspective this is transparent. The handle is obtained without a pinned `RunId`:

```csharp
var handle = _client.GetWorkflowHandle<DurableChatWorkflow>(workflowId);
```

A handle without a `RunId` follows the continuation chain automatically. `ExecuteUpdateAsync` reaches the current live run regardless of how many `ContinueAsNew` transitions have occurred.

### ContinueAsNew Timing

The condition only fires when `!_isProcessing` — the workflow will never ContinueAsNew in the middle of a turn. A turn in progress completes fully, its results are appended to history, and only then does the workflow observe the suggestion and roll over. This guarantees that the `carriedHistory` snapshot is always consistent.

### History Query

`GetHistory()` is a `[WorkflowQuery]` that reads `_history` synchronously from in-memory workflow state — no activity dispatch, no latency beyond the Temporal RPC:

```csharp
[WorkflowQuery("GetHistory")]
public IReadOnlyList<ChatMessage> GetHistory() => _history;
```

`DurableChatSessionClient.GetHistoryAsync` calls it via `QueryAsync`.

### History Reduction (Optional)

Apply a sliding window for the LLM with a plain stateless `IChatReducer` such as `MessageCountingChatReducer`. The reducer trims what gets sent on each turn; the full conversation log remains in `DurableChatWorkflow._history` and is read via `DurableChatSessionClient.GetHistoryAsync`.

```csharp
// Registration
services
    .AddChatClient(innerClient)
    .UseChatReducer(new MessageCountingChatReducer(20))
    .UseFunctionInvocation()
    .UseDurableExecution()
    .Build();
```

> **Design rationale — full history lives on the workflow, not on middleware.** `DurableChatWorkflow._history` is the single source of truth for full conversation state. It is workflow-local (no leakage across conversations), replay-safe (rebuilt deterministically from Temporal event history), and carried through `ContinueAsNew` transitions. Reducer middleware stays in its proper, stateless role of trimming the message list passed to the LLM on each turn — it never accumulates conversation state of its own.

See [docs/how-to/MEAI/usage.md](../../how-to/MEAI/usage.md) for complete registration examples.

---

## 6. Turn Serialization

A workflow receives incoming updates asynchronously. If two callers both call `sessionClient.ChatAsync` on the same `conversationId` at the same moment, both updates arrive at the workflow nearly simultaneously. Running them concurrently would corrupt history — the second turn would start building its activity input before the first turn's response had been appended.

`DurableChatWorkflow` uses an `_isProcessing` flag with `WaitConditionAsync` as a gate:

```csharp
[WorkflowUpdate("Chat")]
public async Task<DurableChatOutput> ChatAsync(DurableChatInput input)
{
    await Workflow.WaitConditionAsync(() => !_isProcessing);  // wait if busy
    _isProcessing = true;
    try
    {
        // ... append messages, execute activity, append response
    }
    finally
    {
        _isProcessing = false;
    }
}
```

This is not a mutex or a lock in the traditional sense. Temporal workflow code is single-threaded — only one handler runs at a time on the workflow's custom `TaskScheduler`. What `WaitConditionAsync` does is suspend the current handler's coroutine at the `await` point and return control to the workflow event loop, which can then process other incoming events (including the second update arriving). When the first handler sets `_isProcessing = false`, the event loop re-evaluates the condition for the suspended handler and resumes it.

The net result is that turns always execute strictly one at a time, in arrival order, without any external locking. Each turn sees a complete and consistent `_history` snapshot.

---

## 7. `DurableAIDataConverter` — Why It's Required

MEAI's `AIContent` is an abstract base type with multiple subtypes:

- `TextContent` — plain text response
- `FunctionCallContent` — LLM-requested tool invocation (name + arguments + call ID)
- `FunctionResultContent` — tool result (call ID + result)
- `ImageContent`, `DataContent`, `UsageContent`, and others

When these types are serialized to JSON, MEAI's `AIJsonUtilities.DefaultOptions` adds a `"$type"` discriminator field:

```json
{
  "$type": "functionCall",
  "callId": "call_abc123",
  "name": "get_weather",
  "arguments": "{ \"city\": \"London\" }"
}
```

Without this discriminator, a JSON deserializer reading `AIContent[]` has no way to know which concrete type to instantiate. It falls back to the base `AIContent` type, discarding all subtype-specific fields.

Temporal's default `DefaultPayloadConverter` uses `System.Text.Json` with default options — it does not know about `AIJsonUtilities.DefaultOptions` and does not include the polymorphic type resolvers. If you use the default converter, `FunctionCallContent` and `FunctionResultContent` instances in `_history` round-trip through workflow history as bare `AIContent` objects. On the next turn, the full history (including those stripped records) is sent to the LLM as activity input — the function call/result pairs are lost, breaking multi-turn tool use.

`DurableAIDataConverter.Instance` fixes this by constructing Temporal's payload converter with `AIJsonUtilities.DefaultOptions`:

```csharp
public static DataConverter Instance { get; } = new(
    new DefaultPayloadConverter(CreateOptions()),
    new DefaultFailureConverter());

private static JsonSerializerOptions CreateOptions()
{
    var options = new JsonSerializerOptions(AIJsonUtilities.DefaultOptions);
    return options;
}
```

**This converter must be set on both the Temporal client and any workers:**

```csharp
// Client (external caller / API server)
var client = await TemporalClient.ConnectAsync(new("localhost:7233")
{
    DataConverter = DurableAIDataConverter.Instance,
});

// Worker (in hosted worker registration)
services.AddHostedTemporalWorker(opts =>
{
    opts.DataConverter = DurableAIDataConverter.Instance;
});
```

If the converter is set on the worker but not the client (or vice versa), payloads written and read will use different serializers, causing deserialization failures at runtime.

---

## 8. `DurableFunctionRegistry` — How Tools Are Resolved

Tool calls follow the same `Workflow.InWorkflow` dispatch pattern as LLM calls, but involve an extra indirection: the `AIFunction` instance itself cannot cross the workflow-to-activity boundary (it is a live .NET object, not serializable). Instead, `DurableAIFunction` sends only the function's **name** and **arguments** as a `DurableFunctionInput` payload. `DurableFunctionActivities` looks up the function by name from a registry and invokes it on the worker side.

### Phase 1: Startup Registration

`AddDurableTools` registers a configurator delegate for each tool in the DI container:

```csharp
// In AddDurableTools:
foreach (var tool in tools)
{
    services.AddSingleton<Action<DurableFunctionRegistry>>(
        registry => registry.Register(tool));
}
```

When the `DurableFunctionRegistry` singleton is first resolved from DI (which happens when `DurableFunctionActivities` is constructed at worker startup), it runs all configurator delegates:

```csharp
internal sealed class DurableFunctionRegistry : Dictionary<string, AIFunction>, IReadOnlyDictionary<string, AIFunction>
{
    public DurableFunctionRegistry(IEnumerable<Action<DurableFunctionRegistry>>? configurators = null)
        : base(StringComparer.OrdinalIgnoreCase)
    {
        foreach (var configure in configurators ?? [])
            configure(this);
    }
}
```

The dictionary is case-insensitive, so `"get_weather"` and `"Get_Weather"` resolve to the same function.

### Phase 2: Runtime Invocation

When `DurableAIFunction.InvokeCoreAsync` fires inside a workflow, it dispatches:

```csharp
var input = new DurableFunctionInput
{
    FunctionName = Name,
    Arguments    = ConvertArguments(arguments),
};

var output = await Workflow.ExecuteActivityAsync(
    (DurableFunctionActivities a) => a.InvokeFunctionAsync(input),
    activityOptions);
```

`DurableFunctionActivities.InvokeFunctionAsync` then resolves the function by name:

```csharp
if (!functionRegistry.TryGetValue(input.FunctionName, out var function))
{
    throw new InvalidOperationException(
        $"Function '{input.FunctionName}' is not registered in the durable function registry.");
}
var result = await function.InvokeAsync(arguments, ct);
```

Every tool called inside a workflow **must** be registered with `AddDurableTools` before the worker starts. Tools not in the registry cause a hard `InvalidOperationException` at activity execution time.

### Registration Example

```csharp
var weatherTool = AIFunctionFactory.Create(
    (string city) => $"It's sunny in {city}.",
    name: "get_weather");

services
    .AddHostedTemporalWorker("my-task-queue")
    .AddDurableAI()
    .AddDurableTools(weatherTool);
```

See [docs/how-to/MEAI/tool-functions.md](../../how-to/MEAI/tool-functions.md) for the full tool registration and `AsDurable()` guide.

---

## 9. Streaming Strategy

`DurableChatClient.GetStreamingResponseAsync` has a behavioral split based on execution context:

**Outside a workflow** (`Workflow.InWorkflow == false`): the inner client's `GetStreamingResponseAsync` is called directly and tokens are yielded as they arrive. True streaming works normally.

**Inside a workflow** (`Workflow.InWorkflow == true`): true streaming is not possible. Temporal activities return a single result value. The activity executes to completion and returns a full `ChatResponse` payload. `DurableChatClient` then converts that buffered response to a `ChatResponseUpdate` sequence:

```csharp
// Inside a workflow — buffer strategy
var output = await Workflow.ExecuteActivityAsync(
    (DurableChatActivities a) => a.GetResponseAsync(input),
    CreateActivityOptions(options));

// Convert the buffered response to streaming updates.
foreach (var update in output.Response.ToChatResponseUpdates())
{
    yield return update;
}
```

Callers that use `GetStreamingResponseAsync` inside a workflow will see the full response arrive in a burst after the activity completes rather than as a true token stream.

This limitation is fundamental to Temporal's activity execution model, which is request/response. Future approaches for true in-workflow streaming could include sending tokens back via workflow signals from the activity, or using an external token buffer and polling from the workflow — neither is currently implemented.

---

## 10. Observability

The library emits OpenTelemetry spans via `DurableChatTelemetry.ActivitySource` (`"Temporalio.Extensions.AI"`). Temporal's SDK `TracingInterceptor` emits separate spans for the Temporal protocol layer. These compose into a single trace:

```
durable_chat.send                    ← DurableChatTelemetry (conversation.id, model)
  UpdateWorkflow:Chat                ← TracingInterceptor (SDK span)
    RunActivity:GetResponse          ← TracingInterceptor (SDK span)
      durable_chat.turn              ← DurableChatTelemetry (tokens, model)
    RunActivity:InvokeFunction       ← TracingInterceptor (if tool called)
      durable_function.invoke        ← DurableChatTelemetry (tool name, call ID)
```

Register all required sources:

```csharp
Sdk.CreateTracerProviderBuilder()
    .AddSource(
        TracingInterceptor.ClientSource.Name,
        TracingInterceptor.WorkflowsSource.Name,
        TracingInterceptor.ActivitiesSource.Name,
        DurableChatTelemetry.ActivitySourceName)   // "Temporalio.Extensions.AI"
    .AddOtlpExporter()
    .Build();
```

### Span Attributes

| Attribute | Constant | Emitted by |
|---|---|---|
| `conversation.id` | `ConversationIdAttribute` | `durable_chat.send`, `durable_chat.turn` |
| `gen_ai.request.model` | `RequestModelAttribute` | `durable_chat.send` |
| `gen_ai.response.model` | `ResponseModelAttribute` | `durable_chat.send`, `durable_chat.turn` |
| `gen_ai.usage.input_tokens` | `InputTokensAttribute` | `durable_chat.send`, `durable_chat.turn` |
| `gen_ai.usage.output_tokens` | `OutputTokensAttribute` | `durable_chat.send`, `durable_chat.turn` |
| `gen_ai.tool.name` | `ToolNameAttribute` | `durable_function.invoke` |
| `gen_ai.tool.call_id` | `ToolCallIdAttribute` | `durable_function.invoke` |

---

## 11. Configuration Reference

All configuration lives in `DurableExecutionOptions`. `AddDurableAI` binds options to the worker's task queue automatically:

```csharp
services
    .AddHostedTemporalWorker("my-task-queue")
    .AddDurableAI(opts =>
    {
        opts.ActivityTimeout    = TimeSpan.FromMinutes(5);   // default
        opts.HeartbeatTimeout   = TimeSpan.FromMinutes(2);   // default
        opts.ApprovalTimeout    = TimeSpan.FromDays(7);      // default (HITL)
        opts.SessionTimeToLive  = TimeSpan.FromDays(14);     // default
        opts.WorkflowIdPrefix   = "chat-";                   // default
        opts.RetryPolicy        = null;                      // null = Temporal default (unlimited retries)
    });
```

### Per-Request Overrides

`ChatOptions.AdditionalProperties` carries per-request overrides that `DurableChatClient` reads when building `ActivityOptions`:

```csharp
var opts = new ChatOptions()
    .WithActivityTimeout(TimeSpan.FromMinutes(10))    // overrides opts.ActivityTimeout
    .WithMaxRetryAttempts(3)                           // overrides opts.RetryPolicy
    .WithHeartbeatTimeout(TimeSpan.FromMinutes(5));    // overrides opts.HeartbeatTimeout

var response = await sessionClient.ChatAsync("conv-123", messages, opts);
```

The keys are `public const string` on `TemporalChatOptionsExtensions`:
- `"temporal.activity.timeout"` — `ActivityTimeoutKey`
- `"temporal.retry.max_attempts"` — `MaxRetryAttemptsKey`
- `"temporal.heartbeat.timeout"` — `HeartbeatTimeoutKey`

`ChatOptions` is serialized as part of `DurableChatInput` and carried to the activity. `DurableChatClient` strips the non-serializable `RawRepresentationFactory` field before serialization.

### Session Lifecycle

A session workflow starts on the first `ChatAsync` call and runs until one of:

- `SessionTimeToLive` elapses with no active turns (`WaitConditionAsync` timeout fires)
- A `[WorkflowSignal("Shutdown")]` is received — sets `_shutdownRequested = true`, which the `RunAsync` loop observes and exits cleanly

Subsequent `ChatAsync` calls with the same `conversationId` reuse the existing workflow via `WorkflowIdConflictPolicy.UseExisting`.

---

## Related Documents

- [Usage Guide](../../how-to/MEAI/usage.md) — registration, DI setup, first chat call
- [Tool Functions](../../how-to/MEAI/tool-functions.md) — `AddDurableTools`, `AsDurable()`, approval gates
- [Durability and Determinism](../durability-and-determinism.md) — replay guarantees, determinism rules (Agents library; same principles apply here)
