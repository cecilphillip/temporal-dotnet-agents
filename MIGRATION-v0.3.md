# Migrating to v0.3 (`Temporalio.Extensions.Agents`)

v0.3.0 is a clean break for the Microsoft Agent Framework integration. The agent registration surface is consolidated around a single entry point — `AddDurableAgent` — and the legacy v0.2 surface has been removed without `[Obsolete]` aliases or compat shims.

This guide walks a v0.2.x user through every removed API and its v0.3 replacement, with concrete before/after snippets you can paste over your existing code.

---

## Why v0.3 is breaking

`Temporalio.Extensions.Agents` is pre-1.0. Per project policy, breaking changes ship cleanly between minor versions during the pre-1.0 phase rather than carrying forward `[Obsolete]` aliases that gradually accrete. The v0.3 redesign retires the v0.2 surface in one pass for three reasons:

1. **String-keyed configuration foot-guns.** `PerToolActivityOptions["apply_refund"]` had to match `AIFunctionFactory.Create(name: "apply_refund")` exactly. A typo silently fell back to the default retry policy and a non-idempotent write tool would re-fire on transient activity retry.
2. **Tools were registered in three places.** `AddDurableTools(...)` for the registry, `tools:` on `AsAIAgent(...)` for the schema, and `PerToolActivityOptions[name]` for retry policy. Adding a tool required edits in three files.
3. **Implicit constraints with no compile-time enforcement.** Step mode silently degraded if `UseFunctionInvocation()` was on the chat pipeline. There was no compile-time check and no runtime guard.

The v0.3 surface fixes all three: one `AddDurableAgent` call per agent, one `agent.AddTool(t, opts => opts.NoRetry())` for the canonical write-tool fix, and the chat pipeline composition is library-internal.

---

## At-a-glance summary

| Removed in v0.3 | Replacement |
|----------------|-------------|
| `opts.AddAIAgent(agent)` | `opts.AddDurableAgent("name", agent => { ... })` |
| `opts.AddAIAgentFactory(name, sp => agent)` | `opts.AddDurableAgent("name", agent => { agent.ChatClient = sp => ...; agent.AddTool(...); })` |
| `opts.AddAIAgents(params AIAgent[])` | One `AddDurableAgent` call per agent |
| `opts.EnablePerToolActivities = true` | Implicit — every `AddDurableAgent` is durable |
| `opts.PerToolActivityOptions["name"] = ...` | `agent.AddTool(t, opts => opts.NoRetry())` (or `WithMaxAttempts(n)` / `WithTimeout(t)`) |
| `opts.UseExternalHistory = true` | `opts.HistoryStore = sp => sp.GetRequiredService<TStore>()` (presence is the opt-in) |
| `services.UseExternalAgentHistory<TStore>()` | `services.AddSingleton<TStore>()` + `opts.HistoryStore = sp => sp.GetRequiredService<TStore>()` |
| `opts.TimeToLive` | `opts.DefaultTimeToLive` (per-agent: `agent.TimeToLive`) |
| `opts.ApprovalTimeout` | `opts.DefaultApprovalTimeout` (per-agent: `agent.ApprovalTimeout`) |
| `opts.ActivityTimeout` | `opts.DefaultActivityTimeout` (per-agent: `agent.ActivityTimeout`) |
| `opts.HeartbeatTimeout` | `opts.DefaultHeartbeatTimeout` (per-agent: `agent.HeartbeatTimeout`) |
| `opts.MaxEntryCount` | `opts.DefaultMaxEntryCount` (per-agent: `agent.MaxEntryCount`) |
| `opts.RetryPolicy` | `opts.DefaultRetryPolicy` (per-agent: `agent.RetryPolicy`) |
| `opts.HistoryReducer` | `opts.DefaultHistoryReducer` (per-agent: `agent.HistoryReducer`) |
| `opts.MaxToolCallsPerTurn` (worker-level) | `agent.MaxToolCallsPerTurn` (per-agent; default `20`, no worker fallback) |
| `AddDurableTools(...)` for MAF agent tools | `agent.AddTool(...)` on the builder |

