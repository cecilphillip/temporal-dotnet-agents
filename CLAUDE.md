# TemporalAgents Project Guide

**Two Temporal .NET SDK integrations for durable AI applications:**
- `Temporalio.Extensions.Agents` — durable agent sessions built on Microsoft Agent Framework (`Microsoft.Agents.AI`)
- `Temporalio.Extensions.AI` — makes any plain `IChatClient` (MEAI) durable, no Agent Framework required

This document provides essential context for working with the TemporalAgents codebase. It covers project structure, architecture, key patterns, and important behavioral guarantees.

---

## Quick Facts

- **Language**: C# (.NET 10.0)
- **Solution File**: `TemporalAgents.slnx` (.slnx format, not .sln)
- **Status**: Complete — 415 unit tests + 66 integration tests (481 total, all pass)
  - Agents: 225 unit + 53 integration
  - AI: 190 unit + 13 integration
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
│   │   │   ├── agent-to-agent-communication.md
│   │   │   └── pub-sub-and-event-driven.md
│   │   └── MEAI/                           # Internal design docs (AI library)
│   │       ├── durable-chat-pipeline.md
│   │       └── cross-library-integration.md
│   └── how-to/
│       ├── MAF/                            # Practical guides (Agents library)
│       │   ├── usage.md
│       │   ├── routing.md
│       │   ├── testing-agents.md
│       │   ├── observability.md
│       │   ├── scheduling.md
│       │   ├── structured-output.md
│       │   ├── hitl-patterns.md
│       │   ├── prompt-caching.md
│       │   └── dos-and-donts.md
│       └── MEAI/                           # Practical guides (AI library)
│           ├── usage.md
│           ├── tool-functions.md
│           ├── embeddings.md
│           ├── testing.md
│           ├── observability.md
│           ├── hitl-patterns.md
│           └── custom-workflow-output.md
├── TemporalAgents.slnx                     # Solution file (use this, not .sln)
│
├── src/
│   ├── Temporalio.Extensions.Agents/       # Agent Framework integration library
│   │   ├── README.md                       # Library-specific docs
│   │   ├── ServiceCollectionExtensions.cs  # GetTemporalAgentProxy, AddTemporalAgentProxies
│   │   ├── TemporalWorkerBuilderExtensions.cs # .AddTemporalAgents() / AddWorkerPlugin(TemporalAgentsPlugin)
│   │   ├── TemporalAgentsOptions.cs        # Configuration (internal ctor); GetRegisteredAgentNames(), IsAgentRegistered()
│   │   ├── TemporalAgentsPlugin.cs         # ITemporalWorkerPlugin entry point [TA001]
│   │   ├── TemporalAgentsRegistrar.cs      # Internal: shared DI registration body for both entry points
│   │   ├── ITemporalAgentClient.cs         # Interface: RunAgentAsync, HITL
│   │   ├── TemporalAIAgent.cs              # For workflow orchestration (sub-agent)
│   │   ├── TemporalAIAgentProxy.cs         # For external callers (proxy)
│   │   ├── TemporalWorkflowExtensions.cs   # GetAgent(), ExecuteAgentsInParallelAsync()
│   │   ├── TemporalAgentDataConverter.cs   # Re-exposes AI library's DurableAIDataConverter for the agents library
│   │   ├── TemporalAgentDataConverterPlugin.cs # Plugin alias around DurableAIDataConverterPlugin
│   │   ├── TemporalAgentJsonUtilities.cs   # JSON options for agents-only types
│   │   ├── TemporalAgentTelemetry.cs       # ActivitySource + span/attribute constants
│   │   ├── AgentNotRegisteredException.cs  # Thrown when an unknown agent name is run
│   │   ├── AIAgentExtensions.cs            # AsAIAgent() and friends
│   │   ├── StructuredOutputExtensions.cs   # ChatResponseFormat helpers for typed output
│   │   ├── StructuredOutputOptions.cs      # Config for typed-output agents
│   │   ├── AgentWorkflowWrapper.cs         # Wraps agent with request context
│   │   ├── Session/                        # TemporalAgentContext, TemporalAgentSession, TemporalAgentSessionId
│   │   ├── State/                          # AgentSessionRequest/Response (extend DurableSession*), AgentDescriptor, source-gen ctx
│   │   └── Workflows/                      # AgentWorkflow (subclass of DurableChatWorkflowBase<AgentResponse>), AgentActivities, schedule infrastructure
│   │
│   └── Temporalio.Extensions.AI/           # MEAI IChatClient middleware library
│       ├── README.md                       # Library-specific docs
│       ├── DurableChatClient.cs            # DelegatingChatClient middleware
│       ├── DurableChatWorkflow.cs          # [Workflow] managing session history (subclass of DurableChatWorkflowBase<ChatResponse>)
│       ├── DurableChatWorkflowBase.cs      # Abstract base class providing the shared session loop + HITL
│       ├── DurableChatWorkflowInput.cs     # Workflow input record
│       ├── DurableChatActivities.cs        # [Activity] wrapping IChatClient.GetResponseAsync
│       ├── DurableChatInput.cs             # Activity input for chat dispatch
│       ├── DurableChatSessionClient.cs     # External entry point: ChatAsync, GetHistoryAsync, HITL — implements IDurableChatSessionClient
│       ├── IDurableChatSessionClient.cs    # Interface for session client (testable)
│       ├── DurableExecutionOptions.cs      # TaskQueue, ActivityTimeout, RetryPolicy, MaxEntryCount, HistoryReducer, EnableSearchAttributes, etc.
│       ├── DurableAIPayloadConverter.cs    # DurableAIDataConverter.Instance (AIJsonUtilities.DefaultOptions)
│       ├── DurableAIDataConverterPlugin.cs # ITemporalClientPlugin that installs the data converter
│       ├── DurableAIJsonContext.cs         # Source-gen JSON context for AI types
│       ├── DurableAIFunction.cs            # DelegatingAIFunction wrapping tool calls as activities
│       ├── DurableFunctionActivities.cs    # [Activity] resolving + invoking AIFunction from DI registry
│       ├── DurableFunctionInput.cs         # Activity input for tool dispatch
│       ├── DurableFunctionOutput.cs        # Activity output for tool dispatch
│       ├── DurableEmbeddingGenerator.cs    # DelegatingEmbeddingGenerator for IEmbeddingGenerator
│       ├── DurableEmbeddingActivities.cs   # [Activity] wrapping IEmbeddingGenerator.GenerateAsync
│       ├── DurableEmbeddingInput.cs        # Activity input for embedding dispatch
│       ├── DurableEmbeddingOutput.cs       # Activity output for embedding dispatch
│       ├── DurableApprovalRequest.cs       # HITL request type (RequestId, FunctionName, Description)
│       ├── DurableApprovalDecision.cs      # HITL decision type (RequestId, Approved, Reason)
│       ├── DurableApprovalMixin.cs         # Shared HITL handler logic used by DurableChatWorkflowBase
│       ├── DurableSessionAttributes.cs     # SearchAttributeKey<> definitions for TurnCount, SessionCreatedAt
│       ├── Session/                        # DurableSessionEntry / DurableSessionRequest / DurableSessionResponse
│       ├── DurableChatTelemetry.cs         # ActivitySource "Temporalio.Extensions.AI" + span constants
│       ├── ChatClientBuilderExtensions.cs  # UseDurableExecution()
│       ├── EmbeddingGeneratorBuilderExtensions.cs # UseDurableExecution() for embeddings
│       ├── DurableAIServiceCollectionExtensions.cs # AddDurableAI(), AddDurableTools()
│       ├── DurableAIPlugin.cs              # ITemporalWorkerPlugin entry point (parallel to AddDurableAI) [TAI001]
│       ├── DurableAIRegistrar.cs           # Internal: shared DI registration body for both entry points
│       ├── TemporalPluginBuilderExtensions.cs # AddWorkerPlugin() / AddClientPlugin() — incl. DurableAIPlugin overload
│       ├── AIFunctionExtensions.cs         # AsDurable() extension on AIFunction
│       └── TemporalChatOptionsExtensions.cs # WithActivityTimeout(), WithMaxRetryAttempts(), etc.
│
├── tests/
│   ├── Temporalio.Extensions.Agents.Tests/       # 225 unit tests
│   │   ├── TemporalWorkerBuilderExtensionsTests.cs
│   │   ├── HITLTypesTests.cs
│   │   ├── StateBagPersistenceTests.cs
│   │   ├── TemporalAgentTelemetryTests.cs
│   │   ├── TemporalWorkflowExtensionsTests.cs
│   │   ├── Helpers/
│   │   │   ├── StubAIAgent.cs              # Test double: implements CreateSessionCoreAsync
│   │   │   └── CapturingChatClient.cs      # Test double: records ChatOptions
│   │   └── ...
│   │
│   ├── Temporalio.Extensions.Agents.IntegrationTests/ # 53 integration tests (embedded Temporal server)
│   │
│   ├── Temporalio.Extensions.AI.Tests/           # 190 unit tests
│   │   ├── DurableExecutionOptionsTests.cs
│   │   ├── DurableChatClientTests.cs
│   │   ├── SerializationTests.cs
│   │   ├── DurableAIDataConverterTests.cs
│   │   ├── TemporalChatOptionsExtensionsTests.cs
│   │   ├── DurableEmbeddingGeneratorTests.cs
│   │   ├── DurableApprovalTests.cs
│   │   ├── DurableSessionEntryTests.cs
│   │   └── ...
│   │
│   └── Temporalio.Extensions.AI.IntegrationTests/ # 13 integration tests (embedded Temporal server)
│       └── Helpers/
│           ├── TestChatClient.cs           # IChatClient stub returning canned responses
│           └── IntegrationTestFixture.cs   # WorkflowEnvironment.StartLocalAsync() + hosted worker
│
└── samples/
    ├── MEAI/                               # Microsoft.Extensions.AI samples
    │   ├── DurableChat/                    # Extensions.AI: multi-turn chat
    │   ├── DurableTools/                   # Extensions.AI: per-tool activity dispatch via AsDurable()
    │   ├── OpenTelemetry/                  # Extensions.AI: OTel tracing configuration (project: DurableOpenTelemetry.csproj)
    │   ├── HumanInTheLoop/                 # Extensions.AI: HITL approval gates
    │   ├── DurableEmbeddings/              # Extensions.AI: IEmbeddingGenerator in workflow context
    │   └── CustomWorkflow/                 # Extensions.AI: subclass DurableChatWorkflowBase<TOutput> for typed update output
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

