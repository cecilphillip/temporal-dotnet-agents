# PerToolActivities — Per-Tool Temporal Activity Granularity

This sample demonstrates **step mode** in `Temporalio.Extensions.Agents`: every LLM
call becomes its own `RunAgentStep` Temporal activity, and every tool call becomes
its own `InvokeFunction` activity. Per-tool retry policies make write-style tools
safe to use without risking double-fire on transient failure.

For the conceptual background, see
[`docs/how-to/MAF/per-tool-activities.md`](../../../docs/how-to/MAF/per-tool-activities.md).

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
proving that retries are scoped to the failing tool and do not cascade across the
entire turn the way they would in default-mode (single-`ExecuteAgent`-per-turn) execution.

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
<http://localhost:8233> and inspect either workflow's history. Each `RunAgentStep`
and each `InvokeFunction` shows up as a distinct activity row with the tool name
in its summary — clicking into the failed `lookup_order` row in the second
workflow shows two attempts, while the `apply_refund` and `send_email` rows show
exactly one attempt each.

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

## How It Wires Together

### Agent registration: NO `UseFunctionInvocation()`

This is the constraint that makes step mode work. In default mode, the agent's
`IChatClient` pipeline includes `UseFunctionInvocation()` middleware, which
auto-executes tool calls inside a single `ExecuteAgent` activity. In step mode,
the workflow needs to see raw `FunctionCallContent` from the model so it can
dispatch each tool call as its own activity. Therefore:

```csharp
// Register the inner IChatClient WITHOUT UseFunctionInvocation
builder.Services
    .AddChatClient(openAiClient.GetChatClient(model).AsIChatClient())
    .Build();

// The agent factory uses .AsAIAgent(...) WITHOUT a clientFactory parameter — the
// default pipeline does NOT include UseFunctionInvocation, which is what step mode
// requires. The tools[] list is schema-only; the workflow dispatches them.
opts.AddAIAgentFactory("RefundAgent", sp =>
    sp.GetRequiredService<IChatClient>().AsAIAgent(
        name: "RefundAgent",
        instructions: "...",
        tools: [lookupTool, refundTool, emailTool]));
```

### Tool registration: both schema and registry

The same three `AIFunction` instances are passed to two places:

- **Agent's `tools:` parameter** — exposes the JSON schema to the LLM so it knows
  what calls it may emit.
- **`AddDurableTools(...)`** — registers them in `DurableFunctionRegistry` keyed
  by name, so `DurableFunctionActivities.InvokeFunction` can resolve them when
  the workflow dispatches a tool call.

### Per-tool retry policy

```csharp
opts.PerToolActivityOptions = new()
{
    ["apply_refund"] = new ActivityOptions
    {
        StartToCloseTimeout = TimeSpan.FromSeconds(30),
        RetryPolicy = new RetryPolicy { MaximumAttempts = 1 },
    },
    ["send_email"] = new ActivityOptions
    {
        StartToCloseTimeout = TimeSpan.FromSeconds(30),
        RetryPolicy = new RetryPolicy { MaximumAttempts = 1 },
    },
    // lookup_order: omitted — falls through to TemporalAgentsOptions.RetryPolicy
    //                          (the SDK default, unbounded retry).
};
```

When `InvokeFunction(toolName)` is dispatched, the workflow looks the name up in
this dictionary case-insensitively. A miss falls back to the worker's default
`ActivityTimeout`, `HeartbeatTimeout`, and `RetryPolicy`. See
[`docs/how-to/MAF/per-tool-activities.md`](../../../docs/how-to/MAF/per-tool-activities.md#per-tool-activityoptions)
for the full fallback table.

---

## What You'll See in the Web UI

Scenario 1 (happy path) workflow history:

```
RunAgentStep                               ← LLM call #1 → returns lookup_order
InvokeFunction (Summary: "lookup_order")   ← 1 attempt, succeeds
RunAgentStep                               ← LLM call #2 → returns apply_refund
InvokeFunction (Summary: "apply_refund")   ← 1 attempt, succeeds (MaximumAttempts = 1)
RunAgentStep                               ← LLM call #3 → returns send_email
InvokeFunction (Summary: "send_email")     ← 1 attempt, succeeds (MaximumAttempts = 1)
RunAgentStep                               ← LLM call #4 → final assistant message
```

Scenario 2 (transient lookup failure) — the difference shows up on the first
`InvokeFunction` row:

```
InvokeFunction (Summary: "lookup_order")   ← 2 attempts: 1 failure, 1 success
```

The write-tool rows are unchanged: still one attempt each. That is the per-tool
retry granularity benefit.

---

## See Also

- Conceptual guide: [`docs/how-to/MAF/per-tool-activities.md`](../../../docs/how-to/MAF/per-tool-activities.md)
- Step-mode workflow loop: [`docs/architecture/MAF/agent-sessions-and-workflow-loop.md`](../../../docs/architecture/MAF/agent-sessions-and-workflow-loop.md)
- The MEAI counterpart for tool durability: [`samples/MEAI/DurableTools/`](../../MEAI/DurableTools/)