`AddDurableTools` itself is **not** removed — MEAI users (`Temporalio.Extensions.AI` / `DurableChatWorkflow`) still call it. Only the MAF agent path subsumes it via `agent.AddTool`.

---

## Concrete before/after

Each section below is a self-contained `builder.Services...` block. The `// changed:` comments call out the lines that move.

### 1. Simple agent

A single agent with one tool. The v0.2 form built the `AIAgent` in user code and handed it to `AddAIAgent`. The v0.3 form pushes registration onto the builder and uses a DI factory for the chat client.

**v0.2 (before):**

```csharp
var weatherAgent = openAiClient
    .GetChatClient("gpt-4o-mini")
    .AsAIAgent(
        name: "WeatherAgent",
        instructions: "You are a weather specialist.",
        tools: [getWeatherTool]);

builder.Services
    .AddHostedTemporalWorker("localhost:7233", "default", "agents")
    .AddTemporalAgents(opts =>
    {
        opts.AddAIAgent(weatherAgent);
    });
```

**v0.3 (after):**

```csharp
// Register the chat client in DI once — the agent factory pulls it from the container.
builder.Services.AddChatClient(openAiClient.GetChatClient("gpt-4o-mini").AsIChatClient());

builder.Services
    .AddHostedTemporalWorker("localhost:7233", "default", "agents")
    .AddTemporalAgents(opts =>
    {
        opts.AddDurableAgent("WeatherAgent", agent =>
        {
            agent.Instructions = "You are a weather specialist.";
            agent.ChatClient   = sp => sp.GetRequiredService<IChatClient>();
            agent.AddTool(getWeatherTool);
        });
    });
```

### 2. DI-resolved agent (the `AddAIAgentFactory` case)

In v0.2 the factory ran at activity time so DI services were available. In v0.3 every slot on the builder accepts a `Func<IServiceProvider, T>`, so DI access is uniform without a separate factory overload.

**v0.2 (before):**

```csharp
builder.Services.AddSingleton<OrderService>();

builder.Services
    .AddHostedTemporalWorker(taskQueue)
    .AddTemporalAgents(opts =>
    {
        opts.AddAIAgentFactory("SupportAgent", sp =>
        {
            var orderService = sp.GetRequiredService<OrderService>();
            var lookupTool = AIFunctionFactory.Create(orderService.LookupOrder, "lookup_order");

            return openAiClient
                .GetChatClient("gpt-4o")
                .AsAIAgent(
                    name: "SupportAgent",
                    instructions: "Help the customer.",
                    tools: [lookupTool]);
        });
    });
```

**v0.3 (after):**

```csharp
builder.Services.AddSingleton<OrderService>();
builder.Services.AddChatClient(openAiClient.GetChatClient("gpt-4o").AsIChatClient());

builder.Services
    .AddHostedTemporalWorker(taskQueue)
    .AddTemporalAgents(opts =>
    {
        opts.AddDurableAgent("SupportAgent", agent =>
        {
            agent.Instructions = "Help the customer.";
            agent.ChatClient   = sp => sp.GetRequiredService<IChatClient>();

            // DI-resolved tool — the factory runs at first activity dispatch.
            agent.AddTool(
                "lookup_order",
                sp => AIFunctionFactory.Create(
                    sp.GetRequiredService<OrderService>().LookupOrder,
                    "lookup_order"));
        });
    });
```

The chat client, tools, context providers, and history store all share the same lazy-factory lifecycle — composed once at first activity dispatch and cached for the worker's lifetime.

### 3. Per-tool retry (the canonical foot-gun fix)

This is the most impactful change. v0.2 required three coordinated edits — registry, agent factory, options dictionary — with the `apply_refund` string repeated as a magic key. v0.3 binds the retry policy to the `AIFunction` reference at registration time.

**v0.2 (before):**

