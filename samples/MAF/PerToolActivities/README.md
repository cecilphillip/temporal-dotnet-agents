# PerToolActivities — Per-Tool Temporal Activity Granularity

This sample demonstrates **per-tool Temporal activities** in v0.3 of
`Temporalio.Extensions.Agents`. Every LLM call is its own
`Temporalio.Extensions.Agents.RunDurableAgentStep` activity, and every tool call
is its own `Temporalio.Extensions.Agents.InvokeAgentTool` activity. Per-tool
retry policies make write-style tools safe to use without risking double-fire on
transient failure.

For the conceptual background, see
[`docs/how-to/MAF/durable-agents.md`](../../../docs/how-to/MAF/durable-agents.md).

---

## The Story

A refund-handling agent receives a customer complaint and must perform three tool
calls to resolve it:

| Tool          | Type  | `MaximumAttempts` | Why                                           |
| ------------- | ----- | ----------------- | --------------------------------------------- |
| `lookup_order`| Read  | default (unbounded) | Idempotent — safe to retry on transient failure |
| `apply_refund`| Write | **1**             | Non-idempotent — retry would issue a double refund |
| `send_email`  | Write | **1**             | Non-idempotent — retry would deliver a duplicate   |

The same workflow runs twice:

1. **Happy path** — every tool succeeds on its first attempt.
2. **Transient lookup failure** — the in-memory `lookup_order` tool is configured
   to throw on its first invocation. Temporal retries the activity (default
   policy, unbounded attempts) and the second attempt succeeds. The write tools
   are **not** re-run.

The console output of scenario 2 prints `LookupCalls = 2, RefundCalls = 1, SendCalls = 1`,
proving that retries are scoped to the failing tool.

---

## Prerequisites

- .NET 10 SDK
- Temporal dev server running: `temporal server start-dev`
- An OpenAI API key:
  ```bash
  dotnet user-secrets set "OPENAI_API_KEY" "sk-..." \
      --project samples/MAF/PerToolActivities
  ```
  (Optional `OPENAI_API_BASE_URL` if you target a self-hosted compatible endpoint.)

---

## Run

```bash
dotnet run --project samples/MAF/PerToolActivities/PerToolActivities.csproj
```

After both scenarios complete, open the Temporal Web UI at
<http://localhost:8233> and inspect either workflow's history. Each
`RunDurableAgentStep` and each `InvokeAgentTool` shows up as a distinct activity
row with the tool name in its summary — clicking into the failed `lookup_order`
row in the second workflow shows two attempts, while the `apply_refund` and
`send_email` rows show exactly one attempt each.

---

## Expected Output (abridged)

```
Worker started. Running per-tool activity scenarios...

─── Scenario 1: Happy path (all tools succeed) ──────────────
  Workflow:  refund-happy-...
  Complaint: I never received my order ORD-002 and want a full $49.99 refund...
  Agent:     I have refunded $49.99 to your order ORD-002 and emailed confirmation to acme@example.com.
  Final state → LookupCalls = 1, RefundCalls = 1, SendCalls = 1

─── Scenario 2: Transient lookup failure (per-tool retry) ───
  Workflow:  refund-retry-...
  Complaint: Refund my order ORD-001 for $19.99. Email confirmation to acme@example.com.
  Agent:     Your refund of $19.99 has been applied to ORD-001 and the confirmation email is on its way.
  This run    → LookupCalls = 2, RefundCalls = 1, SendCalls = 1
  Cumulative  → LookupCalls = 3, RefundCalls = 2, SendCalls = 2
  ✓ Per-tool retry granularity confirmed: lookup retried, writes fired exactly once.

─── View the activity timeline ──────────────────────────────
  Open http://localhost:8233 in the Temporal Web UI.
  ...

Done.
```

The exact wording of the agent's final reply varies between runs — the model is
non-deterministic. The call counts are deterministic: lookup at least 2, write
tools exactly 1 each.

---

## How It Wires Together (v0.3)

### One registration call — `AddDurableAgent`