All middleware (`DurableChatClient`, `DurableAIFunction`, `DurableEmbeddingGenerator`) uses `Workflow.InWorkflow` as the dispatch guard. `false` = pass through to inner; `true` = dispatch as Temporal activity.

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

### Plugin Support

Plugin APIs are experimental and emit `TAI001`. Suppress with `#pragma warning disable TAI001`.

```csharp
// Worker plugin — composited with AddDurableAI on the hosted worker builder
builder.Services
    .AddHostedTemporalWorker("localhost:7233", "default", "my-queue")
    .AddDurableAI()
    .AddWorkerPlugin(new TracingPlugin());     // ITemporalWorkerPlugin

// Client plugin — only fires when the worker creates its own client (3-arg overload)
    .AddClientPlugin(new EncryptionPlugin()); // ITemporalClientPlugin

// Client plugin — standalone client via AddTemporalClient
builder.Services
    .AddTemporalClient("localhost:7233", "default")
    .AddClientPlugin(new EncryptionPlugin());
```

### Two equivalent registration entry points

Two parallel paths register the durable AI workflow, activities, function registry, session client, and `DurableAIDataConverter` auto-wiring. They produce identical DI state — pick whichever fits your composition style. `AddDurableAI()` is the lowest-friction option for hosted-worker users; `DurableAIPlugin` matches the canonical "AI Agent SDK" pattern from the Temporal AI Partner Ecosystem Guide ("Our standard partner integration mechanism is the Plugin").