```csharp
builder.Services
    .AddHostedTemporalWorker(taskQueue)
    .AddDurableAI(_ => { })
    .AddDurableTools(lookupOrderTool, applyRefundTool, sendEmailTool)
    .AddTemporalAgents(opts =>
    {
        opts.EnablePerToolActivities = true;
        opts.PerToolActivityOptions ??= new();
        opts.PerToolActivityOptions["apply_refund"] = new ActivityOptions
        {
            StartToCloseTimeout = TimeSpan.FromSeconds(30),
            RetryPolicy         = new RetryPolicy { MaximumAttempts = 1 },
        };
        opts.PerToolActivityOptions["send_email"] = new ActivityOptions
        {
            StartToCloseTimeout = TimeSpan.FromSeconds(30),
            RetryPolicy         = new RetryPolicy { MaximumAttempts = 1 },
        };

        opts.AddAIAgentFactory("RefundAgent", sp =>
            openAiClient.GetChatClient(model)
                .AsAIAgent(
                    name: "RefundAgent",
                    instructions: "...",
                    tools: [lookupOrderTool, applyRefundTool, sendEmailTool])); // schema only
    });
```

**v0.3 (after):**

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
            agent.Instructions = "You are a refund specialist.";
            agent.ChatClient   = sp => sp.GetRequiredService<IChatClient>();

            // Read tool — inherits worker default retry policy.
            agent.AddTool(sp => AIFunctionFactory.Create(
                sp.GetRequiredService<OrderService>().LookupOrder,
                "lookup_order"));

            // Write tools — bind NoRetry() to the AIFunction reference. No string keys.
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

