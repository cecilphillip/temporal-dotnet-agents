# TemporalAgents Project Guide

**Two Temporal .NET SDK integrations for durable AI applications:**
- `Temporalio.Extensions.Agents` — durable agent sessions built on Microsoft Agent Framework (`Microsoft.Agents.AI`)
- `Temporalio.Extensions.AI` — makes any plain `IChatClient` (MEAI) durable, no Agent Framework required

This document provides essential context for working with the TemporalAgents codebase. It covers project structure, architecture, key patterns, and important behavioral guarantees.

---

## Quick Facts

- **Language**: C# (.NET 10.0)
- **Solution File**: `TemporalAgents.slnx` (.slnx format, not .sln)
- **Status**: Complete — 292 unit tests + 58 integration tests (350 total, all pass)
  - Agents: 214 unit + 51 integration
  - AI: 78 unit + 7 integration
- **Purpose**: Two complementary libraries — `Extensions.Agents` ports `Microsoft.Agents.AI.DurableTask` to Temporal; `Extensions.AI` adds MEAI-level durability without the Agent Framework
- **Key Pattern**: `[WorkflowUpdate]` replaces Signal+Query+polling for request/response

---

## Project Structure

```
TemporalAgents/
├── CLAUDE.md                               # This file
├── README.md                               # Umbrella README linking to both libraries
├── docs/
│   ├── architecture/
│   │   ├── MAF/                            # Internal design docs (Agents library)
│   │   │   ├── durability-and-determinism.md
│   │   │   ├── agent-sessions-and-workflow-loop.md
│   │   │   ├── session-statebag-and-context-providers.md
│   │   │   └── pub-sub-and-event-driven.md
│   │   └── MEAI/                           # Internal design docs (AI library) — stubs
│   │       └── durable-chat-pipeline.md
│   └── how-to/
│       ├── MAF/                            # Practical guides (Agents library)
│       │   ├── usage.md
│       │   ├── routing.md
│       │   └── ... (9 guides)
│       └── MEAI/                           # Practical guides (AI library) — stubs
│           ├── usage.md
│           ├── testing.md
│           ├── observability.md
│           └── hitl-patterns.md
├── TemporalAgents.slnx                     # Solution file (use this, not .sln)
│
├── src/
│   ├── Temporalio.Extensions.Agents/       # Agent Framework integration library
│   │   └── README.md                       # Library-specific docs
│   └── Temporalio.Extensions.AI/           # MEAI IChatClient middleware library
│       ├── README.md                       # Library-specific docs
│       ├── DurableChatClient.cs            # DelegatingChatClient middleware
│       ├── DurableChatWorkflow.cs          # [Workflow] managing session history + HITL
│       ├── DurableChatActivities.cs        # [Activity] wrapping IChatClient.GetResponseAsync
│       ├── DurableChatSessionClient.cs     # External entry point: ChatAsync, GetHistoryAsync, HITL
│       ├── DurableExecutionOptions.cs      # TaskQueue, ActivityTimeout, RetryPolicy, etc.
│       ├── DurableAIPayloadConverter.cs    # DurableAIDataConverter.Instance (AIJsonUtilities.DefaultOptions)
│       ├── DurableAIFunction.cs            # DelegatingAIFunction wrapping tool calls as activities
│       ├── DurableFunctionActivities.cs    # [Activity] resolving + invoking AIFunction from DI registry
│       ├── DurableEmbeddingGenerator.cs    # DelegatingEmbeddingGenerator for IEmbeddingGenerator
│       ├── DurableEmbeddingActivities.cs   # [Activity] wrapping IEmbeddingGenerator.GenerateAsync
│       ├── DurableChatReducer.cs           # IChatReducer preserving full history in workflow state
│       ├── DurableApprovalRequest.cs       # HITL request type (RequestId, FunctionName, Description)
│       ├── DurableApprovalDecision.cs      # HITL decision type (RequestId, Approved, Reason)
│       ├── DurableChatTelemetry.cs         # ActivitySource "Temporalio.Extensions.AI" + span constants
│       ├── ChatClientBuilderExtensions.cs  # UseDurableExecution(), UseDurableReduction()
│       ├── EmbeddingGeneratorBuilderExtensions.cs # UseDurableExecution() for embeddings
│       ├── DurableAIServiceCollectionExtensions.cs # AddDurableAI(), AddDurableTools()
│       ├── AIFunctionExtensions.cs         # AsDurable() extension on AIFunction
│       └── TemporalChatOptionsExtensions.cs # WithActivityTimeout(), WithMaxRetryAttempts(), etc.

│       ├── ServiceCollectionExtensions.cs  # GetTemporalAgentProxy, AddTemporalAgentProxies
│       ├── TemporalWorkerBuilderExtensions.cs # [NEW API] .AddTemporalAgents() fluent builder
│       ├── TemporalAgentsOptions.cs        # Configuration (internal ctor)
│       ├── ITemporalAgentClient.cs         # Interface: RunAgentAsync, RouteAsync, HITL
│       ├── DefaultTemporalAgentClient.cs   # Implementation using WorkflowUpdate + OTel
│       ├── AgentWorkflow.cs                # Durable session: history, HITL handlers, StateBag
│       ├── AgentActivities.cs              # Activity: calls real AIAgent, OTel span
│       ├── TemporalAIAgent.cs              # For workflow orchestration (sub-agent)
│       ├── TemporalAIAgentProxy.cs         # For external callers (proxy)
│       ├── TemporalWorkflowExtensions.cs   # GetAgent(), ExecuteAgentsInParallelAsync()
│       ├── AgentWorkflowWrapper.cs         # Wraps agent with request context
│       ├── TemporalAgentSession.cs         # Session with StateBag persistence
│       ├── TemporalAgentTelemetry.cs       # ActivitySource + span/attribute constants
│       ├── IAgentRouter.cs                 # Routing abstraction
│       ├── AIModelAgentRouter.cs               # LLM-backed router implementation
│       ├── AgentDescriptor.cs              # Name+description for routing
│       │   # HITL types: DurableApprovalRequest / DurableApprovalDecision (from Temporalio.Extensions.AI)
│       ├── ExecuteAgentResult.cs           # Internal: wraps AgentResponse + StateBag
│       ├── State/                          # Conversation history serialization
│       └── ...
│
├── tests/
│   ├── Temporalio.Extensions.Agents.Tests/       # 214 unit tests
│   │   ├── TemporalWorkerBuilderExtensionsTests.cs
│   │   ├── AIModelAgentRouterTests.cs
│   │   ├── RoutingOptionsTests.cs
│   │   ├── HITLTypesTests.cs
│   │   ├── StateBagPersistenceTests.cs
│   │   ├── TemporalAgentTelemetryTests.cs
│   │   ├── TemporalWorkflowExtensionsTests.cs
│   │   ├── Helpers/
│   │   │   ├── StubAIAgent.cs              # Test double: implements CreateSessionCoreAsync
│   │   │   └── CapturingChatClient.cs      # Test double: records ChatOptions
│   │   └── ...
│   │
│   ├── Temporalio.Extensions.Agents.IntegrationTests/ # 51 integration tests (real Temporal server)
│   │
│   ├── Temporalio.Extensions.AI.Tests/           # 78 unit tests
│   │   ├── DurableExecutionOptionsTests.cs
│   │   ├── DurableChatClientTests.cs
│   │   ├── SerializationTests.cs
│   │   ├── DurableAIDataConverterTests.cs
│   │   ├── TemporalChatOptionsExtensionsTests.cs
│   │   ├── DurableChatReducerTests.cs
│   │   ├── DurableEmbeddingGeneratorTests.cs
│   │   ├── DurableApprovalTests.cs
│   │   └── ...
│   │
│   └── Temporalio.Extensions.AI.IntegrationTests/ # 7 integration tests (real Temporal server)
│       └── Helpers/
│           ├── TestChatClient.cs           # IChatClient stub returning canned responses
│           └── IntegrationTestFixture.cs   # WorkflowEnvironment.StartLocalAsync() + hosted worker
│
└── samples/
    ├── MEAI/                               # Microsoft.Extensions.AI samples
    │   ├── DurableChat/                    # Extensions.AI: multi-turn chat
    │   ├── DurableTools/                   # Extensions.AI: per-tool activity dispatch via AsDurable()
    │   ├── OpenTelemetry/                  # Extensions.AI: OTel tracing configuration
    │   ├── HumanInTheLoop/                 # Extensions.AI: HITL approval gates
    │   └── DurableEmbeddings/              # Extensions.AI: IEmbeddingGenerator in workflow context
    └── MAF/                                # Microsoft Agent Framework samples
        ├── BasicAgent/                     # Extensions.Agents: external caller pattern
        ├── SplitWorkerClient/              # Extensions.Agents: worker + client in separate processes
        ├── WorkflowOrchestration/          # Extensions.Agents: workflow sub-agent pattern
        ├── EvaluatorOptimizer/             # Extensions.Agents: generator+evaluator loop
        ├── MultiAgentRouting/              # Extensions.Agents: routing + parallel execution + OTel
        ├── HumanInTheLoop/                 # Extensions.Agents: HITL approval gates via WorkflowUpdate
        ├── WorkflowRouting/                # Extensions.Agents: routing inside a workflow
        └── AmbientAgent/                   # Extensions.Agents: ambient agent pattern
```

