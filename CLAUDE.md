# TemporalAgents Project Guide

**Two Temporal .NET SDK integrations for durable AI applications:**
- `Temporalio.Extensions.Agents` — durable agent sessions built on Microsoft Agent Framework (`Microsoft.Agents.AI`)
- `Temporalio.Extensions.AI` — makes any plain `IChatClient` (MEAI) durable, no Agent Framework required

This document gives load-bearing project context: structure, gotchas, behavioral guarantees. For API how-tos, see `docs/how-to/`.

---

## Quick Facts

- **Language**: C# (.NET 10.0)
- **Solution File**: `TemporalAgents.slnx` (.slnx format, not .sln)

---

## Project Structure

```
TemporalAgents/
├── TemporalAgents.slnx        # Solution file (.slnx — use this, not .sln)
├── docs/
│   ├── architecture/          # Internal design docs (durability, sessions, statebag, a2a, pub/sub, etc.)
│   └── how-to/MAF + MEAI/     # Practical guides per library
├── src/
│   ├── Temporalio.Extensions.Agents/   # Agent Framework integration (depends on Extensions.AI)
│   └── Temporalio.Extensions.AI/       # MEAI IChatClient middleware (no Agent Framework)
├── tests/                     # Four projects: {Agents,AI} × {Tests, IntegrationTests}
└── samples/
    ├── MAF/                   # 8 samples: BasicAgent, SplitWorkerClient, WorkflowOrchestration,
    │                          # EvaluatorOptimizer, MultiAgentRouting, HumanInTheLoop,
    │                          # WorkflowRouting, AmbientAgent
    └── MEAI/                  # 6 samples: DurableChat, DurableTools, OpenTelemetry
                               # (DurableOpenTelemetry.csproj), HumanInTheLoop,
                               # DurableEmbeddings, CustomWorkflow
```

Use `Glob` / `ls` to discover specific files. Notable types and their locations are documented inline elsewhere in this guide (Key Type Locations, JSON Serialization, etc.).

---

## Temporalio.Extensions.AI — Key Concepts

**Entry points** (any of these is sufficient — they produce identical DI state):
- `services.AddHostedTemporalWorker(...).AddDurableAI(opts => ...)` — DI extension (primary)
- `services.AddHostedTemporalWorker(...).AddWorkerPlugin(new DurableAIPlugin(opts => ...))` — `[Experimental("TAI001")]`

**External usage**: `host.Services.GetRequiredService<DurableChatSessionClient>().ChatAsync(...)` returns `Task<DurableSessionResponse>` (post-Layer-2). `GetHistoryAsync` returns `Task<IReadOnlyList<DurableSessionEntry>>`.

**Required for MEAI types**: `DurableAIDataConverter.Instance` must be set on the Temporal client. Without it, `FunctionCallContent` / `FunctionResultContent` / other `AIContent` subtypes lose `$type` and deserialize as base `AIContent`. **Auto-wired** when using `AddTemporalClient(...)`, `AddHostedTemporalWorker(addr, ns, queue)`, or any of the plugin paths. **Manual `TemporalClient.ConnectAsync` callers** must set it explicitly.

**Per-request overrides** via `ChatOptions` extensions:
- `.WithActivityTimeout(TimeSpan)` / `.WithMaxRetryAttempts(int)` / `.WithHeartbeatTimeout(TimeSpan)` / `.WithChatClientKey(string)`
- Keys are `public const string` constants on `TemporalChatOptionsExtensions`.

**Durable tools**: `AddDurableTools(workerBuilder, params aiFunctions)` registers tools in `DurableFunctionRegistry` (resolved by name in `DurableFunctionActivities`). Or `aiFunction.AsDurable()` wraps as `DurableAIFunction` — passes through when `Workflow.InWorkflow == false`.

**Context detection**: All middleware (`DurableChatClient`, `DurableAIFunction`, `DurableEmbeddingGenerator`) uses `Workflow.InWorkflow` as the dispatch guard. `false` = pass through; `true` = dispatch as Temporal activity.

