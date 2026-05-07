# Per-LLM-Call Interception via `ChatClientFactory`

How to wrap the inner `IChatClient` so every LLM call made by an agent is observable — log inputs and outputs, time the request, capture token usage, attach custom telemetry — without changing how Temporal dispatches the activity.

This is the answer to "I want per-LLM-call observability today." See also the [`docs/design-decisions.md`](../../design-decisions.md) entry on durable-agent dispatch granularity for the underlying architectural rationale.

> **Applies to `ChatClientAgent`-backed durable agents.** In v0.3, `AddDurableAgent` constructs a `ChatClientAgent` internally from the user's `agent.ChatClient` factory, with `UseProvidedChatClientAsIs = true` (so MAF does not auto-inject `FunctionInvokingChatClient` — the workflow owns the tool-dispatch loop). The interception point is the `IChatClient` your factory returns; decorate it before returning it from the factory. **`A2AAgent`, graph-workflow agents, and other non-`ChatClientAgent` `AIAgent` subtypes do not have an inner `IChatClient` to wrap** — they dispatch via different protocols and are not produced by `AddDurableAgent`. If you need observability for those agent types, instrument at the agent's own dispatch layer instead (e.g., HTTP-client middleware for A2A; OpenTelemetry source for graph workflows).

---

## When to use this pattern

Use `ChatClientFactory` interception when you want to:

- Log every LLM request/response pair from a registered agent.
- Time each LLM call independently from the surrounding activity.
- Emit custom OpenTelemetry spans, metrics, or events around the model call.
- Inspect or rewrite tool call payloads in flight (debugging tool-loop misbehavior).

Do **not** use this pattern for:

- **Per-tool durability** (each tool retried independently in workflow event history). That is a different problem with a different solution — see [Comparison with granular tool dispatch](#comparison-with-granular-tool-dispatch) and `docs/design-decisions.md` § "Function Invocation: Loop Ownership and Durability Granularity".
- **Cross-agent or cross-session aggregation.** Decorate at the registered `IChatClient` level, but treat the data as scoped to a single activity invocation — the wrapped client is rebuilt per call.

---

## How it works

In v0.3, `AddDurableAgent` lazily composes the agent at first activity dispatch (`AgentActivities.ComposeDurableAgent`):

1. Resolve `IChatClient` by invoking the user-supplied `agent.ChatClient` factory against the worker's `IServiceProvider`.
2. Resolve each tool's `Factory(sp)` and each context provider's factory.
3. Build a `ChatClientAgent` with `UseProvidedChatClientAsIs = true` so MAF does **not** auto-wrap the client in `FunctionInvokingChatClient`. The tool loop is owned by the workflow (`AgentWorkflow.ExecuteDurableAgentTurnAsync`) which dispatches each LLM call as a separate `RunDurableAgentStep` activity and each tool call as a separate `InvokeAgentTool` activity, fanned out via `Workflow.WhenAllAsync`.
4. Cache the composed agent on `AgentActivities._durableAgentCache`.

Each `RunDurableAgentStep` activity calls `IChatClient.GetStreamingResponseAsync` on the cached client with a freshly constructed `ChatOptions` (cloned from `registration.ChatOptions`, with library-stamped `Tools` / `Instructions` / `ResponseFormat`). Per-request tool filtering (`TemporalAgentRunOptions.EnableToolNames`) and response-format selection are applied to that `ChatOptions` before the call — they do **not** require an additional `ChatClientFactory` decorator.

The cleanest interception point for application code is therefore the `agent.ChatClient` factory itself: return a decorated `IChatClient` from the factory and every `RunDurableAgentStep` activity sees calls flow through it. Because the factory runs inside the activity (not on the workflow side), OTel spans, logs, and metrics emitted by the decorator nest naturally inside the activity's existing trace context.

---

## Example: a logging decorator

This example uses `Microsoft.Extensions.AI`'s `DelegatingChatClient` to wrap the inner client with a logger. Adjust the body of `GetResponseAsync` / `GetStreamingResponseAsync` to emit whatever telemetry your platform expects.

### Step 1 — write a `DelegatingChatClient`

```csharp
using System.Diagnostics;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

internal sealed class LoggingChatClient(IChatClient inner, ILogger<LoggingChatClient> logger)
    : DelegatingChatClient(inner)
{
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        logger.LogInformation(
            "LLM request: model={Model}, messages={Count}, tools={ToolCount}",
            options?.ModelId,
            messages.Count(),
            options?.Tools?.Count ?? 0);

        try
        {
            var response = await base.GetResponseAsync(messages, options, cancellationToken);

            logger.LogInformation(
                "LLM response: model={Model}, duration={DurationMs}ms, " +
                "input_tokens={Input}, output_tokens={Output}, total_tokens={Total}, " +
                "finish_reason={FinishReason}",
                options?.ModelId,
                sw.ElapsedMilliseconds,
                response.Usage?.InputTokenCount,
                response.Usage?.OutputTokenCount,
                response.Usage?.TotalTokenCount,
                response.FinishReason);

            return response;
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "LLM call failed: model={Model}, duration={DurationMs}ms",
                options?.ModelId, sw.ElapsedMilliseconds);
            throw;
        }
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        logger.LogInformation(
            "LLM streaming request: model={Model}, messages={Count}",
            options?.ModelId, messages.Count());

        await foreach (var update in base.GetStreamingResponseAsync(messages, options, cancellationToken))
        {
            yield return update;
        }

        logger.LogInformation(
            "LLM streaming complete: model={Model}, duration={DurationMs}ms",
            options?.ModelId, sw.ElapsedMilliseconds);
    }
}
```

### Step 2 — decorate the `IChatClient` registered in DI

In v0.3 the durable-agent path composes the chat pipeline internally and passes the user's `IChatClient` through with `UseProvidedChatClientAsIs = true` (so that MAF does not auto-inject `FunctionInvokingChatClient` — the workflow owns the tool-dispatch loop). To intercept individual LLM calls, decorate the `IChatClient` that the agent's `ChatClient` factory resolves:

```csharp
using Microsoft.Extensions.AI;

builder.Services.AddSingleton<LoggingChatClient>(sp =>
    new LoggingChatClient(
        openAiClient.GetChatClient("gpt-4o-mini").AsIChatClient(),
        sp.GetRequiredService<ILogger<LoggingChatClient>>()));

builder.Services.AddChatClient(sp => sp.GetRequiredService<LoggingChatClient>());

builder.Services
    .AddHostedTemporalWorker("agents")
    .AddTemporalAgents(opts =>
    {
        opts.AddDurableAgent("Assistant", agent =>
        {
            agent.Instructions = "You are a helpful assistant.";
            agent.ChatClient   = sp => sp.GetRequiredService<IChatClient>();
            agent.AddTool(weatherTool);
        });
    });
```

The decorator wraps the inner `IChatClient` and sees every LLM call the durable-agent loop dispatches via `RunDurableAgentStep`. Each iteration of the per-tool loop produces a separate decorated call, so per-LLM-round observability composes naturally.

> **Don't** call `.UseFunctionInvocation()` on the chain — the durable-agent workflow owns the tool-dispatch loop. Calling it would conflict with the workflow's `InvokeAgentTool` activities and is unsupported.

### Step 3 — invoke the agent normally

No caller-side changes are required:

```csharp
var proxy = host.Services.GetTemporalAgentProxy("Assistant");
var session = await proxy.CreateSessionAsync();
var response = await proxy.RunAsync("What's the weather?", session);
```

The decorator runs inside each `AgentActivities.RunDurableAgentStepAsync` activity for every LLM round of every turn. Logs and spans nest naturally inside the existing `agent.turn` span (see [Observability](./observability.md) for the full span hierarchy).

---

## Composing with the library's own per-step `ChatOptions`

The library does not decorate your `IChatClient` — it constructs a fresh `ChatOptions` per step from `registration.ChatOptions` (cloned), then stamps `Instructions`, `Tools`, and `ResponseFormat` on it according to the active `TemporalAgentRunOptions` (e.g., `EnableToolNames` filtering) and the originating `RunRequest`. Your decorator sees the resulting `ChatOptions` on every call.

Concretely, for a registered agent with the example factory above:

```
inner OpenAI client
  → LoggingChatClient                   (your decorator, logs every round)
    ↑
    invoked by RunDurableAgentStepAsync via IChatClient.GetStreamingResponseAsync
    with a per-step ChatOptions (tools / instructions / response format stamped by the library)
```

You do not need to do anything special to compose: return the decorated client from `agent.ChatClient` and the library's per-step `ChatOptions` shaping happens transparently around it.

---

## Comparison with the v0.3 dispatch model

Per-LLM-call observability and per-tool durability are different concerns, and v0.3 supports both directly:

| Need | Solution |
|---|---|
| **Per-LLM-call observability** — see every model request and response, time it, log it, span it. | `IChatClient` decorator returned from `agent.ChatClient` (this guide). |
| **Per-tool durability** — each tool call retried independently in Temporal event history, with its own timeout and retry policy. | Built in. Every `agent.AddTool(...)` runs as a separately-named `InvokeAgentTool` activity. Configure retry/timeout per tool via the `DurableToolOptions` callback (e.g., `agent.AddTool(t, opts => opts.NoRetry())` for non-idempotent write tools). See [`durable-agents.md`](./durable-agents.md). |

Both work together. The decorator sees every LLM round — including the rounds where the model returns `FunctionCallContent` — and the workflow's tool fan-out (`Workflow.WhenAllAsync`) is independently visible in Temporal event history with one event group per tool call.

---

## References

- [`docs/design-decisions.md`](../../design-decisions.md) — the underlying design rationale for the v0.3 durable-agent dispatch model.
- [`docs/how-to/MAF/durable-agents.md`](./durable-agents.md) — per-tool retry/timeout configuration via `DurableToolOptions`.
- [`docs/how-to/MAF/observability.md`](./observability.md) — the full OTel span hierarchy. Spans emitted by your decorator nest inside `agent.turn`.
- `src/Temporalio.Extensions.Agents/Workflows/AgentActivities.cs` — `ComposeDurableAgent` and `RunDurableAgentStepAsync`, where the per-step `ChatOptions` is built and the decorated `IChatClient` is invoked.
- `samples/MAF/BasicAgent/Program.cs` — canonical `agent.ChatClient = sp => ...` registration shape.
- [`Microsoft.Extensions.AI.DelegatingChatClient`](https://learn.microsoft.com/dotnet/api/microsoft.extensions.ai.delegatingchatclient) — the base class for chat client decorators.

---

_Last updated: 2026-04-30_
