# Custom Workflow Output with `DurableChatWorkflowBase<TOutput>`

`DurableChatWorkflow` returns a `ChatResponse` from each `[WorkflowUpdate]`. That is the right choice for most applications — it matches what `DurableChatSessionClient` expects and requires no workflow code. But some use cases need something more: a domain-specific type returned atomically from the same Update that drives the LLM turn.

---

## When to Use the Default

`DurableChatWorkflow` with `DurableChatSessionClient` is sufficient when:

- You need multi-turn conversation with history persistence.
- Your tools run inside `UseFunctionInvocation()` and their side effects are visible through existing APIs (a database, a service call, a log).
- You do not need per-turn domain data returned synchronously to the caller.

This is the right starting point. Most applications never need a custom workflow.

---

## When to Use `DurableChatWorkflowBase<TOutput>`

Subclass `DurableChatWorkflowBase<TOutput>` when the return value of each Update must carry **domain-specific data** alongside the assistant response, and that data must be returned atomically — not fetched from a separate query or external system after the turn completes.

Concrete examples:

- A shopping assistant returns `CartAction` records produced by tool calls in the same turn.
- A code generation workflow returns the extracted code blocks alongside the assistant explanation.
- A document processing workflow returns structured entities parsed from the LLM output.
- A safety-critical workflow returns a confidence score or structured audit record alongside the response.

If the caller needs this data synchronously — not via a follow-up query — a custom workflow is the right tool.

---

## The Three-Step Pattern

### Step 1: Define Your Output Type

```csharp
public sealed class ShoppingTurnOutput
{
    public required ChatResponse Response { get; init; }
    public IReadOnlyList<CartAction> CartActions { get; init; } = [];
}
```

Your output type can carry anything that is JSON-serializable. It must always include the `ChatResponse` (or its messages) so the base class can append the assistant's messages to the history.

### Step 2: Subclass `DurableChatWorkflowBase<TOutput>`

Implement the three abstract members and add a `[WorkflowUpdate]` method that delegates to `RunTurnAsync`:

```csharp
[Workflow("CustomWorkflow.ShoppingAssistant")]
public sealed class ShoppingAssistantWorkflow : DurableChatWorkflowBase<ShoppingTurnOutput>
{
    [WorkflowRun]
    public new Task RunAsync(DurableChatWorkflowInput input) => base.RunAsync(input);

    [WorkflowUpdateValidator(nameof(ShopAsync))]
    public void ValidateShop(DurableChatInput input)
    {
        if (IsShutdownRequested)
            throw new InvalidOperationException("Session has been shut down.");
        if (input?.Messages is null || input.Messages.Count == 0)
            throw new ArgumentException("At least one message is required.");
    }

    [WorkflowUpdate("Shop")]
    public async Task<ShoppingTurnOutput> ShopAsync(DurableChatInput input)
    {
        var requestEntry = DurableSessionRequest.FromMessages(input.Messages, input.CorrelationId);
        var (output, _) = await RunTurnAsync(requestEntry, input.Options);
        return output;
    }

    // Wraps the activity output as a DurableSessionResponse for the base class to append
    // to history. The base class calls this once ExecuteTurnAsync completes.
    protected override DurableSessionResponse BuildResponseEntry(
        string correlationId,
        ShoppingTurnOutput output,
        DateTimeOffset createdAt) =>
        DurableSessionResponse.FromChatResponse(correlationId, output.Response, createdAt);

    protected override Task<ShoppingTurnOutput> ExecuteTurnAsync(
        ActivityOptions activityOptions,
        DurableSessionRequest requestEntry,
        ChatOptions? chatOptions)
    {
        // Flatten the conversation (the request entry is already appended to History) into a
        // single message list for the activity input.
        var allMessages = History.SelectMany(e => e.Messages).ToList();
        var activityInput = new DurableChatInput
        {
            Messages = allMessages,
            Options = chatOptions,
            CorrelationId = requestEntry.CorrelationId,
        };
        return Workflow.ExecuteActivityAsync(
            (ShoppingActivities a) => a.GetShoppingResponseAsync(activityInput),
            activityOptions);
    }

    protected override ContinueAsNewException CreateContinueAsNewException(
        DurableChatWorkflowInput input) =>
        Workflow.CreateContinueAsNewException(
            (ShoppingAssistantWorkflow wf) => wf.RunAsync(input));
}
```

Note the `new` keyword on `RunAsync` — it hides the base class's `protected` method to expose a `public` method that Temporal can discover via `[WorkflowRun]`.

### Step 3: Register and Call via Workflow Handle

Register the workflow and its activity class with the worker, then call via the workflow handle rather than `DurableChatSessionClient`:

```csharp
// Worker registration
builder.Services
    .AddHostedTemporalWorker(temporalAddress, "default", "my-queue")
    .AddDurableAI(opts =>
    {
        opts.ActivityTimeout = TimeSpan.FromMinutes(5);
        opts.RegisterDefaultWorkflow = false;  // Skip default workflow; use custom instead
    })
    .AddWorkflow<ShoppingAssistantWorkflow>()
    .AddSingletonActivities<ShoppingActivities>();
```

The `RegisterDefaultWorkflow = false` setting tells `AddDurableAI()` to skip registering `DurableChatWorkflow` and `DurableChatSessionClient` since your custom workflow handles session management instead. All other supporting infrastructure (options, DataConverter, activities, embeddings) is still registered.