**HITL**: see `docs/how-to/MEAI/hitl-patterns.md`. Activity timeout on the underlying `[WorkflowUpdate]` must accommodate human review time.

**Activity summaries** (auto-populated for the Temporal Web UI):
- Chat: `chatOptions.ModelId`
- Tool: function `Name`
- Embedding: `EmbeddingGenerationOptions.ModelId`
- HITL approval is a `[WorkflowUpdate]`, not an activity — no summary site.

**Important notes**:
- `DurableChatActivities` is `internal`; registered as `AddSingletonActivities`. Don't instantiate directly.
- `DurableFunctionRegistry` is internal (`Dictionary<string, AIFunction>`, case-insensitive).
- `IChatClient` must be registered in DI **before** `AddDurableAI` (constructor-injected on activity).
- Use `AddChatClient(innerClient).UseFunctionInvocation().Build()` (idiomatic MEAI DI) over `AddSingleton<IChatClient>`. `UseDurableExecution()` chains onto the same builder.
- `IChatClient` resolution is layered: per-call `ChatOptions.WithChatClientKey("k")` → worker-level `DurableExecutionOptions.DefaultChatClientKey` → unkeyed fallback.

For full API surface, see `docs/how-to/MEAI/usage.md`.

---

## Temporalio.Extensions.Agents — Key Concepts

**Entry points**:
- `services.AddHostedTemporalWorker(...).AddTemporalAgents(opts => { opts.AddAIAgent(agent); opts.EnableSearchAttributes = true; })`
- `services.AddHostedTemporalWorker(...).AddWorkerPlugin(new TemporalAgentsPlugin(opts => ...))` — `[Experimental("TA001")]`. Idempotent if mixed with `AddTemporalAgents()`.

**Configuration**: see `docs/how-to/MAF/usage.md` for the full `TemporalAgentsOptions` reference (`EnableSearchAttributes`, `MaxEntryCount`, `HistoryReducer`, `RetryPolicy`, etc.). Names worth knowing for migration: `MaxHistorySize` → `MaxEntryCount` in 0.2.0; `HistoryReducer` is `Func<IList<DurableSessionEntry>, IList<DurableSessionEntry>>?` since 0.3.0 (operates on entries, not flat messages).

**Two agent types** (use the right one for context):
- `TemporalAIAgent` — workflow-context sub-agent. Access via `TemporalWorkflowExtensions.GetAgent("Name")`.
- `TemporalAIAgentProxy` — external-context proxy. Access via `services.GetTemporalAgentProxy("Name")`.

**Workflow-based routing**: routing belongs inside a `[Workflow]` (durable, replay-cached decisions). Two patterns:
- **Static**: classifier agent → `switch` → hardcoded specialist. Simple, fixed agent set.
- **Dynamic**: an activity calls `TemporalAgentsOptions.GetRegisteredAgentNames()` to discover agents at runtime; the activity's result is cached in workflow history (replay-deterministic). See `samples/MAF/WorkflowRouting/DynamicRoutingWorkflow.cs`.
- `AgentDescriptor` (`Name`, `Description`) lives in `Temporalio.Extensions.Agents.State` — routing activities can build their own description maps locally.
- **Never** call `GetRegisteredAgentNames()` / `IsAgentRegistered()` directly inside a `[Workflow]` — wrap in an activity.

**Parallel agent execution** (workflow-only, uses `Workflow.WhenAllAsync`):
```csharp
var results = await TemporalWorkflowExtensions.ExecuteAgentsInParallelAsync(new[]
{
    (researchAgent, messages, researchSession),
    (summaryAgent,  messages, summarySession),
});
```

**HITL**: see `docs/how-to/MAF/hitl-patterns.md`. Two flows — from inside a tool (activity context) via `TemporalAgentContext.Current.RequestApprovalAsync(...)`; from external systems via `client.GetPendingApprovalAsync` + `SubmitApprovalAsync`. Activity timeout must accommodate human review time.

**StateBag persistence** (`AgentSessionStateBag` for `AIContextProvider` like `Mem0Provider`):
- Serialized after each turn via `session.SerializeStateBag()`
- Stored in `_currentStateBag` on `AgentWorkflow`; passed forward in `ExecuteAgentInput`
- Restored at activity start via `TemporalAgentSession.FromStateBag`
- Empty bag (`StateBag.Count == 0`) returns `null` — no wasted serialization