---

## Temporalio.Extensions.AI — Key Concepts

### Registration

```csharp
// 1. Connect Temporal client with MEAI-aware data converter (required for AIContent polymorphism)
var client = await TemporalClient.ConnectAsync(new("localhost:7233")
{
    DataConverter = DurableAIDataConverter.Instance,
    Namespace = "default"
});
builder.Services.AddSingleton<ITemporalClient>(client);

// 2. Register IChatClient in DI — DurableChatActivities injects this on the worker side
builder.Services.AddSingleton<IChatClient>(sp =>
    openAiClient.GetChatClient("gpt-4o-mini")
        .AsBuilder()
        .UseFunctionInvocation()
        .Build());

// 3. Register durable AI on the worker (workflow + activities + DurableChatSessionClient)
builder.Services
    .AddHostedTemporalWorker("my-queue")
    .AddDurableAI(opts =>
    {
        opts.ActivityTimeout = TimeSpan.FromMinutes(5);
        opts.SessionTimeToLive = TimeSpan.FromHours(24);
    });
```

### External Usage

```csharp
var sessionClient = host.Services.GetRequiredService<DurableChatSessionClient>();
var response = await sessionClient.ChatAsync("conv-123",
    [new ChatMessage(ChatRole.User, "Hello!")]);
```