```csharp
// Start the workflow
var handle = await temporalClient.StartWorkflowAsync(
    (ShoppingAssistantWorkflow wf) => wf.RunAsync(new DurableChatWorkflowInput
    {
        ActivityTimeout = TimeSpan.FromMinutes(5),
        TimeToLive = TimeSpan.FromHours(1),
    }),
    new WorkflowOptions(workflowId, taskQueue)
    {
        IdConflictPolicy = WorkflowIdConflictPolicy.UseExisting,
    });

// Send a turn and receive the typed output
var output = await handle.ExecuteUpdateAsync<ShoppingTurnOutput>(
    "Shop",
    [new DurableChatInput { Messages = userMessages }]);

Console.WriteLine(output.Response.Messages.Last().Text);
foreach (var action in output.CartActions)
    Console.WriteLine($"[{action.Action}] {action.ProductName}");
```

---

## The Three Abstract Members

### `BuildResponseEntry(correlationId, output, createdAt)`

Wraps the per-turn `TOutput` into a `DurableSessionResponse` that the base class appends to history. The correlation ID matches the request entry that was already appended.

```csharp
protected override DurableSessionResponse BuildResponseEntry(
    string correlationId,
    ShoppingTurnOutput output,
    DateTimeOffset createdAt) =>
    DurableSessionResponse.FromChatResponse(correlationId, output.Response, createdAt);
```

For a `ChatResponse`-based output, the static `DurableSessionResponse.FromChatResponse` factory captures the assistant messages, usage, and timestamp. If your output type does not include the full `ChatResponse`, construct a `DurableSessionResponse` directly from the assistant messages you want preserved.

### `ExecuteTurnAsync(ActivityOptions activityOptions, DurableSessionRequest requestEntry, ChatOptions? chatOptions)`

Dispatches the LLM call (or custom logic) as a Temporal activity. The base class supplies pre-built `ActivityOptions` (timeout, heartbeat, summary), the request entry that was just appended to history, and the per-turn `ChatOptions` (model id, tools).

```csharp
protected override Task<ShoppingTurnOutput> ExecuteTurnAsync(
    ActivityOptions activityOptions,
    DurableSessionRequest requestEntry,
    ChatOptions? chatOptions)
{
    var allMessages = History.SelectMany(e => e.Messages).ToList();
    var activityInput = new DurableChatInput
    {
        Messages = allMessages,
        Options = chatOptions,
        CorrelationId = requestEntry.CorrelationId,
    };
    return Workflow.ExecuteActivityAsync(
        (ShoppingActivities a) => a.GetShoppingResponseAsync(activityInput),
        activityOptions);
}
```

You can dispatch to any registered activity class — it does not have to be derived from `DurableChatActivities`. The base exposes the full conversation history via the protected `History` property; the activity input is yours to construct.

### `CreateContinueAsNewException(DurableChatWorkflowInput input)`

Creates the `ContinueAsNewException` typed to the concrete workflow class. The base class calls this when `Workflow.ContinueAsNewSuggested` is true, passing the new input with the carried history.

```csharp
protected override ContinueAsNewException CreateContinueAsNewException(
    DurableChatWorkflowInput input) =>
    Workflow.CreateContinueAsNewException(
        (ShoppingAssistantWorkflow wf) => wf.RunAsync(input));
```

The concrete type in the lambda must match the actual workflow class — if you use the wrong type, Temporal will start a workflow of the wrong kind on the next run.

---

## What You Inherit

By extending `DurableChatWorkflowBase<TOutput>` you get the following at no cost:

- **Session loop** — `RunAsync` waits for shutdown or `ContinueAsNewSuggested`, then transitions or returns.
- **Conversation history** — full `List<DurableSessionEntry>` persisted in workflow state, restored on continue-as-new. Each turn appends a `DurableSessionRequest` followed by a `DurableSessionResponse`.
- **Turn serialization** — `WaitConditionAsync(() => !_isProcessing)` prevents concurrent turns from corrupting history.
- **HITL** — `[WorkflowUpdate("RequestApproval")]`, `[WorkflowUpdate("SubmitApproval")]`, and `[WorkflowQuery("GetPendingApproval")]` are wired to `DurableApprovalMixin` automatically.
- **Continue-as-new** — history is carried forward when workflow history grows large; search attributes are preserved.
- **Search attributes** — optional `TurnCount` and `SessionCreatedAt` upserts via `DurableSessionAttributes` when `input.EnableSearchAttributes` is `true`.
- **`[WorkflowQuery("GetHistory")]`** — returns the current conversation history.
- **`[WorkflowSignal("Shutdown")]`** — sets `IsShutdownRequested` and unblocks the session loop.

---

## Comparison

| | Default (`DurableChatWorkflow` + `DurableChatSessionClient`) | Custom (`DurableChatWorkflowBase<TOutput>`) |
|---|---|---|
| Return type from Update | `ChatResponse` | Any serializable type |
| Domain data per turn | Via separate query or side channel | Returned atomically with the response |
| Activity class | `DurableChatActivities` (built-in) | Your own class |
| Entry point | `DurableChatSessionClient.ChatAsync` | `WorkflowHandle.ExecuteUpdateAsync` |
| Registration | `AddDurableAI()` only | `AddDurableAI()` + `AddWorkflow<T>()` + `AddSingletonActivities<T>()` |
| Code to write | None | Three abstract method overrides + Update method |
| HITL | Inherited | Inherited |
| Continue-as-new | Inherited | Inherited |
| History persistence | Inherited | Inherited |

---

## Sample Code

The full implementation is in `samples/MEAI/CustomWorkflow/`:

- `ShoppingAssistantWorkflow.cs` — the concrete `DurableChatWorkflowBase<ShoppingTurnOutput>` subclass
- `ShoppingActivities.cs` — the activity class with cart tool definitions and `GetShoppingResponseAsync`
- `ShoppingTurnOutput.cs` — the typed output carrying `ChatResponse` + `IReadOnlyList<CartAction>`
- `CartAction.cs` — the domain type for cart mutations
- `Program.cs` — host setup, workflow start, and two-turn demo