**OpenTelemetry**: SDK's `TracingInterceptor` handles Temporal protocol spans; `TemporalAgentTelemetry` handles agent-semantic spans. Composed hierarchy:
```
agent.client.send                     ← TemporalAgentTelemetry
  UpdateWorkflow:RunAgent             ← TracingInterceptor
    RunActivity:ExecuteAgent          ← TracingInterceptor
      agent.turn                      ← TemporalAgentTelemetry (token counts, correlation ID)
```
Register all four sources with the tracer provider. **Never** call `ActivitySource.StartActivity()` inside `[Workflow]` — non-deterministic during replay; use `ActivitySourceExtensions.TrackWorkflowDiagnosticActivity` instead.

For full API surface, see `docs/how-to/MAF/usage.md`.

---

## Critical: Durability and Determinism

**MUST READ**: [`docs/architecture/MAF/durability-and-determinism.md`](./docs/architecture/MAF/durability-and-determinism.md)

When a worker crashes:
- ✅ Completed agent calls are **not re-executed** — results replay from history
- ✅ `_currentStateBag` carries forward through `AgentWorkflowInput.CarriedStateBag`
- ✅ Conversation history is serialized in workflow state across continue-as-new transitions

As of Layer 3, `AgentWorkflow : DurableChatWorkflowBase<AgentResponse>`. The shared session loop (history accumulation, mutex, `[WorkflowSignal("Shutdown")]`, `[WorkflowQuery("GetHistory")]`, HITL approval handlers, continue-as-new trigger) lives on the base. `AgentWorkflow` overrides the abstract hooks (`ExecuteTurnAsync`, `BuildResponseEntry`, `CreateContinueAsNewException`, `UpsertCustomSearchAttributes`) and adds MAF-specific concerns (StateBag carry-forward, `AgentName` search attribute, fire-and-forget signal).

---

## Important Dependencies and Notes

### Microsoft Agent Framework
- `Temporalio.Extensions.Agents` depends on `Temporalio.Extensions.AI` (which transitively brings in MEAI).
- HITL types are MEAI-side: `DurableApprovalRequest` / `DurableApprovalDecision` (from `Temporalio.Extensions.AI`).
- `AgentResponse`, `AIAgent`, `DelegatingAIAgent`, `AgentRunOptions` → `Microsoft.Agents.AI`.
- `ChatClientAgentRunOptions` → `Microsoft.Agents.AI` (not the Hosting package).
- `AgentSessionStateBag.Count` available; `AgentSessionStateBag.Serialize()` uses its own `AgentAbstractionsJsonUtilities.DefaultOptions`.

### Key Type Locations (gotchas)
- `RpcException` — `Temporalio.Exceptions` (NOT `Grpc.Core`)
- `Workflow.CreateContinueAsNewException` — takes `Expression<Func<TWorkflow, Task>>` (no collection expressions inside)
- `WorkflowIdConflictPolicy.UseExisting` — `Temporalio.Api.Enums.V1`

### DI Patterns
- `TemporalAgentsOptions` has an **internal constructor** — always access via the `AddTemporalAgents(opts => ...)` delegate.
- `TryAddSingleton` for `ITemporalAgentClient` — allows custom implementations.
- `ActivatorUtilities.CreateInstance<T>(provider, taskQueue)` — pattern for extra constructor args.

### JSON Serialization (gotchas)
- `AgentSessionJsonContext` (Agents) and `DurableAIJsonContext` (AI) — source-gen contexts for conversation history types.
- `TemporalAgentSession` is **NOT** in any source-gen context. Don't try `DefaultOptions.GetTypeInfo(typeof(TemporalAgentSession))`.
- `TemporalAgentSession.SerializeStateBag()` delegates to `StateBag.Serialize()`, not session serialization.
- Agents library reuses `DurableAIDataConverter` from the AI library (re-exposed via `TemporalAgentDataConverter`) for chat-content polymorphism.