```csharp
builder.Services.AddSingleton<OrderService>();
builder.Services.AddSingleton<RefundService>();
builder.Services.AddSingleton<EmailService>();
builder.Services.AddChatClient(openAiClient.GetChatClient(model).AsIChatClient());

builder.Services
    .AddHostedTemporalWorker(taskQueue)
    .AddTemporalAgents(opts =>
    {
        opts.AddDurableAgent("RefundAgent", agent =>
        {
            agent.Instructions = "You are a customer refund specialist...";
            agent.ChatClient   = sp => sp.GetRequiredService<IChatClient>();
            agent.MaxToolCallsPerTurn = 10;

            // Read tool — inherits worker default retry policy.
            agent.AddTool("lookup_order", sp => AIFunctionFactory.Create(
                sp.GetRequiredService<OrderService>().LookupOrder,
                "lookup_order"));

            // Write tools — opts.NoRetry() binds MaximumAttempts = 1 to the AIFunction
            // reference. No string-keyed PerToolActivityOptions dictionary, no
            // name-typo footgun.
            agent.AddTool(
                "apply_refund",
                sp => AIFunctionFactory.Create(
                    sp.GetRequiredService<RefundService>().ApplyRefund,
                    "apply_refund"),
                opts => opts.NoRetry().WithTimeout(TimeSpan.FromSeconds(30)));

            agent.AddTool(
                "send_email",
                sp => AIFunctionFactory.Create(
                    sp.GetRequiredService<EmailService>().SendEmail,
                    "send_email"),
                opts => opts.NoRetry().WithTimeout(TimeSpan.FromSeconds(30)));
        });
    })
    .AddWorkflow<RefundWorkflow>();
```

What v0.3 simplifies away:

- **No `BuildServiceProvider()` bootstrap** — the `AddTool(string, sp => ...)`
  factory runs at first activity dispatch with the worker's runtime
  `IServiceProvider`. Tool services resolve cleanly without an early DI build.
- **No `AddDurableAI(_ => { })`** + **no `AddDurableTools(...)`** — the durable
  agent path subsumes both. (MEAI's `DurableChatWorkflow` users still need them.)
- **No `EnablePerToolActivities = true`** — every `AddDurableAgent` is durable
  by definition.
- **No `PerToolActivityOptions["..."]` dictionary** — per-tool retry policy is
  bound to the `AIFunction` reference at registration via `opts => opts.NoRetry()`.
- **No "must not call `UseFunctionInvocation()`"** caveat — the library
  composes the chat pipeline internally with `UseProvidedChatClientAsIs = true`.

### `DurableToolOptions` fluent sugar

| Method                       | What it does                                           |
| ---------------------------- | ------------------------------------------------------ |
| `opts.NoRetry()`             | Sets `RetryPolicy = new() { MaximumAttempts = 1 }`     |
| `opts.WithMaxAttempts(n)`    | Sets a fixed-attempt retry policy                       |
| `opts.WithTimeout(t)`        | Sets `StartToCloseTimeout`                              |

These compose: `opts => opts.NoRetry().WithTimeout(TimeSpan.FromSeconds(30))` is valid.

---

## What You'll See in the Web UI

Scenario 1 (happy path) workflow history:

```
RunDurableAgentStep                                  ← LLM call #1 → returns lookup_order
InvokeAgentTool:RefundAgent:lookup_order             ← 1 attempt, succeeds
RunDurableAgentStep                                  ← LLM call #2 → returns apply_refund
InvokeAgentTool:RefundAgent:apply_refund             ← 1 attempt, succeeds (MaximumAttempts = 1)
RunDurableAgentStep                                  ← LLM call #3 → returns send_email
InvokeAgentTool:RefundAgent:send_email               ← 1 attempt, succeeds (MaximumAttempts = 1)
RunDurableAgentStep                                  ← LLM call #4 → final assistant message
```

Scenario 2 (transient lookup failure) — the difference shows up on the first
`InvokeAgentTool` row:

```
InvokeAgentTool:RefundAgent:lookup_order             ← 2 attempts: 1 failure, 1 success
```

The write-tool rows are unchanged: still one attempt each. That is the per-tool
retry granularity benefit.

---

## See Also

- Conceptual guide: [`docs/how-to/MAF/durable-agents.md`](../../../docs/how-to/MAF/durable-agents.md)
- Workflow loop architecture: [`docs/architecture/MAF/agent-sessions-and-workflow-loop.md`](../../../docs/architecture/MAF/agent-sessions-and-workflow-loop.md)
- v0.2 → v0.3 migration: [`MIGRATION-v0.3.md`](../../../MIGRATION-v0.3.md)
- The MEAI counterpart for tool durability: [`samples/MEAI/DurableTools/`](../../MEAI/DurableTools/)
