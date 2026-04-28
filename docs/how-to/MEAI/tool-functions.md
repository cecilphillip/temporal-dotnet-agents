# Tool Functions in Temporalio.Extensions.AI

There are two distinct models for invoking tools (AI functions) in this library. Choosing
the right one depends on whether you are working in the standard durable chat pipeline or
writing a custom Temporal workflow.

---

## Model 1 — Tools in the Chat Pipeline (`UseFunctionInvocation`)

This is the default model for `DurableChatSessionClient`. Tools are passed via
`ChatOptions.Tools` and the MEAI `UseFunctionInvocation()` middleware handles the
tool call loop automatically inside the `DurableChatActivities` activity.

```
DurableChatWorkflow
  └─► DurableChatActivities.GetResponseAsync      ← one Temporal activity
        └─► IChatClient (with UseFunctionInvocation middleware)
              LLM request
              → tool call (executed locally in the activity)
              → LLM request with tool result
              → final response
```

The entire round-trip — LLM request, tool execution, follow-up LLM request — runs inside
a **single activity**. From Temporal's perspective, the chat turn is one unit of work.

### Setup

Register `UseFunctionInvocation()` on the `IChatClient` pipeline, then pass tools per
call via `ChatOptions`:

```csharp
// Worker
// Host registration
builder.Services
    .AddChatClient(innerClient)
    .UseFunctionInvocation()   // handles the tool call loop inside the activity
    .Build();

builder.Services
    .AddHostedTemporalWorker("my-task-queue")
    .AddDurableAI();
```

```csharp
// Client
// Per call
var getWeather = AIFunctionFactory.Create(
    (string city) => $"Sunny, 22°C in {city}",
    name: "get_current_weather");

var options = new ChatOptions { Tools = [getWeather] };
var response = await sessionClient.ChatAsync("conv-123", messages, options);
```

### When to use

- The tool is fast and lightweight
- You want a simple setup with no custom workflow code
- Independent retry per tool call is not a requirement — retrying the whole chat turn
  on failure is acceptable
- Most applications should start here

### What `DurableTools` does NOT do here

`AddDurableTools` and `AsDurable()` have **no effect** in this path. Tools registered
with `AddDurableTools` are only resolved by `DurableFunctionActivities`, which is only
reached from Model 2. In the chat pipeline, `UseFunctionInvocation()` calls tool
functions directly in the activity process — it never touches the `DurableFunctionRegistry`.

---

## Model 2 — Per-Tool Temporal Activities (`AsDurable()`)

This model is for **custom `[Workflow]` code** that needs to invoke a tool as its own
independent Temporal activity. Each tool call gets a separate entry in the event history,
with its own retry policy, timeout, and failure isolation.

```
MyCustomWorkflow
  └─► durableTool.InvokeAsync(arguments)          ← dispatches to...
        └─► DurableFunctionActivities              ← its OWN Temporal activity
              └─► DurableFunctionRegistry["tool-name"] → real function
```

`AsDurable()` wraps an `AIFunction` as a `DurableAIFunction`. When `InvokeAsync` is
called inside a workflow (`Workflow.InWorkflow == true`), it dispatches to
`DurableFunctionActivities` via `Workflow.ExecuteActivityAsync`. Outside a workflow
(`Workflow.InWorkflow == false`), it passes through to the inner function unchanged —
the same wrapped instance works in both contexts.

### Setup

Register the real function with `AddDurableTools` so `DurableFunctionActivities` can
resolve it by name at runtime:

```csharp
// Worker
// Host registration
builder.Services
    .AddHostedTemporalWorker("my-task-queue")
    .AddDurableAI()
    .AddDurableTools(getWeather);   // puts function in DurableFunctionRegistry
```

Inside the workflow, wrap with `AsDurable()` and call `InvokeAsync`:

```csharp
// Workflow
[Workflow]
public class MyWorkflow
{
    [WorkflowRun]
    public async Task<string> RunAsync(string city)
    {
        // The inner lambda is a stub — only reached when Workflow.InWorkflow == false.
        // Inside this workflow, InvokeAsync dispatches to DurableFunctionActivities.
        var durableWeather = AIFunctionFactory.Create(
            (string c) => "[stub]",
            name: "get_current_weather"
        ).AsDurable();

        var result = await durableWeather.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?> { ["city"] = city }));

        return result?.ToString() ?? string.Empty;
    }
}
```

The function name passed to `AIFunctionFactory.Create` must match the name registered
via `AddDurableTools` — `DurableFunctionActivities` resolves by name (case-insensitive).

> **Prerequisites for `AsDurable()`.** The activity worker handling the workflow's task
> queue must have both `AddDurableAI()` (registers the dispatch activity) and
> `AddDurableTools(function)` (registers the function in the durable function registry)
> called on it. These are runtime requirements — `AsDurable()` itself has no DI context
> and cannot validate them at wrap time. If the function is missing from the registry,
> the activity throws `InvalidOperationException` at invocation time with the message
> `"Function '{name}' is not registered in the durable function registry."`

### When to use

- The tool is **long-running** or calls a slow external API that may time out
- You want **independent retry** per tool call — a failed tool should not force the
  whole workflow to retry from the start
- Different tools need **different timeout or retry policies**
- You want each tool invocation to appear as a **named, inspectable entry** in the
  Temporal Web UI or event history
- You are writing a custom orchestration workflow (not using `DurableChatSessionClient`)

### Per-tool timeout and retry

Pass `ActivityOptions` to `AsDurable()` to override options per function:

```csharp
// Workflow
var durablePayment = paymentTool.AsDurable(new ActivityOptions
{
    StartToCloseTimeout = TimeSpan.FromSeconds(30),
    RetryPolicy = new RetryPolicy { MaximumAttempts = 3 }
});

var durableLookup = lookupTool.AsDurable(new ActivityOptions
{
    StartToCloseTimeout = TimeSpan.FromSeconds(5),
    RetryPolicy = new RetryPolicy { MaximumAttempts = 10 }
});
```

---

## Comparison

| | Model 1 (`UseFunctionInvocation`) | Model 2 (`AsDurable()`) |
|---|---|---|
| Entry point | `DurableChatSessionClient.ChatAsync` | Custom `[Workflow]` |
| Tool execution | MEAI middleware inside one activity | `DurableFunctionActivities` — own activity per call |
| Temporal event history | One entry for the whole chat turn | One entry per tool invocation |
| Per-tool retry / timeout | No | Yes |
| Code complexity | Low | Requires custom workflow |
| Typical use case | Standard chat with tools | Long-running tools, custom pipelines |

---

## Sample Code

- **Model 1**: `samples/MEAI/DurableChat/` — Demo 2 (`RunToolCallDemoAsync`) shows
  `ChatOptions.Tools` with `UseFunctionInvocation()`
- **Model 2**: `samples/MEAI/DurableTools/` — `WeatherReportWorkflow` shows `AsDurable()`
  inside a custom workflow dispatching to `DurableFunctionActivities`