```csharp
// Path A — DI extension (unchanged, primary example in user docs)
services.AddHostedTemporalWorker(addr, ns, "q")
    .AddDurableAI(o => o.ActivityTimeout = TimeSpan.FromMinutes(5));

// Path B — Worker plugin ([Experimental("TAI001")])
services.AddHostedTemporalWorker(addr, ns, "q")
    .AddWorkerPlugin(new DurableAIPlugin(o => o.ActivityTimeout = TimeSpan.FromMinutes(5)));
```

- `DurableAIPlugin : ITemporalWorkerPlugin`. Constructors: `()`, `(Action<DurableExecutionOptions>)`, `(DurableExecutionOptions)`. `Name` is `"Temporalio.Extensions.AI.DurableAIPlugin"` (matches Partner Guide convention). Gated by `[Experimental("TAI001")]`.
- The `AddWorkerPlugin(DurableAIPlugin)` overload is the only ergonomic path: it registers DI services AND queues the plugin in one call, because activities cannot be registered from `ConfigureWorker` (no `IServiceProvider` at that hook). Also `[Experimental("TAI001")]`.
- `DurableAIRegistrar` (internal) holds the shared DI registration body so both entry points converge on identical state.
- Calling both `AddDurableAI()` and `AddWorkerPlugin(new DurableAIPlugin())` on the same builder is idempotent — DI services are registered with `TryAdd*` semantics, and `DurableAIWorkerClientConfigurator.PostConfigure` dedupes `DurableAIDataConverterPlugin` by plugin `Name` before pushing it into `ClientOptions.Plugins`.