---

## Testing Gotchas

For full testing patterns, see `docs/how-to/MAF/testing-agents.md` and `docs/how-to/MEAI/testing.md`. Cross-cutting gotchas worth knowing here:

- **`Assert.Throws<T>` requires exact type, not subtype.** Use `Assert.Throws<ArgumentNullException>` for null, not `ArgumentException`. xUnit will fail the test if the thrown exception is a subtype of the expected.
- **Hand-written stubs preferred** over FakeItEasy/Moq in this project. See `StubAIAgent` and `TestChatClient` in the test helpers.
- **Search-attribute pre-registration is conditional.** Agents integration tests only need `TestEnvironmentHelper.StartLocalAsync()` (which pre-registers `AgentName`/`SessionCreatedAt`/`TurnCount`) when `EnableSearchAttributes = true`. Bare `WorkflowEnvironment.StartLocalAsync()` works otherwise. AI integration tests never need pre-registration.
- **Both suites use embedded server** — `WorkflowEnvironment.StartLocalAsync()`. No external `temporal server start-dev` process.

---

## Workflow Best Practices

### ✅ DO
- Use the fluent `.AddTemporalAgents()` builder
- Use `GetAgent()` inside workflows for sub-agent orchestration
- Use `Workflow.UtcNow` and `Workflow.NewGuid()` (not `DateTime.UtcNow` / `Guid.NewGuid()`)
- Set appropriate per-agent TTLs (default: 14 days)
- Validate config eagerly — `string.IsNullOrEmpty` + `InvalidOperationException` for missing config (not `is null` + `ArgumentNullException`)
- Keep OTel spans out of workflows — `agent.turn` lives in `AgentActivities`; `agent.client.send` in `DefaultTemporalAgentClient`

### ❌ DON'T
- **Never** call `ActivitySource.StartActivity()` inside `[Workflow]` — non-deterministic on replay
- Don't use wall-clock time in workflows (`DateTime.UtcNow`, `DateTimeOffset.Now`)
- Don't use `Random` or `Guid.NewGuid()` in workflows
- Don't call `builder.Build()` twice — assign `var host = builder.Build()` once
- Don't commit real API keys to `appsettings.json` — use `dotnet user-secrets` or environment variables

---

## Build Automation

