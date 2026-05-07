# Per-LLM-Call Interception via `ChatClientFactory`

How to wrap the inner `IChatClient` so every LLM call made by an agent is observable — log inputs and outputs, time the request, capture token usage, attach custom telemetry — without changing how Temporal dispatches the activity.

This is the answer to "I want per-LLM-call observability today." If you have read about a future opt-in granular tool dispatch mode and concluded you need it for visibility, you almost certainly do not — see [Comparison with granular tool dispatch](#comparison-with-granular-tool-dispatch) below.

> **Applies to `ChatClientAgent` only.** This pattern works because `ChatClientAgent` reads `ChatClientFactory` from its run options (or accepts a `clientFactory` constructor parameter) and applies it before each LLM call. **`A2AAgent`, graph-workflow agents, and other non-`ChatClientAgent` `AIAgent` subtypes do not have an inner `IChatClient` to wrap** — they dispatch via different protocols (HTTP+JSON for A2A; in-process graph orchestration for workflow agents). Registering a non-`ChatClientAgent` and supplying a `ChatClientFactory` will silently no-op. If you need observability for those agent types, instrument at the agent's own dispatch layer instead (e.g., HTTP-client middleware for A2A; OpenTelemetry source for graph workflows).

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

The `ChatClientAgent` (returned by `IChatClient.AsAIAgent(...)`) accepts a `clientFactory: Func<IChatClient, IChatClient>` at construction. MAF applies this factory before each `RunAsync` / `RunStreamingAsync` call to produce the `IChatClient` actually used for that turn. The factory runs **inside the activity** — the wrapped client is not part of workflow replay state, so OTel spans, logs, and metrics emitted by the decorator propagate through the activity's existing trace context.

`Temporalio.Extensions.Agents` is layered cleanly on top of this. Internally, `AgentWorkflowWrapper` (the per-activity adapter inside `AgentActivities.ExecuteAgentAsync`) composes its own `ChatClientFactory` over whatever the registered agent already had, in order to apply per-request tool filtering and response-format overrides. Your factory and the library's factory both run on the same `IChatClient` pipeline; nothing about the pattern is exclusive to the library.

The cleanest interception point for application code is therefore the `clientFactory` parameter of `AsAIAgent(...)` at agent registration time — it is in the same place as `UseFunctionInvocation()`, and it composes naturally with whatever middleware the agent already uses.

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

The decorator runs inside `AgentActivities.ExecuteAgentAsync` for every LLM round of every turn. Logs and spans nest naturally inside the existing `agent.turn` span (see [Observability](./observability.md) for the full span hierarchy).

---

## Composing with the library's own decoration

`AgentWorkflowWrapper` adds its own `ChatClientFactory` per request to apply tool filtering (`TemporalAgentRunOptions.EnableToolNames`) and response-format overrides (`ChatClientAgentRunOptions.ChatOptions.ResponseFormat`). When the registered agent already has a `clientFactory` (your logging decorator), the library wraps it — both run, in pipeline order, on every call.

Concretely, for a registered agent with the example factory above:

```
inner OpenAI client
  → UseFunctionInvocation       (function-invocation loop)
    → LoggingChatClient          (your decorator, logs every round)
      → ConfigureOptions block   (library's per-request tool filter / response format)
```

You do not need to do anything special to compose. Register your decorator at agent construction; the library composes around it.

---

## Comparison with granular tool dispatch

Two questions look similar but have different answers:

| Need | Solution today | Status |
|---|---|---|
| **Per-LLM-call observability** — see every model request and response, time it, log it, span it. | `ChatClientFactory` interception (this guide). | Available now, library-level support, no opt-in flag. |
| **Per-tool durability** — each tool call retried independently in Temporal event history, with its own timeout and retry policy. | A future opt-in `EnableGranularDispatch` mode in `TemporalAgentsOptions`. | Deferred — gated by a documented set of exit criteria. See `docs/design-decisions.md` § "Exit criteria — when would granular dispatch be added?" |

The two are different problems:

- **Observability** is about *seeing* what happens during a turn. The full-loop activity model is correct here — Temporal records the turn-level checkpoint, and `ChatClientFactory` interception adds round-level detail without changing the durability shape.
- **Durability** is about *what gets retried* when something fails. The full-loop model retries the whole turn (LLM + all tools) on activity failure. Granular dispatch would retry only the failing tool, but at the cost of additional implementation complexity and a documented incompatibility with stateful `AIContextProvider` implementations.

If you came here because you wanted "more visibility into when agents execute tools," `ChatClientFactory` interception is the answer. The decorator sees every LLM round — including the rounds where the model returns `FunctionCallContent` — and you can log tool requests, tool results, and the final assistant message all from one place.

If you came here because you have a documented production incident where a flaky tool caused costly full-turn replays, see the gate criteria in `docs/design-decisions.md`.

---

## References

- `docs/design-decisions.md` § "Function Invocation: Loop Ownership and Durability Granularity" — the underlying design rationale for keeping the full-loop model as the default.
- `docs/design-decisions.md` § "Exit criteria — when would granular dispatch be added?" — what evidence would unblock the deferred granular mode.
- `docs/how-to/MAF/observability.md` — the full OTel span hierarchy. Spans emitted by your decorator nest inside `agent.turn`.
- `src/Temporalio.Extensions.Agents/AgentWorkflowWrapper.cs` — how the library composes its own `ChatClientFactory` over yours, per request.
- `samples/MAF/BasicAgent/Program.cs` — the canonical `AsAIAgent(..., clientFactory: ...)` registration shape.
- [`Microsoft.Extensions.AI.DelegatingChatClient`](https://learn.microsoft.com/dotnet/api/microsoft.extensions.ai.delegatingchatclient) — the base class for chat client decorators.

---

_Last updated: 2026-04-30_