**`DurableAIDataConverter` auto-wiring**: both entry points register `IConfigureOptions<TemporalClientConnectOptions>` (`DurableAIClientOptionsConfigurator`) and `IPostConfigureOptions<TemporalWorkerServiceOptions>` (`DurableAIWorkerClientConfigurator`) that apply `DurableAIDataConverter.Instance` when the converter is still `DataConverter.Default`.

| Scenario | Auto-wired? |
|---|---|
| `AddTemporalClient(addr, ns)` + `AddDurableAI()` | ✅ Yes — via `IConfigureOptions<TemporalClientConnectOptions>` |
| `AddHostedTemporalWorker(addr, ns, queue)` + `AddDurableAI()` | ✅ Yes — via `IPostConfigureOptions<TemporalWorkerServiceOptions>` |
| `AddHostedTemporalWorker(addr, ns, queue)` + `AddWorkerPlugin(new DurableAIPlugin())` | ✅ Yes — same configurators registered by `DurableAIRegistrar` |
| Manual `TemporalClient.ConnectAsync` + `AddSingleton<ITemporalClient>` | ❌ No — set `DataConverter = DurableAIDataConverter.Instance` explicitly |

`TryAddEnumerable` is used so calling `AddDurableAI()` twice — or mixing `AddDurableAI()` and `AddWorkerPlugin(new DurableAIPlugin())` — does not register the configurators twice.

### Activity summaries (automatic)

Wave 1 added `ActivityOptions.Summary` at every activity dispatch site, populated automatically by the library. Users get the right summary in the Temporal Web UI with no public API knob to set.

| Activity | Summary value |
|---|---|
| Chat (`DurableChatActivities.GetResponseAsync`, streaming and non-streaming) | `chatOptions.ModelId` (e.g., `"gpt-4o-mini"`) |
| Tool (`DurableFunctionActivities.InvokeFunctionAsync` via `DurableAIFunction`) | function `Name` (e.g., `"GetWeather"`) |
| Embedding (`DurableEmbeddingActivities.GenerateAsync`) | `EmbeddingGenerationOptions.ModelId` |

Each middleware class has an `internal static BuildActivitySummary(...)` helper. Falls back to `null` when the underlying field is null/empty (no padding). HITL approval is implemented as a `[WorkflowUpdate]`, not an activity, so it has no `ActivityOptions.Summary` site. See the Temporal "enriching the UI" doc: https://docs.temporal.io/develop/dotnet/platform/enriching-ui#adding-summary-to-activities-and-timers.

### Important Notes

- `DurableChatActivities` is `internal` and registered as `AddSingletonActivities` — do not instantiate directly
- `DurableFunctionRegistry` is `internal Dictionary<string, AIFunction>` (case-insensitive) populated at startup
- Integration tests use `WorkflowEnvironment.StartLocalAsync()` (embedded Temporal CLI binary), not a separate server
- `IChatClient` must be registered in DI **before** `AddDurableAI` — the activities constructor-inject it
- Use `AddChatClient(innerClient).UseFunctionInvocation().Build()` (idiomatic MEAI DI pattern) instead of `AddSingleton<IChatClient>`; `UseDurableExecution()` chains onto the same builder
- `DurableChatActivities` resolves `IChatClient` per-invocation using a layered key model: (1) `ChatOptions.WithChatClientKey("key")` per-call override → (2) `DurableExecutionOptions.DefaultChatClientKey` worker-level default → (3) unkeyed `IChatClient` fallback. Set `opts.DefaultChatClientKey` in `AddDurableAI` when registering only keyed clients — no unkeyed alias is required.

---

## Temporalio.Extensions.Agents — Key Concepts

### 1. Registration API

Two equivalent paths register the agent workflow, activities, proxies, and `DurableAIDataConverter` auto-wiring. They produce identical DI state — pick whichever fits your composition style.

