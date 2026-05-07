# Do's and Don'ts

A consolidated reference of common mistakes and best practices when building with TemporalAgents. Each entry explains the rule, why it matters, and what to do instead.

---

## Table of Contents

1. [Workflow Determinism](#workflow-determinism)
2. [Agent Registration and DI](#agent-registration-and-di)
3. [Session and History Management](#session-and-history-management)
4. [Activity Timeouts](#activity-timeouts)
5. [Observability](#observability)
6. [Testing](#testing)
7. [Security and Configuration](#security-and-configuration)
8. [Per-Tool Activities (Step Mode)](#per-tool-activities-step-mode)
9. [Scheduling](#scheduling)

---

## Workflow Determinism

These rules apply to any code inside a `[Workflow]` class — including `[WorkflowRun]`, `[WorkflowUpdate]`, `[WorkflowSignal]`, and `[WorkflowQuery]` methods.

### Don't use wall-clock time in workflows

```csharp
// WRONG — non-deterministic on replay
var now = DateTime.UtcNow;
var now = DateTimeOffset.Now;

// CORRECT
var now = Workflow.UtcNow;
```

**Why:** Temporal replays workflow code deterministically from event history. `DateTime.UtcNow` returns a different value on each replay, causing the workflow to diverge from its recorded history and fail with a non-determinism error.

### Don't use Random or Guid.NewGuid() in workflows

```csharp
// WRONG — different value on each replay
var id = Guid.NewGuid();
var n = new Random().Next();

// CORRECT
var id = Workflow.NewGuid();
var n = Workflow.Random.Next();
```

**Why:** Same reason as wall-clock time — these produce different values on replay.

### Don't call ActivitySource.StartActivity() in workflow code

```csharp
// WRONG — OTel spans are non-deterministic side effects
using var span = mySource.StartActivity("my-span");

// CORRECT — agent spans are emitted in AgentActivities and DefaultTemporalAgentClient,
// both of which run outside the workflow execution context.
```

**Why:** `System.Diagnostics.Activity` creates spans with timestamps and IDs that differ on replay. All agent OTel spans (`agent.turn`, `agent.client.send`) are already emitted in the correct context — activities and client code.

### Don't query the agent registry in workflow code

```csharp
// WRONG — registry may change between original execution and replay
var names = options.GetRegisteredAgentNames();
var exists = options.IsAgentRegistered("MyAgent");

// CORRECT — wrap in an activity
var names = await Workflow.ExecuteActivityAsync(
    (RoutingActivities a) => a.GetAvailableAgents(),
    new ActivityOptions { StartToCloseTimeout = TimeSpan.FromSeconds(10) });
```

**Why:** If agents are added or removed between the original execution and a replay, the registry returns different results, causing a non-determinism error. Activity results are cached in history and replayed deterministically. See [Routing Patterns — Dynamic Routing via Activity](./routing.md#pattern-3-dynamic-routing-via-activity) for the full pattern.

### Don't use `Task.WhenAll` in workflow code — use `Workflow.WhenAllAsync`

```csharp
// WRONG — not the project convention
var results = await Task.WhenAll(toolTasks);

// CORRECT — workflow-safe SDK combinator
var results = await Workflow.WhenAllAsync(toolTasks);
```

**Why:** `Workflow.WhenAllAsync` is the SDK-provided workflow-safe combinator and the project convention. The XML doc on `TemporalWorkflowExtensions.ExecuteAgentsInParallelAsync` (`src/Temporalio.Extensions.Agents/TemporalWorkflowExtensions.cs:112`) describes it as "the workflow-safe equivalent of `Task.WhenAll`." `Task.WhenAll` is technically safe when every task comes from `Workflow.ExecuteActivityAsync` (those schedule on `TaskScheduler.Current`), but using the SDK method makes intent clear and stays consistent with the rest of the codebase.

### Don't use `ConfigureAwait(false)` in workflow code

```csharp
// WRONG — strips TaskScheduler.Current; the workflow hangs at CompleteWorkflowExecution
await Workflow.ExecuteActivityAsync(...).ConfigureAwait(false);

// CORRECT — stay on the workflow scheduler (omit, or use ConfigureAwait(true))
await Workflow.ExecuteActivityAsync(...);
await Workflow.ExecuteActivityAsync(...).ConfigureAwait(true);
```

**Why:** `ConfigureAwait(false)` bypasses the Temporal workflow scheduler's `SynchronizationContext`, putting the continuation on the ThreadPool. The workflow loses the ability to register `CompleteWorkflowExecution` and hangs at `WorkflowTaskCompleted`. This is a `[Workflow]`-only rule — `AgentActivities.cs` and `DefaultTemporalAgentClient.cs` correctly use `ConfigureAwait(false)` because they are not workflow code. The pattern also applies to `DurableAIFunction.cs` and `DurableEmbeddingGenerator.cs` (workflow-context middleware) — they explicitly note this in inline comments.

### Don't enable `EnablePerToolActivities` without also calling `AddDurableTools()`

```csharp
// WRONG — registrar throws InvalidOperationException at startup
builder.Services
    .AddHostedTemporalWorker(taskQueue)
    .AddTemporalAgents(opts =>
    {
        opts.EnablePerToolActivities = true;   // step mode dispatches InvokeFunction
        opts.AddDurableAgent("MyAgent", a => a.ChatClient = sp => sp.GetRequiredService<IChatClient>());
    });

// CORRECT — register tools in DurableFunctionRegistry first
builder.Services
    .AddHostedTemporalWorker(taskQueue)
    .AddDurableAI(opts => { /* ... */ })
    .AddDurableTools(sendEmailTool, lookupOrderTool)
    .AddTemporalAgents(opts =>
    {
        opts.EnablePerToolActivities = true;
        opts.AddDurableAgent("MyAgent", a => a.ChatClient = sp => sp.GetRequiredService<IChatClient>());
    });
```

**Why:** Step mode's per-tool activity is `DurableFunctionActivities.InvokeFunctionAsync`, which resolves tools by name from `DurableFunctionRegistry`. That registry is populated by `AddDurableTools(...)`, which is wired up by `AddDurableAI()`. `TemporalAgentsRegistrar` validates this at startup and throws `InvalidOperationException` if `DurableFunctionActivities` is not registered when the flag is on — silent fallback would defeat the per-tool retry guarantee callers opted in for. See [Per-Tool Temporal Activities (Step Mode)](./durable-agents.md).

### Do use GetAgent() with string constants or activity results

```csharp
// GOOD — string literal, deterministic
var agent = TemporalWorkflowExtensions.GetAgent("WeatherAgent");

// GOOD — agent name from a cached activity result
var agentName = await Workflow.ExecuteActivityAsync(
    (RoutingActivities a) => a.ValidateAgent(chosenName, "FallbackAgent"),
    new ActivityOptions { StartToCloseTimeout = TimeSpan.FromSeconds(10) });
var agent = TemporalWorkflowExtensions.GetAgent(agentName);
```

---

## Agent Registration and DI

### Do use the fluent API for registration

```csharp
// GOOD
builder.Services
    .AddHostedTemporalWorker("localhost:7233", "default", "agents")
    .AddTemporalAgents(opts =>
    {
        opts.AddDurableAgent("Agent", a => a.ChatClient = sp => sp.GetRequiredService<IChatClient>());
    });
```

`AddTemporalAgents` registers the workflow, activities, keyed proxies, and `ITemporalAgentClient` in a single call.

### Don't call builder.Build() twice

```csharp
// WRONG — building twice creates separate DI containers
var host1 = builder.Build();
var host2 = builder.Build(); // throws or creates a broken second container

// CORRECT
var host = builder.Build();
await host.StartAsync();
```

### Do use AddAIAgentFactory for agents that need DI services

```csharp
opts.AddAIAgentFactory("MyAgent",
    sp => new MyAgent(sp.GetRequiredService<IMyService>()));
```

The factory is invoked once at activity time (not at registration time), so all DI services are fully initialized.

### Do use async factories for agents that need startup I/O

```csharp
opts.AddAIAgentFactory("McpAgent", async sp =>
{
    var mcpClient = await McpClientFactory.CreateAsync(
        new SseServerTransport("http://localhost:3000/sse"));
    var tools = await mcpClient.ListToolsAsync();
    return chatClient.AsAIAgent("McpAgent", tools: [.. tools]);
});
```

---

## Session and History Management

### Don't reuse a TemporalAIAgent instance for independent conversations

```csharp
// WRONG — session2 sees session1's history because they share the instance
var agent = TemporalWorkflowExtensions.GetAgent("Analyst");
var s1 = await agent.CreateSessionAsync();
await agent.RunAsync("Question A", s1);
var s2 = await agent.CreateSessionAsync();
await agent.RunAsync("Question B", s2); // sees "Question A" in context!

// CORRECT — separate instances have independent histories
var agent1 = TemporalWorkflowExtensions.GetAgent("Analyst");
var agent2 = TemporalWorkflowExtensions.GetAgent("Analyst");
var s1 = await agent1.CreateSessionAsync();
var s2 = await agent2.CreateSessionAsync();
```

**Why:** `TemporalAIAgent` stores conversation history on the instance. Two sessions on the same instance accumulate into a single history list.

### Do use explicit session keys for deterministic routing

```csharp
// One session per user — always routes to the same workflow
var sessionId = new TemporalAgentSessionId("MyAgent", userId);
var session = new TemporalAgentSession(sessionId);
```

### Don't serialize TemporalAgentSession directly

```csharp
// WRONG — TemporalAgentSession is not in the source-gen JSON context
JsonSerializer.Serialize(session, DefaultOptions);

// CORRECT — use StateBag.Serialize() directly for state persistence
var serializedBag = session.StateBag.Serialize();
```

> **Note:** `SerializeStateBag()` is an `internal` method on `TemporalAgentSession` used by the
> framework itself (in `AgentActivities`). It is not part of the public API. User code should call
> `session.StateBag.Serialize()` directly when state persistence is required.

---

## Activity Timeouts

### Do set appropriate timeouts for your use case

```csharp
opts.DefaultActivityTimeout = TimeSpan.FromMinutes(10); // for fast models
opts.DefaultActivityTimeout = TimeSpan.FromHours(24);   // for HITL approval
```

The default (5 minutes) is reasonable for most LLM calls, but HITL approval flows need much longer timeouts to accommodate human review time.

### Do set HeartbeatTimeout shorter than ActivityTimeout

```csharp
// GOOD — heartbeats every 5 min, total timeout 30 min
opts.DefaultActivityTimeout    = TimeSpan.FromMinutes(30);
opts.DefaultHeartbeatTimeout   = TimeSpan.FromMinutes(5);

// BAD — heartbeat timeout longer than activity timeout defeats the purpose
opts.DefaultActivityTimeout    = TimeSpan.FromMinutes(5);
opts.DefaultHeartbeatTimeout   = TimeSpan.FromMinutes(30);
```

**Why:** `HeartbeatTimeout` detects stuck activities by checking for periodic progress signals. If it exceeds `ActivityTimeout`, the activity times out before a heartbeat check can trigger.

### Do pass ActivityOptions when using GetAgent() for workflow sub-agents

```csharp
var agent = TemporalWorkflowExtensions.GetAgent(
    "ResearcherAgent",
    activityOptions: new ActivityOptions
    {
        StartToCloseTimeout = TimeSpan.FromMinutes(5),
        HeartbeatTimeout    = TimeSpan.FromMinutes(1)
    });
```

The global `TemporalAgentsOptions` timeouts only apply to `AgentWorkflow`-based sessions. Workflow sub-agents use their own `ActivityOptions`.

---

## Observability

### Do register all four ActivitySource names

```csharp
// WRONG — missing agent spans
builder.AddSource(TracingInterceptor.ClientSource.Name);

// CORRECT — all four sources
builder.AddSource(
    TracingInterceptor.ClientSource.Name,
    TracingInterceptor.WorkflowsSource.Name,
    TracingInterceptor.ActivitiesSource.Name,
    TemporalAgentTelemetry.ActivitySourceName);
```

**Why:** Each source emits a different layer of the trace hierarchy. Missing one creates gaps in your distributed traces. See [Observability](./observability.md) for the full setup.

### Do reach for `ChatClientFactory` interception when you need per-LLM-call visibility

The `agent.turn` span captures one whole turn (LLM call plus all tool rounds). If you need to see each individual LLM request/response — token counts per round, finish reason per round, the exact tool-call payloads — wrap the inner `IChatClient` via the `clientFactory` parameter of `AsAIAgent(...)`:

```csharp
var agent = chatClient.AsAIAgent(
    name: "Assistant",
    instructions: "...",
    tools: [...],
    clientFactory: client => client.AsBuilder()
        .Use(inner => new LoggingChatClient(inner, logger))
        .UseFunctionInvocation()
        .Build());
```

**Why:** Per-LLM-call observability is a different problem from per-tool durability. Adding a logging decorator changes nothing about Temporal's checkpoint shape; it just adds round-level detail to your existing telemetry. See [LLM-Call Interception](./llm-call-interception.md) for the full guide and [`docs/design-decisions.md`](../../design-decisions.md) for why granular tool dispatch is deferred.

### Do opt in to search attributes when you need them

Search attribute upserts are disabled by default. Set `EnableSearchAttributes = true` in `TemporalAgentsOptions` to enable them:

```csharp
opts.EnableSearchAttributes = true;
```

When enabled, register the three attributes on production clusters before starting the worker:

```bash
temporal operator search-attribute create --name AgentName --type Keyword
temporal operator search-attribute create --name SessionCreatedAt --type Datetime
temporal operator search-attribute create --name TurnCount --type Int
```

With `temporal server start-dev` these are auto-created when present, but production clusters require explicit registration. If you enable `EnableSearchAttributes` without pre-registering the attributes, the workflow fails with an opaque "unexpected workflow task failure".

---

## Testing

### Do use exact exception types with Assert.Throws

```csharp
// WRONG — xUnit requires exact type match
Assert.Throws<ArgumentException>(() => Foo(null));

// CORRECT
Assert.Throws<ArgumentNullException>(() => Foo(null));
Assert.Throws<ArgumentException>(() => Foo(""));
```

**Why:** xUnit's `Assert.Throws<T>` matches the **exact** type, not subtypes. `ArgumentNullException` inherits from `ArgumentException`, but `Assert.Throws<ArgumentException>` will fail if `ArgumentNullException` is thrown.

### Do use TestEnvironmentHelper.StartLocalAsync() for Agents integration tests

```csharp
// Temporalio.Extensions.Agents integration tests
var env = await TestEnvironmentHelper.StartLocalAsync();
```

`AgentWorkflow` calls `UpsertTypedSearchAttributes` only when `EnableSearchAttributes = true`. If search attributes are enabled in your test fixture, the three custom attributes (`AgentName`, `SessionCreatedAt`, `TurnCount`) must be pre-registered when the embedded server starts — otherwise the workflow fails with an opaque "unexpected workflow task failure". `TestEnvironmentHelper.StartLocalAsync()` passes the required `--search-attribute` CLI args to `WorkflowEnvironment.StartLocalAsync()` automatically.

If `EnableSearchAttributes` is left at its default (`false`), bare `WorkflowEnvironment.StartLocalAsync()` works fine for Agents integration tests too. It is always appropriate for `Temporalio.Extensions.AI` integration tests, which use `DurableChatWorkflow` and never require custom search attributes:

```csharp
// Temporalio.Extensions.AI integration tests only — no custom search attributes needed
var env = await WorkflowEnvironment.StartLocalAsync();
```

Both approaches start an in-process Temporal server — no external process or Docker needed. See
[Testing Agents](./testing-agents.md) for the full fixture pattern.

### Do validate eagerly with string.IsNullOrEmpty + InvalidOperationException

```csharp
// GOOD — for configuration values
if (string.IsNullOrEmpty(apiKey))
    throw new InvalidOperationException("OPENAI_API_KEY is required.");

// LESS GOOD — ArgumentNullException implies a parameter, not a config value
if (apiKey is null) throw new ArgumentNullException(nameof(apiKey));
```

---

## Security and Configuration

### Don't commit real API keys in appsettings.json

```jsonc
// WRONG — checked into source control
{ "OPENAI_API_KEY": "sk-abc123..." }
```

```bash
# CORRECT — store secrets outside the repo using dotnet user-secrets
dotnet user-secrets set "OPENAI_API_KEY" "sk-..." --project <path-to-project>
```

All samples load secrets from `dotnet user-secrets`, which stores values in `~/.microsoft/usersecrets/` (outside the repo) and is automatically picked up by `Host.CreateApplicationBuilder()` in the Development environment.

### Do use NuGet packages for Temporal SDK dependencies

```xml
<!-- CORRECT -->
<PackageReference Include="Temporalio" Version="1.11.1" />

<!-- WRONG — requires Rust toolchain to build from source -->
<ProjectReference Include="path/to/sdk-dotnet/src/Temporalio/Temporalio.csproj" />
```

**Why:** The Temporal .NET SDK includes a native Rust bridge (`sdk-core-c-bridge`). Project references require the Rust toolchain to compile.

---

## Per-Tool Activities (Step Mode)

### Do set `MaximumAttempts = 1` for write-style tools

```csharp
opts.PerToolActivityOptions ??= new();

// Write tool — non-idempotent, must not double-fire on retry
opts.PerToolActivityOptions["send_email"] = new ActivityOptions
{
    StartToCloseTimeout = TimeSpan.FromSeconds(30),
    RetryPolicy = new RetryPolicy { MaximumAttempts = 1 },
};

// Read tool — idempotent, fall back to default unbounded retry
// (no entry needed; reads ActivityTimeout / RetryPolicy from TemporalAgentsOptions)
```

**Why:** In default mode, a transient failure mid-turn re-runs the entire turn — including any write tools that already succeeded. Step mode lets you scope retry to the tool: `MaximumAttempts = 1` tells Temporal not to re-execute the activity, so a write tool runs at most once per LLM-requested invocation. This is the primary reason `PerToolActivityOptions` exists. See [Per-Tool Temporal Activities (Step Mode)](./durable-agents.md).

### Do use `Workflow.WhenAllAsync` for parallel activity fan-out

```csharp
var tasks = toolCalls.Select(tc =>
    Workflow.ExecuteActivityAsync(
        (DurableFunctionActivities a) => a.InvokeFunctionAsync(BuildInput(tc)),
        ResolveToolActivityOptions(tc.Name))).ToList();

var results = await Workflow.WhenAllAsync(tasks);
```

**Why:** `Workflow.WhenAllAsync` is the workflow-safe combinator and preserves input order — `results[i]` corresponds to `tasks[i]`, which lets you correlate fan-out activity results to the requests that produced them without a side lookup. The step-mode loop in `AgentWorkflow.ExecuteStepModeTurnAsync` uses exactly this pattern.

---

## Scheduling

### Do delete schedules before decommissioning agents

```csharp
var handle = client.GetAgentScheduleHandle("daily-summary");
await handle.DeleteAsync();
```

**Why:** Temporal Schedules are independent of workers. Removing an agent from `TemporalAgentsOptions` does **not** delete its schedule — it continues firing and fails with `AgentNotRegisteredException`.

### Don't assume config-time schedule changes take effect on restart

```csharp
// This change is SILENTLY SKIPPED if the schedule already exists
opts.AddScheduledAgentRun("Agent", "my-schedule", request, updatedSpec);
```

**Why:** `ScheduleRegistrationService` catches `ScheduleAlreadyRunningException` and logs a warning. To apply updated specs, delete the schedule first. See [Scheduling](./scheduling.md#pitfalls-and-gotchas) for details.

---

## Quick Reference Table

| Rule | Category | Severity |
|------|----------|----------|
| Use `Workflow.UtcNow` not `DateTime.UtcNow` | Determinism | Fatal |
| Use `Workflow.NewGuid()` not `Guid.NewGuid()` | Determinism | Fatal |
| Don't query agent registry in workflows | Determinism | Fatal |
| Don't call `ActivitySource.StartActivity()` in workflows | Determinism | Fatal |
| Use `Workflow.WhenAllAsync`, not `Task.WhenAll`, in workflows | Determinism | Convention |
| Don't use `ConfigureAwait(false)` in `[Workflow]` code | Determinism | Workflow hangs |
| Set `MaximumAttempts = 1` on write tools in step mode | Step mode | Non-idempotent re-execution |
| Register all 4 OTel sources | Observability | Silent data loss |
| Set `ActivityTimeout` for HITL | Timeouts | Activity failure |
| Don't reuse `TemporalAIAgent` instances | Sessions | Incorrect behavior |
| Delete schedules before removing agents | Scheduling | Orphaned schedules |
| Use `dotnet user-secrets` for secrets | Security | Credential leak |
| Use exact exception types in xUnit | Testing | Test failures |

---

## References

- [Durability & Determinism](../architecture/MAF/durability-and-determinism.md) — replay guarantees and failure scenarios
- [Routing Patterns](./routing.md) — safe vs. unsafe registry access contexts
- [Observability](./observability.md) — OTel setup and span hierarchy
- [LLM-Call Interception](./llm-call-interception.md) — per-LLM-call decorators via `ChatClientFactory`
- [Testing Agents](./testing-agents.md) — test patterns and fixtures
- [Scheduling](./scheduling.md) — schedule lifecycle and pitfalls
- [Per-Tool Temporal Activities (Step Mode)](./durable-agents.md) — `EnablePerToolActivities`, write-tool retry pattern, iteration cap

---

_Last updated: 2026-05-05_