### DurableAIDataConverter

**Must** set `DurableAIDataConverter.Instance` on the Temporal client when using MEAI types. Without it, `FunctionCallContent`, `FunctionResultContent`, and other `AIContent` subtypes lose their `$type` discriminator and deserialize as base `AIContent` after round-tripping through workflow history.

### Per-request Overrides

```csharp
var opts = new ChatOptions()
    .WithActivityTimeout(TimeSpan.FromMinutes(10))
    .WithMaxRetryAttempts(5)
    .WithHeartbeatTimeout(TimeSpan.FromMinutes(3));
```

Keys live in `TemporalChatOptionsExtensions` as `public const string` constants.

### Durable Tool Functions

- `AddDurableTools(workerBuilder, params aiFunction[])` — registers one or more tools in `DurableFunctionRegistry` (resolved by name in `DurableFunctionActivities`); chains on `ITemporalWorkerServiceOptionsBuilder` after `AddDurableAI()`
- `aiFunction.AsDurable()` — wraps as `DurableAIFunction`; passes through when not in workflow context (`Workflow.InWorkflow == false`)

### Context Detection

All middleware (`DurableChatClient`, `DurableAIFunction`, `DurableEmbeddingGenerator`, `DurableChatReducer`) uses `Workflow.InWorkflow` as the dispatch guard. `false` = pass through to inner; `true` = dispatch as Temporal activity.

### HITL

```csharp
// From external system
var pending = await sessionClient.GetPendingApprovalAsync("conv-123");
await sessionClient.SubmitApprovalAsync("conv-123", new DurableApprovalDecision
{
    RequestId = pending!.RequestId,
    Approved = true
});
```

### Important Notes

- `DurableChatActivities` is `internal` and registered as `AddSingletonActivities` — do not instantiate directly
- `DurableFunctionRegistry` is `internal Dictionary<string, AIFunction>` (case-insensitive) populated at startup
- Integration tests use `WorkflowEnvironment.StartLocalAsync()` (embedded Temporal CLI binary), not a separate server
- `IChatClient` must be registered in DI **before** `AddDurableAI` — the activities constructor-inject it
- Use `AddChatClient(innerClient).UseFunctionInvocation().Build()` (idiomatic MEAI DI pattern) instead of `AddSingleton<IChatClient>`; `UseDurableExecution()` chains onto the same builder
- `DurableChatActivities` injects the **unkeyed** `IChatClient` — if using `AddKeyedChatClient`, also register an unkeyed alias or the activities will fail to resolve at startup

---