Build automation uses [`just`](https://just.systems). All recipes in `justfile`. .NET SDK pinned via `global.json` (10.0.x). Versioning via `minver-cli` (local `dotnet tool restore`).

```bash
just --list             # All recipes
just build              # Restore + Release build (default)
just test-unit-all      # All unit tests (415) — no server required
just test-integration   # Agents integration (53) — embedded server
just test-integration-ai # AI integration (13) — embedded server
just pack               # clean → build → pack → artifacts/packages/*.nupkg
```

**Versions** auto-derive from git tags via MinVer: exactly on `vX.Y.Z` tag → `X.Y.Z`; N commits after → `X.Y.(Z+1)-preview.N`. Cut a release with `git tag -a vX.Y.Z -m "..."` then `just pack`.

**Publish**: `just publish-nuget` (needs `NUGET_API_KEY`) or `just publish-github` (needs `NUGET_GITHUB_TOKEN`).

---

## CI/CD — GitHub Actions

`.github/workflows/build.yml`. Three jobs: `build` (ubuntu+macOS matrix on push to `main`, runs `just build` + `just test-unit`), `package` (after `build`, `just pack`, uploads artifact), `publish` (`workflow_dispatch` only — pushes pre-built artifact to GitHub or NuGet). Integration tests are excluded from CI.

**Required secrets**: `NUGET_PAT` (GitHub Packages), `NUGET_API_KEY` (NuGet.org).

---

## Run Samples

Prerequisites: `temporal server start-dev` running + `OPENAI_API_KEY` (and optionally `OPENAI_API_BASE_URL`) configured via `dotnet user-secrets` or environment variables.

```bash
# MEAI samples
dotnet run --project samples/MEAI/{DurableChat,DurableTools,HumanInTheLoop,DurableEmbeddings,CustomWorkflow}/...csproj
dotnet run --project samples/MEAI/OpenTelemetry/DurableOpenTelemetry.csproj

# MAF samples
dotnet run --project samples/MAF/{BasicAgent,WorkflowOrchestration,EvaluatorOptimizer,MultiAgentRouting,WorkflowRouting,HumanInTheLoop,AmbientAgent}/...csproj

# SplitWorkerClient — Worker first, then Client in a separate terminal
dotnet run --project samples/MAF/SplitWorkerClient/Worker/Worker.csproj
dotnet run --project samples/MAF/SplitWorkerClient/Client/Client.csproj
```

---

## Quick Troubleshooting

| Issue | Solution |
|---|---|
| "Cannot find Temporalio package" | Use NuGet, not project refs; `dotnet restore` |
| "Agent not registered" | Verify `.AddTemporalAgents()` includes the agent |
| `Assert.Throws<ArgumentException>` fails | xUnit requires exact type — use `ArgumentNullException` for null, `ArgumentException` for empty |
| `GetTypeInfo metadata not provided` for `TemporalAgentSession` | Don't serialize via `DefaultOptions`; use `StateBag.Serialize()` |
| Activity timeout (HITL) | Increase `ActivityStartToCloseTimeout` to accommodate human review time |
| OTel spans missing | Register all 4 `ActivitySource` names with the tracer provider |
| Worker won't start | `temporal server start-dev` running on `localhost:7233`? |
| Search attributes missing in UI | `opts.EnableSearchAttributes = true` (opt-in, default `false`); pre-register on production clusters |
| Integration test "Unexpected workflow task failure" | Either set `EnableSearchAttributes = true` AND use `TestEnvironmentHelper.StartLocalAsync()`, or leave search attributes disabled |

---

## References

- **Temporal Documentation**: https://docs.temporal.io/
- **Temporal .NET SDK**: https://github.com/temporalio/sdk-dotnet
- **Microsoft Agent Framework**: https://github.com/microsoft/agent-framework

### Temporalio.Extensions.Agents (MAF)

- **Usage Guide**: `docs/how-to/MAF/usage.md`
- **Routing Patterns**: `docs/how-to/MAF/routing.md`
- **Testing Agents**: `docs/how-to/MAF/testing-agents.md`
- **Observability**: `docs/how-to/MAF/observability.md`
- **Scheduling**: `docs/how-to/MAF/scheduling.md`
- **Structured Output**: `docs/how-to/MAF/structured-output.md`
- **HITL Patterns**: `docs/how-to/MAF/hitl-patterns.md`
- **History & Token Optimization**: `docs/how-to/MAF/prompt-caching.md`
- **Do's and Don'ts**: `docs/how-to/MAF/dos-and-donts.md`
- **Durability Guarantees**: `docs/architecture/MAF/durability-and-determinism.md`
- **Sessions and Workflow Loop**: `docs/architecture/MAF/agent-sessions-and-workflow-loop.md`
- **Pub/Sub Equivalents**: `docs/architecture/MAF/pub-sub-and-event-driven.md`
- **StateBag and AIContextProvider**: `docs/architecture/MAF/session-statebag-and-context-providers.md`
- **Agent-to-Agent Communication**: `docs/architecture/MAF/agent-to-agent-communication.md`

### Temporalio.Extensions.AI (MEAI)

- **Usage Guide**: `docs/how-to/MEAI/usage.md`
- **Tool Functions**: `docs/how-to/MEAI/tool-functions.md` (Model 1 vs Model 2)
- **Embeddings**: `docs/how-to/MEAI/embeddings.md`
- **Testing**: `docs/how-to/MEAI/testing.md`
- **Observability**: `docs/how-to/MEAI/observability.md`
- **HITL Patterns**: `docs/how-to/MEAI/hitl-patterns.md`
- **Custom Workflow Output**: `docs/how-to/MEAI/custom-workflow-output.md`
- **Durable Chat Pipeline**: `docs/architecture/MEAI/durable-chat-pipeline.md`
- **Cross-Library Integration**: `docs/architecture/MEAI/cross-library-integration.md`

---

**Last Updated**: 2026-04-30