```csharp
// Path A — DI extension (primary, recommended)
services.AddHostedTemporalWorker("localhost:7233", "default", "agents")
    .AddTemporalAgents(opts =>
    {
        opts.AddAIAgent(agent);
        opts.EnableSearchAttributes = true;  // opt in to AgentName / SessionCreatedAt / TurnCount
    });

// Path B — Worker plugin ([Experimental("TA001")])
#pragma warning disable TA001
services.AddHostedTemporalWorker("localhost:7233", "default", "agents")
    .AddWorkerPlugin(new TemporalAgentsPlugin(opts =>
    {
        opts.AddAIAgent(agent);
        opts.EnableSearchAttributes = true;
    }));
#pragma warning restore TA001
```

`TemporalAgentsPlugin : ITemporalWorkerPlugin`. Constructors: `()`, `(Action<TemporalAgentsOptions>)`. Gated by `[Experimental("TA001")]`. Mixing `AddTemporalAgents()` and `AddWorkerPlugin(new TemporalAgentsPlugin())` on the same builder is idempotent.

Composes with other worker configuration (e.g., `.ConfigureOptions(opts => opts.MaxConcurrentActivities = 20)`).

#### New configuration knobs (since last release)

| Option | Type | Default | Description |
|--------|------|---------|-------------|
| `EnableSearchAttributes` | `bool` | `false` | Opt in to upsert `AgentName`, `SessionCreatedAt`, `TurnCount` on each run |
| `MaxEntryCount` | `int` | `1000` | Maximum number of `DurableSessionEntry` records the workflow holds before triggering continue-as-new. Renamed from `MaxHistorySize` in 0.2.0 |
| `HistoryReducer` | `Func<IList<DurableSessionEntry>, IList<DurableSessionEntry>>?` | `null` | Custom strategy for trimming history at continue-as-new boundaries. Operates on entries (preserving per-turn `Usage` and `CorrelationId`) since 0.3.0 |
| `RetryPolicy` | `RetryPolicy?` | `null` | Override the default Temporal retry policy for agent activities |

`EnableSearchAttributes = false` is the default and is a **breaking change** from the previous release — workflows that relied on search attributes being upserted unconditionally must now set this flag.

---

### 2. Two Agent Types

#### `TemporalAIAgent` (Workflow Context)
- **Use Case**: Inside a Temporal workflow calling a sub-agent
- **Access**: Via `TemporalWorkflowExtensions.GetAgent("AgentName")`

#### `TemporalAIAgentProxy` (External Context)
- **Use Case**: External caller (API server, CLI, console app)
- **Access**: Via `services.GetTemporalAgentProxy("AgentName")`

---

### 3. Workflow-Based Routing

Routing belongs inside a Temporal workflow, where every decision is durable, visible in history, and replayed from cache. Two patterns are supported:

**Static routing** — a classifier agent runs first; the result drives a switch to a hardcoded specialist name. Best for a fixed agent set.

```csharp
[Workflow("CustomerServiceWorkflow")]
public class CustomerServiceWorkflow
{
    [WorkflowRun]
    public async Task<string> RunAsync(string userQuestion)
    {
        var classifier = GetAgent("Classifier");
        var session = await classifier.CreateSessionAsync();
        var category = (await classifier.RunAsync(
            [new ChatMessage(ChatRole.User, userQuestion)], session))
            .Text?.Trim().ToUpperInvariant();

        var specialistName = category switch
        {
            "ORDERS"       => "OrdersAgent",
            "TECH_SUPPORT" => "TechSupportAgent",
            _              => "GeneralAgent",
        };

        var specialist = GetAgent(specialistName);
        var specialistSession = await specialist.CreateSessionAsync();
        return (await specialist.RunAsync(
            [new ChatMessage(ChatRole.User, userQuestion)], specialistSession))
            .Text ?? string.Empty;
    }
}
```

**Dynamic routing** — when the agent set changes across deployments, discover available agents via an activity that queries `TemporalAgentsOptions.GetRegisteredAgentNames()`. Activity results are cached in workflow history, keeping the workflow deterministic on replay. See `samples/MAF/WorkflowRouting/DynamicRoutingWorkflow.cs` for the full example.