## Temporalio.Extensions.Agents — Key Concepts

### 1. Registration API

```csharp
services.AddHostedTemporalWorker("localhost:7233", "default", "agents")
    .AddTemporalAgents(opts => opts.AddAIAgent(agent));
```

Composes with other worker configuration (e.g., `.ConfigureOptions(opts => opts.MaxConcurrentActivities = 20)`).

---

### 2. Two Agent Types

#### `TemporalAIAgent` (Workflow Context)
- **Use Case**: Inside a Temporal workflow calling a sub-agent
- **Access**: Via `TemporalWorkflowExtensions.GetAgent("AgentName")`

#### `TemporalAIAgentProxy` (External Context)
- **Use Case**: External caller (API server, CLI, console app)
- **Access**: Via `services.GetTemporalAgentProxy("AgentName")`

---

### 3. LLM-Powered Routing

```csharp
// Agents carry descriptions via AsAIAgent(description: ...) — auto-extracted on AddAIAgent()
var weatherAgent = chatClient.AsAIAgent(
    name: "WeatherAgent",
    description: "Handles weather questions.",
    instructions: "You are a weather specialist...");

builder.Services.AddHostedTemporalWorker("agents")
    .AddTemporalAgents(opts =>
    {
        opts.AddAIAgent(weatherAgent);   // description auto-extracted into descriptors
        opts.AddAIAgent(billingAgent);
        opts.SetRouterAgent(routerAgent);  // registers AIModelAgentRouter as IAgentRouter
    });

// External routing — LLM picks the specialist automatically
var response = await client.RouteAsync(sessionKey, new RunRequest(userMessage));
```

- `AddAIAgent()` auto-extracts `AIAgent.Description` into the descriptor registry (used by routing)
- `AddAgentDescriptor()` is still available for factory-registered agents or explicit overrides
- `SetRouterAgent` registers `AIModelAgentRouter` as `IAgentRouter` in DI automatically
- `AIModelAgentRouter` uses exact match then fuzzy (case-insensitive) fallback on the response text
- Throws `InvalidOperationException` if the LLM returns an unrecognized agent name

---

### 4. Parallel Agent Execution

Only valid **inside a `[Workflow]`** — uses `Workflow.WhenAllAsync` internally:

```csharp
var results = await TemporalWorkflowExtensions.ExecuteAgentsInParallelAsync(new[]
{
    (researchAgent, messages, researchSession),
    (summaryAgent,  messages, summarySession),
});
// IReadOnlyList<AgentResponse> in input order
```

---

### 5. Human-in-the-Loop (HITL)

From inside an **agent tool** (running inside an activity):
```csharp
var decision = await TemporalAgentContext.Current.RequestApprovalAsync(
    new DurableApprovalRequest { RequestId = Guid.NewGuid().ToString("N"), Description = "Delete records — Irreversible." });
if (!decision.Approved) throw new OperationCanceledException("Rejected.");
```

From an **external system** (e.g., an admin dashboard):
```csharp
var pending = await client.GetPendingApprovalAsync(sessionId);
var decision = await client.SubmitApprovalAsync(sessionId,
    new DurableApprovalDecision { RequestId = pending!.RequestId, Approved = true });
```

The workflow blocks on `WaitConditionAsync` during approval — the activity timeout on `RequestApprovalAsync` must be long enough to accommodate human review time.

---

### 6. StateBag Persistence

`AgentSessionStateBag` (used by AIContextProviders like `Mem0Provider`) is now persisted across turns:
- `AgentActivities.ExecuteAgentAsync` serializes the bag after each turn via `session.SerializeStateBag()`
- `AgentWorkflow` stores it in `_currentStateBag` and passes it forward in `ExecuteAgentInput`
- `TemporalAgentSession.FromStateBag` restores it at the start of each activity
- An **empty** bag returns `null` (checked via `StateBag.Count == 0`) — no wasted serialization

---

### 7. OpenTelemetry

The SDK's `TracingInterceptor` handles Temporal protocol spans; `TemporalAgentTelemetry` handles agent-semantic spans. They compose:

```
agent.client.send                     ← TemporalAgentTelemetry (agent name, session ID)
  UpdateWorkflow:RunAgent             ← TracingInterceptor SDK span
    RunActivity:ExecuteAgent          ← TracingInterceptor SDK span
      agent.turn                      ← TemporalAgentTelemetry (token counts, correlation ID)
```