What changed:
- No `AddDurableAI(_ => { })` and no separate `AddDurableTools(...)` for the MAF path. (MEAI's `DurableChatWorkflow` users still need both.)
- No `EnablePerToolActivities = true` — every `AddDurableAgent` is durable by definition.
- No `PerToolActivityOptions` dictionary — `agent.AddTool(t, opts => opts.NoRetry())` binds the retry policy to the tool reference. A typo on the tool name is a build error.
- No "must not call `UseFunctionInvocation()`" caveat — the library composes the chat pipeline internally with `UseProvidedChatClientAsIs = true`.

### 4. External history store (single agent)

In v0.2, opting an agent into an external history store required two things: a flag (`UseExternalHistory = true`) and a DI registration for `IAgentHistoryStore`. v0.3 fuses them into a single factory slot — the presence of a non-null `HistoryStore` is the opt-in.

**v0.2 (before):**

```csharp
builder.Services.AddSingleton<MyHistoryStore>();

builder.Services
    .AddHostedTemporalWorker("localhost:7233", "default", "agents")
    .UseExternalAgentHistory<MyHistoryStore>()    // registers + sets UseExternalHistory = true
    .AddTemporalAgents(opts =>
    {
        opts.AddAIAgent(agent);
    });

// — or, the equivalent two-call form —
builder.Services.AddSingleton<IAgentHistoryStore, MyHistoryStore>();

builder.Services
    .AddHostedTemporalWorker("localhost:7233", "default", "agents")
    .AddTemporalAgents(opts =>
    {
        opts.UseExternalHistory = true;
        opts.AddAIAgent(agent);
    });
```

**v0.3 (after):**

```csharp
builder.Services.AddSingleton<MyHistoryStore>();
builder.Services.AddChatClient(chatClient);

builder.Services
    .AddHostedTemporalWorker("localhost:7233", "default", "agents")
    .AddTemporalAgents(opts =>
    {
        // Worker-level default — every durable agent on this worker uses MyHistoryStore
        // unless it overrides via agent.HistoryStore.
        opts.HistoryStore = sp => sp.GetRequiredService<MyHistoryStore>();

        opts.AddDurableAgent("MyAgent", agent =>
        {
            agent.ChatClient = sp => sp.GetRequiredService<IChatClient>();
        });
    });
```

The `IAgentHistoryStore` interface itself is unchanged — your existing `LoadAsync` / `AppendAsync` / `ReplaceAsync` implementation moves over without modification.

### 5. External history store — per-agent override

The worker default applies to every durable agent. To use a different store for one agent (e.g., a regulated agent that needs PHI segregation), set `agent.HistoryStore` on its builder.

**v0.3:**

```csharp
builder.Services.AddSingleton<MyStore>();
builder.Services.AddSingleton<HipaaStore>();

builder.Services
    .AddHostedTemporalWorker(taskQueue)
    .AddTemporalAgents(opts =>
    {
        // Worker-level default for unscoped agents.
        opts.HistoryStore = sp => sp.GetRequiredService<MyStore>();

        opts.AddDurableAgent("StandardAgent", agent =>
        {
            agent.ChatClient = sp => sp.GetRequiredService<IChatClient>();
            // Inherits opts.HistoryStore — no override needed.
        });

        opts.AddDurableAgent("ComplianceAgent", agent =>
        {
            agent.ChatClient = sp => sp.GetRequiredService<IChatClient>();
            // Per-agent override — wins over the worker default.
            agent.HistoryStore = sp => sp.GetRequiredService<HipaaStore>();
        });
    });
```

The inheritance rule for every per-agent setting is the same: **per-agent value if set, else worker-level default.** There is no per-agent explicit-disable mechanism — to opt one agent on a worker out of an external store, split that agent into a separate worker registration with no `opts.HistoryStore` set.

### 6. Worker timeout and policy renames

Every worker-level scalar that has a per-agent override now uses the `Default*` prefix on `TemporalAgentsOptions`. The unprefixed names from v0.2 are gone.

**v0.2 (before):**

```csharp
builder.Services
    .AddHostedTemporalWorker(taskQueue)
    .AddTemporalAgents(opts =>
    {
        opts.TimeToLive        = TimeSpan.FromDays(7);
        opts.ApprovalTimeout   = TimeSpan.FromHours(24);
        opts.ActivityTimeout   = TimeSpan.FromMinutes(10);
        opts.HeartbeatTimeout  = TimeSpan.FromMinutes(2);
        opts.MaxEntryCount     = 500;
        opts.RetryPolicy       = new RetryPolicy { MaximumAttempts = 3 };
        opts.HistoryReducer    = entries => entries.TakeLast(50).ToList();

        opts.AddAIAgent(agent);
    });
```

**v0.3 (after):**

```csharp
builder.Services
    .AddHostedTemporalWorker(taskQueue)
    .AddTemporalAgents(opts =>
    {
        opts.DefaultTimeToLive       = TimeSpan.FromDays(7);
        opts.DefaultApprovalTimeout  = TimeSpan.FromHours(24);
        opts.DefaultActivityTimeout  = TimeSpan.FromMinutes(10);
        opts.DefaultHeartbeatTimeout = TimeSpan.FromMinutes(2);
        opts.DefaultMaxEntryCount    = 500;
        opts.DefaultRetryPolicy      = new RetryPolicy { MaximumAttempts = 3 };
        opts.DefaultHistoryReducer   = entries => entries.TakeLast(50).ToList();

        opts.AddDurableAgent("MyAgent", agent =>
        {
            agent.ChatClient = sp => sp.GetRequiredService<IChatClient>();

            // Per-agent overrides win over the Default* worker values.
            agent.ActivityTimeout = TimeSpan.FromMinutes(30);
            agent.MaxEntryCount   = 200;
        });
    });
```

`MaxToolCallsPerTurn` was previously a worker-level setting; in v0.3 it is **per-agent only** and defaults to `20`. There is no worker-level fallback for it.

---

## In-flight workflow upgrade story

> **Active v0.2 agent sessions cannot be migrated in place.** The workflow input shape, activity signatures, and workflow code all change incompatibly between v0.2 and v0.3. A v0.3 worker cannot resume a v0.2 in-flight `AgentWorkflow` — the deserialization will fail or non-deterministically diverge. Before deploying v0.3 workers, drain or terminate all running agent sessions.

The simplest deployment sequence:

1. **Stop submitting new agent workflows to v0.2 workers.** Pause your client side (gateway, scheduled jobs, scheduled triggers).
2. **Wait for in-flight workflows to complete**, or terminate them via the Temporal CLI / Web UI if you cannot wait. `temporal workflow list --query 'WorkflowType="AgentWorkflow" AND ExecutionStatus="Running"'` shows what is still in flight.
3. **Deploy v0.3 workers.** Workers compiled against v0.3 packages will only pick up new workflows started against the v0.3 surface.
4. **Resume normal traffic.** Clients can now submit workflows that the v0.3 workers will handle end-to-end.

If you need more graceful handling — e.g., long-running agent sessions you cannot reasonably drain — consider running both versions of your worker in parallel on different task queues during the transition, then redirecting client traffic over time once the v0.2 task queue has emptied.

---

## New capabilities worth knowing about

The v0.3 builder exposes a few APIs that did not have v0.2 equivalents:

- **`agent.AddContextProvider(...)`** — first-class registration for `AIContextProvider` instances, available in two overloads:
  - `agent.AddContextProvider(providerInstance)` — concrete instance
  - `agent.AddContextProvider(sp => providerInstance)` — DI factory invoked at first activity dispatch

  In v0.2 you attached providers via `ChatClientAgentOptions.AIContextProviders`; in v0.3 the agent's chat pipeline is composed by the library so providers are registered on the builder instead. `InvokingAsync` / `InvokedAsync` fire **once per LLM call** in durable mode — make these hooks idempotent and cheap, or cache via `StateBag`.

- **Fluent per-tool sugar.** `DurableToolOptions` exposes three convenience methods so the `opts =>` lambda stays readable:
  - `opts.NoRetry()` — equivalent to `RetryPolicy = new() { MaximumAttempts = 1 }`
  - `opts.WithMaxAttempts(n)` — fixed-attempt policy
  - `opts.WithTimeout(t)` — sets `StartToCloseTimeout`

  These compose: `opts => opts.NoRetry().WithTimeout(TimeSpan.FromSeconds(30))` is valid.

- **DI-factory-per-slot pattern.** Every slot on the builder that needs a runtime service — `ChatClient`, `AddTool(name, factory)`, `AddContextProvider(factory)`, `HistoryStore` — accepts a `Func<IServiceProvider, T>`. There is no need to call `services.BuildServiceProvider()` inside the configure delegate to bootstrap dependencies; the library invokes each factory once at first activity dispatch with the worker's runtime `IServiceProvider`.

- **Per-agent override of every worker default.** Set `agent.TimeToLive`, `agent.ActivityTimeout`, `agent.MaxEntryCount`, etc. on the builder to override the corresponding `opts.Default*` value. Leave them `null` (the default) to inherit.

- **Three-layer per-tool retry hierarchy.** From most to least specific:
  1. `agent.AddTool(t, opts => opts.RetryPolicy = ...)` — per-tool override
  2. `agent.RetryPolicy` — agent-level default for the LLM-call activity (`RunDurableAgentStep`); does **not** cascade to tools
  3. `opts.DefaultRetryPolicy` — worker-level default for both `RunDurableAgentStep` and `InvokeAgentTool`

  Set per-tool when the tool is non-idempotent. Set `agent.RetryPolicy` for slow-LLM tolerance. Leave both null and let `opts.DefaultRetryPolicy` cover everything else.

---

## Cross-references

- [`docs/how-to/MAF/usage.md`](docs/how-to/MAF/usage.md) — canonical reference for `AddDurableAgent`, the full `DurableAgentBuilder` and `DurableToolOptions` reference tables, and the inheritance rule.
- [`docs/how-to/MAF/durable-agents.md`](docs/how-to/MAF/durable-agents.md) — durable-agent semantics, the per-tool retry-policy hierarchy, and the canonical write-tool example.
- [`docs/how-to/MAF/external-history-store.md`](docs/how-to/MAF/external-history-store.md) — `opts.HistoryStore` worker default, per-agent override pattern, and the `IAgentHistoryStore` contract.
- [`docs/architecture/MAF/agent-sessions-and-workflow-loop.md`](docs/architecture/MAF/agent-sessions-and-workflow-loop.md) — the workflow-loop internals that drive the durable dispatch path and continue-as-new behavior.
- [`docs/how-to/MAF/dos-and-donts.md`](docs/how-to/MAF/dos-and-donts.md) — workflow-determinism rules and the write-tool `NoRetry()` convention.