- `TemporalAgentsOptions.GetRegisteredAgentNames()` returns all registered agent names; `IsAgentRegistered(name)` is a case-insensitive existence check.
- The `AgentDescriptor` record (`Name`, `Description`) is available in `Temporalio.Extensions.Agents.State` for routing activities to build their own description maps; the routing sample maintains its description metadata locally inside the activity rather than on the agent registry.
- Never call `GetRegisteredAgentNames()` or `IsAgentRegistered()` directly inside a `[Workflow]` — non-deterministic on replay; wrap in an activity instead.

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

As of Layer 3, `AgentWorkflow` inherits from `DurableChatWorkflowBase<AgentResponse>`. The shared session loop (history accumulation, mutex, `[WorkflowSignal("Shutdown")]`, `[WorkflowQuery("GetHistory")]`, HITL approval handlers, continue-as-new trigger) lives on the base; `AgentWorkflow` overrides the abstract hooks (`ExecuteTurnAsync`, `BuildResponseEntry`, `CreateContinueAsNewException`, `UpsertCustomSearchAttributes`) and adds the MAF-specific concerns (`StateBag` carry-forward, `AgentName` search attribute, fire-and-forget signal).

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
- `TryAddSingleton` for `ITemporalAgentClient` — allows custom implementations
- `ActivatorUtilities.CreateInstance<T>(provider, taskQueue)` — pattern for extra constructor args

### JSON Serialization
- `AgentSessionJsonContext` (Agents) and `DurableAIJsonContext` (AI) — source-generated contexts for conversation history types
- `TemporalAgentSession` is **NOT** in any source-gen context — do not try to serialize it via `DefaultOptions.GetTypeInfo(typeof(TemporalAgentSession))` directly
- `TemporalAgentSession.SerializeStateBag()` — delegates to `StateBag.Serialize()`, not session serialization
- Agents library reuses `DurableAIDataConverter` from the AI library (re-exposed via `TemporalAgentDataConverter`) so chat-content polymorphism works identically across both libraries

---

## Testing Patterns

### Unit Tests (415 total — 225 Agents + 190 AI)
- **Framework**: xunit with `[Fact]` attributes
- **Assertions**: `Assert.*` — `Assert.Throws<T>` requires **exact** type, not subtype (use `Assert.Throws<ArgumentNullException>` for null, not `ArgumentException`)
- **Mocking**: Hand-written fakes/stubs preferred over Moq
- `StubAIAgent` — implements `CreateSessionCoreAsync` returning `new TemporalAgentSession(TemporalAgentSessionId.WithRandomKey(Name ?? "stub"))`
- `TestChatClient` — `IChatClient` stub for AI tests returning `"Response: {lastMessage}"` with token counts

### Integration Tests (66 total — 53 Agents + 13 AI)
- Both test suites use `WorkflowEnvironment.StartLocalAsync()` (embedded server — no external process needed)
- Agents tests use `TestEnvironmentHelper.StartLocalAsync()`, a thin wrapper that passes `--search-attribute` CLI args to pre-register the three custom search attributes (`AgentName`, `SessionCreatedAt`, `TurnCount`). This pre-registration is only required when `EnableSearchAttributes = true` — if your test fixture leaves search attributes disabled (the default), bare `WorkflowEnvironment.StartLocalAsync()` works fine for Agents tests too.
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
- **Don't commit real API keys in `appsettings.json`** — use `dotnet user-secrets` or environment variables

---

## Common Patterns

### Pattern 1: External Agent Call
```csharp
var proxy = services.GetTemporalAgentProxy("MyAgent");
var session = await proxy.CreateSessionAsync();
var response = await proxy.RunAsync(userMessage, session);
```

### Pattern 2: Workflow-Based Routing (inside workflow)
```csharp
// Classify intent, then dispatch by name — routing decision is recorded in history
var classifier = GetAgent("Classifier");
var session = await classifier.CreateSessionAsync();
var category = (await classifier.RunAsync(messages, session)).Text?.Trim();
var agentName = category == "ORDERS" ? "OrdersAgent" : "GeneralAgent";
var specialist = GetAgent(agentName);
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
just test-unit          # Agents unit tests (225) — no server required
just test-unit-ai       # AI unit tests (190) — no server required
just test-unit-all      # All unit tests (415) — no server required
just test-integration   # Agents integration tests (53) — uses embedded server via TestEnvironmentHelper
just test-integration-ai # AI integration tests (13) — uses embedded server (no external process)
just test               # All suites

just test-coverage      # Unit tests with XPlat Code Coverage (output: artifacts/packages/coverage/)
just test-filter "FullyQualifiedName~Router"  # Run tests matching a filter expression
```

