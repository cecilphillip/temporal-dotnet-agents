# Durable Agents

In v0.3, every agent registered with `AddDurableAgent` is a **durable agent**: each LLM call runs in a separate `RunDurableAgentStep` activity, and each tool call runs in a separately named `InvokeAgentTool` activity dispatched in parallel via `Workflow.WhenAllAsync`. There is no separate "step mode" toggle — durable agents are the only registration path.

This makes per-tool retry granularity explicit and prevents the legacy foot-guns where write-style tools could re-fire on a transient activity retry.

## When to use what

- **Read tools** (lookup, query, fetch): leave the per-tool retry policy unset; they fall through to the worker default (or per-agent default), which is normally unbounded retries.
- **Write tools** (send_email, apply_refund, write_record): always pass `opts => opts.NoRetry()` (or set a small `MaximumAttempts`) so a worker crash cannot re-issue the side effect.

## Canonical example

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
            agent.ChatClient = sp => sp.GetRequiredService<IChatClient>();
            agent.Instructions = "You are a refund specialist...";
            agent.MaxToolCallsPerTurn = 10;

            // Read tool — retries on transient failure (default unbounded).
            agent.AddTool(sp => AIFunctionFactory.Create(
                sp.GetRequiredService<OrderService>().LookupOrder,
                "lookup_order"));

            // Write tools — never retry, never re-fire on activity-level retry.
            agent.AddTool(
                sp => AIFunctionFactory.Create(
                    sp.GetRequiredService<RefundService>().ApplyRefund,
                    "apply_refund"),
                opts => opts.NoRetry());

            agent.AddTool(
                sp => AIFunctionFactory.Create(
                    sp.GetRequiredService<EmailService>().SendEmail,
                    "send_email"),
                opts => opts.NoRetry());
        });
    });
```

No `BuildServiceProvider()` bootstrap. No `EnablePerToolActivities` flag. No string-keyed dictionary. No `AddDurableTools` separate registration. No "don't use `UseFunctionInvocation()`" caveat — the library composes the chat pipeline correctly internally.

## Fluent sugar on `DurableToolOptions`

```csharp
opts => opts.NoRetry()              // RetryPolicy { MaximumAttempts = 1 }
opts => opts.WithMaxAttempts(3)
opts => opts.WithTimeout(TimeSpan.FromSeconds(30))
```

## Per-tool retry policy hierarchy

For every tool dispatched as a Temporal activity (`InvokeAgentTool`), the effective retry policy is:

1. The tool's `DurableToolOptions.RetryPolicy` if set (via the `configure` callback on `AddTool`)
2. Else the agent's `DurableAgentBuilder.RetryPolicy`
3. Else the worker's `TemporalAgentsOptions.DefaultRetryPolicy`
4. Else Temporal SDK defaults (unbounded retries)

The per-LLM-call activity (`RunDurableAgentStep`) follows the same chain, minus step 1.

## Sample

See [`samples/MAF/PerToolActivities/`](../../../samples/MAF/PerToolActivities/) for an end-to-end demonstration with intentionally injected lookup failures.