Register **all four** sources:
```csharp
Sdk.CreateTracerProviderBuilder()
    .AddSource(
        TracingInterceptor.ClientSource.Name,
        TracingInterceptor.WorkflowsSource.Name,
        TracingInterceptor.ActivitiesSource.Name,
        TemporalAgentTelemetry.ActivitySourceName)  // "Temporalio.Extensions.Agents"
    .AddOtlpExporter()
    .Build();
```

**⚠️ Never** use `ActivitySource.StartActivity()` inside a `[Workflow]` class — use `ActivitySourceExtensions.TrackWorkflowDiagnosticActivity` instead (only needed for custom workflow spans; agent spans are in activities/client code).

---

## Critical: Durability and Determinism

**MUST READ**: [`docs/architecture/MAF/durability-and-determinism.md`](./docs/architecture/MAF/durability-and-determinism.md)

When a worker crashes:
- ✅ Completed agent calls are **not re-executed** — results are replayed from history
- ✅ `_currentStateBag` is carried forward through `AgentWorkflowInput.CarriedStateBag`
- ✅ Conversation history is serialized in workflow state across continue-as-new transitions

---

## Important Dependencies and Notes

### Temporal .NET SDK
- **Use NuGet packages** (`Temporalio 1.11.1`, `Temporalio.Extensions.Hosting 1.11.1`), NOT project references
- **Reason**: Rust native bridge (`sdk-core-c-bridge`) requires Rust toolchain to build from source
- **OTel extension**: `Temporalio.Extensions.OpenTelemetry 1.11.1` — matches SDK version

### Microsoft Agent Framework
- `Temporalio.Extensions.Agents` **depends on** `Temporalio.Extensions.AI` — no extra NuGet packages added since `Microsoft.Agents.AI` already pulls in `Microsoft.Extensions.AI` transitively
- HITL types are now the canonical MEAI types: `DurableApprovalRequest` / `DurableApprovalDecision` (from `Temporalio.Extensions.AI`)
- `AgentResponse`, `AIAgent`, `DelegatingAIAgent`, `AgentRunOptions` → `Microsoft.Agents.AI`
- `ChatClientAgentRunOptions` → `Microsoft.Agents.AI` (not the Hosting package)
- `AgentSessionStateBag.Count` — available, used to detect empty bag without serializing
- `AgentSessionStateBag.Serialize()` — uses its own `AgentAbstractionsJsonUtilities.DefaultOptions`

### MEAI v10 Breaking Changes
- `IChatClient.CompleteAsync` → `GetResponseAsync` (returns `Task<ChatResponse>`)
- `ChatCompletion` → `ChatResponse`
- `StreamingChatCompletionUpdate` → `ChatResponseUpdate`

### Key Type Locations
- `RpcException` — `Temporalio.Exceptions` (not Grpc.Core)
- `Workflow.CreateContinueAsNewException` — takes `Expression<Func<TWorkflow, Task>>` (no collection expressions inside)
- `WorkflowIdConflictPolicy.UseExisting` — `Temporalio.Api.Enums.V1`

### DI Patterns
- `TemporalAgentsOptions` — **internal constructor** (always access via delegate parameter)
- `IAgentRouter` — registered automatically as singleton when `SetRouterAgent` is called
- `TryAddSingleton` for `ITemporalAgentClient` — allows custom implementations
- `ActivatorUtilities.CreateInstance<T>(provider, taskQueue)` — pattern for extra constructor args

### JSON Serialization
- `TemporalAgentStateJsonContext` — source-generated context for conversation history types only
- `TemporalAgentSession` is **NOT** in the source-gen context — do not try to serialize it via `DefaultOptions.GetTypeInfo(typeof(TemporalAgentSession))` directly
- `TemporalAgentSession.SerializeStateBag()` — delegates to `StateBag.Serialize()`, not session serialization

---

## Testing Patterns

### Unit Tests (292 total — 214 Agents + 78 AI)
- **Framework**: xunit with `[Fact]` attributes
- **Assertions**: `Assert.*` — `Assert.Throws<T>` requires **exact** type, not subtype (use `Assert.Throws<ArgumentNullException>` for null, not `ArgumentException`)
- **Mocking**: Hand-written fakes/stubs preferred over Moq
- `StubAIAgent` — implements `CreateSessionCoreAsync` returning `new TemporalAgentSession(TemporalAgentSessionId.WithRandomKey(Name ?? "stub"))`
- `TestChatClient` — `IChatClient` stub for AI tests returning `"Response: {lastMessage}"` with token counts