> Both integration test suites use the embedded Temporal server and require no external process.
> Agents tests use `TestEnvironmentHelper.StartLocalAsync()` to pre-register custom search attributes — only necessary when `EnableSearchAttributes = true`.

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
# All samples require: temporal server start-dev + OPENAI_API_KEY set via dotnet user-secrets

# Temporalio.Extensions.AI samples
dotnet run --project samples/MEAI/DurableChat/DurableChat.csproj
dotnet run --project samples/MEAI/DurableTools/DurableTools.csproj
dotnet run --project samples/MEAI/OpenTelemetry/DurableOpenTelemetry.csproj
dotnet run --project samples/MEAI/HumanInTheLoop/HumanInTheLoop.csproj
dotnet run --project samples/MEAI/DurableEmbeddings/DurableEmbeddings.csproj
dotnet run --project samples/MEAI/CustomWorkflow/CustomWorkflow.csproj

# Temporalio.Extensions.Agents samples
dotnet run --project samples/MAF/BasicAgent/BasicAgent.csproj
dotnet run --project samples/MAF/WorkflowOrchestration/WorkflowOrchestration.csproj
dotnet run --project samples/MAF/EvaluatorOptimizer/EvaluatorOptimizer.csproj
dotnet run --project samples/MAF/MultiAgentRouting/MultiAgentRouting.csproj
dotnet run --project samples/MAF/WorkflowRouting/WorkflowRouting.csproj
dotnet run --project samples/MAF/HumanInTheLoop/HumanInTheLoop.csproj
dotnet run --project samples/MAF/AmbientAgent/AmbientAgent.csproj

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
           │ Proxy(name)       │ Client.RunAsync     │ Client.SubmitApproval
           ▼                  ▼                    ▼
  ┌──────────────┐   ┌──────────────────┐   ┌──────────────────┐
  │ Temporal     │   │ DefaultTemporal  │   │ DefaultTemporal  │
  │ AIAgentProxy │   │ AgentClient      │   │ AgentClient      │
  └──────┬───────┘   └────────┬─────────┘   └────────┬─────────┘
         │                    │                      │
         └───────────────┬────┘                      │
                         │ ExecuteUpdateAsync         │
                         ▼                            ▼
              ┌──────────────────────────────────────────────────┐
              │  AgentWorkflow : DurableChatWorkflowBase<AgentResponse>
              │  • conversation history (List<DurableSessionEntry> on base)│
              │  • StateBag carry-forward (_currentStateBag)     │
              │  • HITL state (inherited DurableApprovalMixin)   │
              │  • RunAgentAsync [WorkflowUpdate("Run")]         │
              │  • RequestApprovalAsync / SubmitApprovalAsync    │
              │     (inherited [WorkflowUpdate])                 │
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
| `Assert.Throws<ArgumentException>` fails | xUnit requires exact type — use `ArgumentNullException` for null, `ArgumentException` for empty |
| `GetTypeInfo metadata not provided` for `TemporalAgentSession` | Do not serialize `TemporalAgentSession` via `DefaultOptions`; use `StateBag.Serialize()` directly |
| "Activity timeout" | Increase `ActivityStartToCloseTimeout` — especially for HITL (needs human review time) |
| OTel spans missing | Ensure all 4 `ActivitySource` names are registered with the tracer provider |
| "Worker won't start" | Verify `temporal server start-dev` is running on `localhost:7233` |
| Search attributes not appearing in UI | Set `opts.EnableSearchAttributes = true` — upsert is opt-in (default: `false`); also pre-register the attributes on production clusters |
| "Unexpected workflow task failure" in integration tests | Set `EnableSearchAttributes = true` in the fixture AND use `TestEnvironmentHelper.StartLocalAsync()` to pre-register the attributes; or leave search attributes disabled |

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
- **Embeddings**: `docs/how-to/MEAI/embeddings.md`
- **Testing**: `docs/how-to/MEAI/testing.md`
- **Observability**: `docs/how-to/MEAI/observability.md`
- **Human-in-the-Loop**: `docs/how-to/MEAI/hitl-patterns.md`
- **Custom Workflow Output**: `docs/how-to/MEAI/custom-workflow-output.md`
- **Durable Chat Pipeline**: `docs/architecture/MEAI/durable-chat-pipeline.md`
- **Cross-Library Integration**: `docs/architecture/MEAI/cross-library-integration.md`

---

**Last Updated**: 2026-04-30