### Integration Tests (58 total — 51 Agents + 7 AI)
- Both test suites use `WorkflowEnvironment.StartLocalAsync()` (embedded server — no external process needed)
- Agents tests use `TestEnvironmentHelper.StartLocalAsync()`, a thin wrapper that passes `--search-attribute` CLI args to pre-register the three custom search attributes (`AgentName`, `SessionCreatedAt`, `TurnCount`) required by `AgentWorkflow.UpsertTypedSearchAttributes`. Without pre-registration, the workflow fails with an opaque "unexpected workflow task failure" at runtime.
- AI tests use a bare `WorkflowEnvironment.StartLocalAsync()` — `DurableChatWorkflow` uses no custom search attributes.
- Location: `tests/Temporalio.Extensions.Agents.IntegrationTests/` and `tests/Temporalio.Extensions.AI.IntegrationTests/`

### InternalsVisibleTo
- Via MSBuild: `<InternalsVisibleTo Include="TestProject" />` in `.csproj`
- Internal types accessible in tests: `ExecuteAgentResult`, `ExecuteAgentInput`

---

## Workflow Best Practices

### ✅ DO

- **Use fluent API** — `.AddTemporalAgents()` on the worker builder
- **Use `GetAgent()`** — inside workflows for sub-agent orchestration
- **Use `Workflow.UtcNow`** — not `DateTime.UtcNow`
- **Use `Workflow.NewGuid()`** — not `Guid.NewGuid()` inside workflows
- **Set appropriate TTLs** — `timeToLive` per agent (default: 14 days)
- **Validate config eagerly** — use `string.IsNullOrEmpty` + `InvalidOperationException` for missing config values (not `is null` + `ArgumentNullException`)
- **Keep OTel spans out of workflows** — `agent.turn` is in `AgentActivities`, `agent.client.send` is in `DefaultTemporalAgentClient` — both are correct

### ❌ DON'T

- **Don't call `ActivitySource.StartActivity()` inside `[Workflow]`** — non-deterministic during replay
- **Don't use wall-clock time in workflows** — `DateTime.UtcNow`, `DateTimeOffset.Now`
- **Don't use `Random` or `Guid.NewGuid()` in workflows** — non-deterministic
- **Don't call `builder.Build()` twice** — assign `var host = builder.Build()` and keep the reference
- **Don't commit real API keys in `appsettings.json`** — use `appsettings.local.json` (gitignored) or environment variables

---

## Common Patterns

### Pattern 1: External Agent Call
```csharp
var proxy = services.GetTemporalAgentProxy("MyAgent");
var session = await proxy.CreateSessionAsync();
var response = await proxy.RunAsync(userMessage, session);
```

### Pattern 2: LLM-Powered Routing
```csharp
var response = await agentClient.RouteAsync(sessionKey, new RunRequest(userMessage));
```

### Pattern 3: Parallel Fan-out (inside workflow)
```csharp
var results = await TemporalWorkflowExtensions.ExecuteAgentsInParallelAsync(new[]
{
    (agentA, messages, sessionA),
    (agentB, messages, sessionB),
});
```

### Pattern 4: HITL Approval (inside a tool)
```csharp
var decision = await TemporalAgentContext.Current.RequestApprovalAsync(
    new DurableApprovalRequest { RequestId = Guid.NewGuid().ToString("N"), Description = "Deploy to production" });
```

### Pattern 5: Workflow Sub-Agent
```csharp
[WorkflowRun]
public async Task<string> RunAsync(string request)
{
    var agent = TemporalWorkflowExtensions.GetAgent("SubAgent");
    var session = await agent.CreateSessionAsync();
    return (await agent.RunAsync(request, session)).Text ?? string.Empty;
}
```

---

## Build Automation

Build automation uses [`just`](https://just.systems) (a `make`-like command runner). All common tasks are recipes in `justfile`. The .NET SDK version is pinned via `global.json` (10.0.x). Package versioning uses `minver-cli` as a local dotnet tool (`.config/dotnet-tools.json`).

### Prerequisites

```bash
# Install just (macOS)
brew install just

# Install minver-cli and any other local dotnet tools
dotnet tool restore
```

### Build

```bash
just build        # Restore + Release build (default)
just build-debug  # Restore + Debug build
just restore      # Restore packages only
just info         # Show solution, version, config, artifacts path
```

### Testing

```bash
just test-unit          # Agents unit tests (214) — no server required
just test-unit-ai       # AI unit tests (78) — no server required
just test-unit-all      # All unit tests (292) — no server required
just test-integration   # Agents integration tests (51) — uses embedded server via TestEnvironmentHelper
just test-integration-ai # AI integration tests (7) — uses embedded server (no external process)
just test               # All suites

just test-coverage      # Unit tests with XPlat Code Coverage (output: artifacts/packages/coverage/)
just test-filter "FullyQualifiedName~Router"  # Run tests matching a filter expression
```

> Both integration test suites use the embedded Temporal server and require no external process.
> Agents tests use `TestEnvironmentHelper.StartLocalAsync()` to pre-register custom search attributes.

### Packaging

```bash
just pack   # clean → build → pack → artifacts/packages/*.nupkg + *.snupkg
```

Packages land in `artifacts/packages/`. The version is computed automatically from the nearest git tag by MinVer:

| Git state | Example version |
|-----------|----------------|
| Exactly on `v1.0.0` tag | `1.0.0` |
| 3 commits after `v1.0.0` | `1.0.1-preview.3` |
| No tags in repo | `0.0.0-preview.{height}` |

To cut a release: `git tag -a v1.0.0 -m "Release 1.0.0"` then `just pack`.

### Publishing

```bash
# Publish to NuGet.org (requires NUGET_API_KEY env var)
just publish-nuget

# Publish to GitHub Packages (requires NUGET_GITHUB_TOKEN env var)
just publish-github
```

### Full CI pipeline (local)

```bash
just ci   # clean → build → test-unit → pack
```

Mirrors what GitHub Actions runs. Use this before pushing to verify the full pipeline locally.

### All recipes

```bash
just --list   # Print all available recipes with descriptions
```

---

## CI/CD — GitHub Actions

Pipeline defined in `.github/workflows/build.yml`. Three jobs:

| Job | Runs on | Triggered by |
|-----|---------|-------------|
| `build` | ubuntu + macOS matrix | every push to `main` |
| `package` | ubuntu | after `build` succeeds |
| `publish` | ubuntu | `workflow_dispatch` on `main` only |

**`build` job**: `dotnet tool restore` → `just build` → `just test-unit`
(Integration tests are excluded from CI — they require a live Temporal server.)

**`package` job**: full git history checkout (`fetch-depth: 0`, required for MinVer) → `just pack` → uploads `.nupkg` + `.snupkg` as a workflow artifact named `packages`.

**`publish` job**: downloads the pre-built artifact (no recompilation) → pushes to the registry selected via the `workflow_dispatch` dropdown (`GitHub` or `NuGet`).

### Required GitHub Secrets

| Secret | Used by |
|--------|---------|
| `NUGET_PAT` | Publish to GitHub Package Registry |
| `NUGET_API_KEY` | Publish to NuGet.org |

---

## Run Samples

```bash
# All samples require: temporal server start-dev + OPENAI_API_KEY in appsettings.json

# Temporalio.Extensions.AI sample
dotnet run --project samples/MEAI/DurableChat/DurableChat.csproj

# Temporalio.Extensions.Agents samples
dotnet run --project samples/MAF/BasicAgent/BasicAgent.csproj
dotnet run --project samples/MAF/WorkflowOrchestration/WorkflowOrchestration.csproj
dotnet run --project samples/MAF/EvaluatorOptimizer/EvaluatorOptimizer.csproj
dotnet run --project samples/MAF/MultiAgentRouting/MultiAgentRouting.csproj
dotnet run --project samples/MAF/HumanInTheLoop/HumanInTheLoop.csproj

# SplitWorkerClient — run Worker first, then Client in a separate terminal
dotnet run --project samples/MAF/SplitWorkerClient/Worker/Worker.csproj
dotnet run --project samples/MAF/SplitWorkerClient/Client/Client.csproj
```

---

## Architecture Diagram (Extended)

```
┌────────────────────────────────────────────────────────────────┐
│                    External Caller / Client                    │
└──────────┬──────────────────┬────────────────────┬────────────┘
           │                  │                    │
           │ GetTemporalAgent  │ ITemporalAgent      │ ITemporalAgent
           │ Proxy(name)       │ Client.RouteAsync   │ Client.SubmitApproval
           ▼                  ▼                    ▼
  ┌──────────────┐   ┌──────────────────┐   ┌──────────────────┐
  │ Temporal     │   │ DefaultTemporal  │   │ DefaultTemporal  │
  │ AIAgentProxy │   │ AgentClient      │   │ AgentClient      │
  └──────┬───────┘   │ + IAgentRouter   │   │ + HITL support   │
         │           └────────┬─────────┘   └────────┬─────────┘
         │                    │                      │
         └───────────────┬────┘                      │
                         │ ExecuteUpdateAsync         │
                         ▼                            ▼
              ┌──────────────────────────────────────────────────┐
              │                  AgentWorkflow                   │
              │  • conversation history (_history)               │
              │  • StateBag (_currentStateBag)                   │
              │  • HITL state (_pendingApproval)                 │
              │  • RunAgentAsync [WorkflowUpdate]                │
              │  • RequestApprovalAsync [WorkflowUpdate]         │
              │  • SubmitApprovalAsync [WorkflowUpdate]          │
              └──────────┬───────────────────────────────────────┘
                         │ ExecuteActivityAsync
                         ▼
              ┌──────────────────────────────────────────────────┐
              │          AgentActivities.ExecuteAgentAsync       │
              │  • restores StateBag from input                  │
              │  • emits agent.turn OTel span (token counts)     │
              │  • calls real AIAgent (ChatClientAgent)          │
              │  • serializes updated StateBag into result       │
              └──────────────────────────────────────────────────┘
```

---

## Quick Troubleshooting

| Issue | Solution |
|-------|----------|
| "Cannot find Temporalio package" | Use NuGet, not project refs; run `dotnet restore` |
| "Agent not registered" | Verify agent is added via `.AddTemporalAgents()` |
| "Router returned unrecognized name" | Check `AddAgentDescriptor` names match registered agents exactly |
| `Assert.Throws<ArgumentException>` fails | xUnit requires exact type — use `ArgumentNullException` for null, `ArgumentException` for empty |
| `GetTypeInfo metadata not provided` for `TemporalAgentSession` | Do not serialize `TemporalAgentSession` via `DefaultOptions`; use `StateBag.Serialize()` directly |
| "Activity timeout" | Increase `ActivityStartToCloseTimeout` — especially for HITL (needs human review time) |
| OTel spans missing | Ensure all 4 `ActivitySource` names are registered with the tracer provider |
| "Worker won't start" | Verify `temporal server start-dev` is running on `localhost:7233` |

---

## References

- **Temporal Documentation**: https://docs.temporal.io/
- **Temporal .NET SDK**: https://github.com/temporalio/sdk-dotnet
- **Microsoft Agent Framework**: https://github.com/microsoft/agents
### Temporalio.Extensions.Agents (MAF)

- **Usage Guide**: `docs/how-to/MAF/usage.md`
- **Routing Patterns**: `docs/how-to/MAF/routing.md`
- **Testing Agents**: `docs/how-to/MAF/testing-agents.md`
- **Observability**: `docs/how-to/MAF/observability.md`
- **Scheduling**: `docs/how-to/MAF/scheduling.md`
- **Structured Output**: `docs/how-to/MAF/structured-output.md`
- **Human-in-the-Loop**: `docs/how-to/MAF/hitl-patterns.md`
- **History & Token Optimization**: `docs/how-to/MAF/prompt-caching.md`
- **Do's and Don'ts**: `docs/how-to/MAF/dos-and-donts.md`
- **Durability Guarantees**: `docs/architecture/MAF/durability-and-determinism.md`
- **Sessions and Workflow Loop**: `docs/architecture/MAF/agent-sessions-and-workflow-loop.md`
- **Pub/Sub Equivalents**: `docs/architecture/MAF/pub-sub-and-event-driven.md`
- **StateBag and AIContextProvider**: `docs/architecture/MAF/session-statebag-and-context-providers.md`
- **Agent-to-Agent Communication**: `docs/architecture/MAF/agent-to-agent-communication.md`

### Temporalio.Extensions.AI (MEAI)

- **Usage Guide**: `docs/how-to/MEAI/usage.md`
- **Tool Functions**: `docs/how-to/MEAI/tool-functions.md` — Model 1 vs Model 2 explained
- **Testing**: `docs/how-to/MEAI/testing.md`
- **Observability**: `docs/how-to/MEAI/observability.md`
- **Human-in-the-Loop**: `docs/how-to/MEAI/hitl-patterns.md`
- **Durable Chat Pipeline**: `docs/architecture/MEAI/durable-chat-pipeline.md`

---

**Last Updated**: 2026-03-18
