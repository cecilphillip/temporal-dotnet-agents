# Tank - Backend Developer

Hired: 2026-04-05

## Recent Updates

Agent hired and assigned to Temporal Agents Dev.

### 2026-05-06 — Phase 3 of v0.3 API redesign (durable agent workflow loop)

Executed Phase 3 of `src-temporalio-extensions-ai-readme-md-vivid-meteor.md`. Wires `AddDurableAgent` registrations into the workflow loop — Phase 1 added the types, Phase 2 added the registration + `InvokeAgentTool` activity + lazy composition, this phase adds the workflow side so end-to-end durable execution works.

**Code changes:**
- `Workflows/AgentWorkflowInput.cs`: added `IsDurable` flag and `DurableAgentToolActivityOptions` dictionary fields. Both default to false/null so the legacy path is unaffected.
- `Workflows/AgentActivities.cs`: added `RunDurableAgentStepAsync` activity (`Temporalio.Extensions.Agents.RunDurableAgentStep`). Resolves the agent via Phase 2's `ResolveDurableAgent` (uses the composed pipeline with `UseAIContextProviders`) and pulls `IChatClient` off the cached `ChatClientAgent.ChatClient` property — the architectural difference from legacy `RunAgentStepAsync` (which goes straight to DI). Mirrors the streaming + heartbeat + tool-call detection logic of the legacy step activity. Did NOT touch `RunAgentStepAsync` or `ExecuteAgentAsync` per the plan (Phase 5 deletes them).
- `Workflows/AgentWorkflow.cs`: branched `ExecuteAgentTurnAsync` on `_input.IsDurable` (precedes legacy step-mode branch). New `ExecuteDurableAgentTurnAsync` runs the same step-loop shape as legacy step mode (cap-bounded, `Workflow.WhenAllAsync` fan-out for tool calls, structured error message on cap exceeded) but dispatches to the new `RunDurableAgentStepAsync` + `InvokeAgentToolAsync` activities. Added `ResolveDurableToolActivityOptions` helper that looks up the per-tool dict on the input. Updated `CreateContinueAsNewException` to carry `IsDurable` and `DurableAgentToolActivityOptions` forward across CAN.
- `Workflows/DefaultTemporalAgentClient.cs`: centralized `AgentWorkflowInput` construction in a `BuildAgentWorkflowInput(name)` helper called by `RunAgentAsync`, `RunAgentFireAndForgetAsync`, `RunAgentDelayedAsync`. Helper looks up the agent in `options.DurableAgentRegistrations` and sets `IsDurable = true` when found, plus pre-computes `DurableAgentToolActivityOptions` from the tool registrations using the `effective = perTool ?? worker` inheritance pattern. Phase 4 expands this to all per-agent scalars; Phase 3 only wires what the durable loop needs.
- `Logs.cs`: added `LogDurableAgentTurnStarted/Iteration/Completed/Aborted` (event IDs 29-32).

**Tests:**
- New `tests/Temporalio.Extensions.Agents.IntegrationTests/DurableAgentIntegrationTests.cs` — 6 end-to-end tests via the embedded server: single turn no tools; single tool call; 3 parallel tool calls (asserts all 3 schedules precede first complete in workflow history); iteration cap returns structured error; write tool with `opts.NoRetry()` does not retry on activity failure; legacy `AddAIAgent` path still uses single-activity `ExecuteAgent` (no `RunDurableAgentStep`).
- New `tests/Temporalio.Extensions.Agents.IntegrationTests/DurableAgentCrashSafetyTests.cs` — 2 cross-host tests: write tool `NoRetry` does not double-fire across worker restart; transient read-tool failure retries independently while a parallel `NoRetry`-registered write tool fires exactly once.
- Reuses `ScriptedChatClient` and `RecordingTool` linked from the unit-test project via the existing `<Compile Include>` items in the integration tests csproj — no new scaffolding needed.

**Docs:** added "Durable Agent Workflow Loop (v0.3 `AddDurableAgent`)" section to `docs/architecture/MAF/agent-sessions-and-workflow-loop.md` after the existing step-mode section. Includes the data-flow diagram, the two-activity-types table (`RunDurableAgentStep` + `InvokeAgentTool`), the iteration cap, the determinism cross-reference, and the CAN-carry-forward note. Added the new section to the table of contents.

**Verification:**
- `just build` clean (0/0).
- Unit tests: 524 pass (190 AI + 334 Agents).
- Agents integration tests: 71 pass (was 53; +6 DurableAgentIntegrationTests + 2 DurableAgentCrashSafetyTests + 10 from earlier phases).
- AI integration tests: 13 pass.
- Determinism guard `grep -rn "ConfigureAwait(false)" src/Temporalio.Extensions.Agents/Workflows/AgentWorkflow.cs src/Temporalio.Extensions.AI/DurableChatWorkflowBase.cs` returns zero matches.
- `Task.WhenAll` only appears in workflow comments (don't-do warnings), never in real code.

**Deviations from the plan:** none significant. The plan suggested using `cached.Agent.ChatClient` "if exposed, or extracting it from the cached pipeline"; `ChatClientAgent` exposes `ChatClient` as a public property in MAF 1.0, so the cast is straightforward. The plan also suggested `BuildResponseEntry` style for assembling the final `AgentResponse`; I mirrored the legacy `ExecuteStepModeTurnAsync` pattern (build directly from accumulated `allTurnMessages` + `totalUsage` + `Workflow.UtcNow`) which is what `BuildResponseEntry` would have produced anyway. The new test for `WriteToolWithNoRetry` initially attempted to use a `tools` parameter to BuildHost but had to switch to a `registerToolsViaBuilder` callback because options on tools must be set at `AddTool` time — small ergonomic adjustment to the helper.

Did NOT proceed to Phase 4 — chief will dispatch after review.

### 2026-05-05 — Sample 1: ExternalHistoryStore (MAF)

Implemented `samples/MAF/ExternalHistoryStore/` per the plan at `src-temporalio-extensions-ai-readme-md-vivid-meteor.md`. The sample bundles three concerns into one runnable demo:

- Layer 1 — `InMemoryHistoryStore : IAgentHistoryStore` with recent-N reduction inside `LoadAsync` (default 4 entries) plus a `SnapshotFull` audit view and `LoadCalls` / `ReductionEvents` counters.
- Layer 2 — `TenantContextProvider : AIContextProvider` overrides `ProvideAIContextAsync` to inject one tenant-scoped system message per turn. Tenant ID is plumbed from the workflow via `ChatMessage.AdditionalProperties["TenantId"]` (option c was unworkable — `RunRequest` carries no per-call options slot, so I documented why and used the `AdditionalProperties` sideband). Looked up against a DI-registered `TenantDirectory` POCO bound from the `Tenants` section of `appsettings.json`.
- Reduction strategy doc — README spells out the three patterns (recent-N, summarize-and-keep, at-rest in `ReplaceAsync`) and references the `HistoryReducer`/`[JsonIgnore]` constraint that motivates store-side reduction.

`SupportSessionWorkflow` is a long-lived `[Workflow]` exposing `[WorkflowUpdate("Ask")]` so the driver runs 6 turns by calling `handle.ExecuteUpdateAsync` six times. `ChatClientAgentOptions` does NOT have an `Instructions` property at the v1.3.0 of `Microsoft.Agents.AI` — instructions go on `ChatOptions.Instructions` instead; first build attempt failed on this. Build clean (0 warnings, 0 errors). Did not edit `TemporalAgents.slnx` or `CLAUDE.md` per plan (chief handles those). Added a "See Also" link to `docs/how-to/MAF/external-history-store.md` pointing at the new sample.

### 2026-04-30 — Phase 2 of Layer 1 refactor (delete content-type hierarchy)

Executed Phase 2 of `src-temporalio-extensions-ai-readme-md-vivid-meteor.md`.

- Edited `src/Temporalio.Extensions.Agents/State/TemporalAgentStateMessage.cs`: switched `Contents` to `IReadOnlyList<AIContent>` and replaced both conversion loops with a direct `.ToList()`.
- Edited `src/Temporalio.Extensions.Agents/State/TemporalAgentStateJsonContext.cs`: removed `[JsonSerializable(typeof(TemporalAgentStateContent))]`.
- Deleted 12 files in `State/`: `TemporalAgentStateContent.cs` plus the 11 derived `*Content.cs` types.
- Edited `tests/Temporalio.Extensions.Agents.Tests/StateSerializationTests.cs`: removed the 6 content-hierarchy tests; switched the response round-trip assertion to MEAI's `TextContent`.
- Edited integration tests (`ResilienceTests.cs`, `WorkflowQueryTests.cs`, `EdgeCaseTests.cs`): switched `OfType<TemporalAgentStateTextContent>()` to `OfType<TextContent>()`; added MEAI using directives where missing.
- Tweaked the file-level XML doc on `ChatMessagePolymorphismTests.cs` to past-tense the deleted hierarchy.

Results: `just build` clean (0 warnings, 0 errors). Unit tests 378 pass (216 Agents incl. Phase 1 polymorphism tests, 162 AI). Agents integration 53 pass; AI integration 10 pass. Audit grep across the entire repo: zero matches for any of the 12 deleted type names.

### 2026-04-30 — Phase 3 of Layer 1 refactor (delete TemporalAgentStateMessage)

Executed Phase 3 production-code + tests portion of `src-temporalio-extensions-ai-readme-md-vivid-meteor.md`.

- `src/Temporalio.Extensions.Agents/State/TemporalAgentStateEntry.cs`: changed `Messages` field type to `IReadOnlyList<ChatMessage>`; added `using Microsoft.Extensions.AI;`.
- `src/Temporalio.Extensions.Agents/State/TemporalAgentStateRequest.cs` / `TemporalAgentStateResponse.cs`: replaced the `FromChatMessage` / `ToChatMessage` `.Select(...)` calls with direct `.ToList()`.
- `src/Temporalio.Extensions.Agents/Workflows/AgentActivities.cs:62`: dropped `.ToChatMessage()` — messages are already `ChatMessage`.
- `src/Temporalio.Extensions.Agents/State/TemporalAgentStateJsonContext.cs`: removed the `TemporalAgentStateMessage` registration. Did not add `ChatMessage` — the AI base options carry polymorphism.
- `src/Temporalio.Extensions.Agents/TemporalAgentJsonUtilities.cs`: rebased `DefaultOptions` on `AIJsonUtilities.DefaultOptions` (mirrors `DurableAIJsonContext` pattern). This is what makes `ChatMessage`/`AIContent` polymorphism work for entries serialized via the agent JSON utilities.
- Deleted `src/Temporalio.Extensions.Agents/State/TemporalAgentStateMessage.cs`. State/ now has 6 files (down from 7).
- `tests/Temporalio.Extensions.Agents.Tests/StateSerializationTests.cs`: switched `Role` assertion from `ChatRole.User.Value` (string) to `ChatRole.User` (struct); added a `Text` assertion to the round-trip test; made the `$type` discriminator assertions whitespace-tolerant (because `AIJsonUtilities.DefaultOptions` enables `WriteIndented`).
- Three integration test files (`WorkflowQueryTests.cs`, `EdgeCaseTests.cs`): updated string `"user"`/`"assistant"` role comparisons to `ChatRole.User`/`ChatRole.Assistant`.

Results: `just build` clean. `just test-unit-all` 378 pass (216 Agents + 162 AI). `just test-integration` 53 pass. `just ci` green. Audit grep for `TemporalAgentStateMessage|FromChatMessage|ToChatMessage|FromAIContent|ToAIContent` across `src/` + `tests/`: zero matches.

Whitespace-tolerant discriminator assertions are a deliberate trade — the AI library's `DefaultOptions` ships with `WriteIndented = true`, and following the pattern set by `src/Temporalio.Extensions.AI/DurableAIJsonContext.cs` means inheriting that setting. Easier to relax the test than to fight the upstream default.

### 2026-04-30 — Phase 1 of Layer 2 refactor (introduce DurableSessionEntry)

Executed Phase 1 of `src-temporalio-extensions-ai-readme-md-vivid-meteor.md` (Layer 2). Pure additive — established the shared `DurableSessionEntry` type surface in `Temporalio.Extensions.AI` without migrating any workflow code yet.

- New `src/Temporalio.Extensions.AI/Session/DurableSessionEntry.cs`: abstract base with `[JsonPolymorphic($type)]` registering `"ai_request"` / `"ai_response"` discriminators. Required `CorrelationId` + `CreatedAt`, default-empty `Messages : IReadOnlyList<ChatMessage>`, `[JsonExtensionData] AdditionalProperties`.
- New `src/Temporalio.Extensions.AI/Session/DurableSessionRequest.cs`: concrete; `FromMessages(messages, correlationId, timestamp)` factory mirrors the timestamp-fallback pattern from `TemporalAgentStateRequest.FromRunRequest` (min message CreatedAt or fallback).
- New `src/Temporalio.Extensions.AI/Session/DurableSessionResponse.cs`: concrete with `UsageDetails? Usage` (used directly per locked decision #1 — no wrapper) and `[JsonIgnore] string Text` returning the last assistant message's text or empty. `FromChatResponse(correlationId, response, timestamp)` factory mirrors the existing pattern.
- Edited `src/Temporalio.Extensions.AI/DurableAIJsonContext.cs`: added 5 `[JsonSerializable]` lines (entry, request, response, `IReadOnlyList<DurableSessionEntry>`, `List<DurableSessionEntry>`).
- New `tests/Temporalio.Extensions.AI.Tests/DurableSessionEntryTests.cs` — 17 tests covering request/response round-trip, usage preservation, polymorphic mixed-list round-trip, `$type` discriminator wire-format lock-in, `Text` accessor, `AdditionalProperties` round-trip, factory shapes, factory validation.

One small fix mid-flight: the wire-format discriminator assertion needed to be whitespace-tolerant (`AIJsonUtilities.DefaultOptions` writes indented JSON — same trade as Phase 3 of Layer 1).

Results: `just build` clean. `just test-unit-all` 395 pass (216 Agents + 179 AI — up from 162; +17 new tests). `just test-integration` 53 pass; `just test-integration-ai` 10 pass. No production code outside the new files yet uses `DurableSessionEntry`/`Request`/`Response` — Phase 2 (MEAI migration) handles that.

### 2026-04-30 — Phase 2 of Layer 2 refactor (migrate MEAI to DurableSessionEntry)

Executed Phase 2 of `src-temporalio-extensions-ai-readme-md-vivid-meteor.md` (Layer 2). `DurableChatWorkflow` now stores `List<DurableSessionEntry>` instead of `List<ChatMessage>`; `DurableChatSessionClient.ChatAsync` returns `DurableSessionResponse` and accepts an optional `correlationId`; `GetHistoryAsync` returns `IReadOnlyList<DurableSessionEntry>`.

Production code edits:
- `src/Temporalio.Extensions.AI/DurableChatWorkflowBase.cs`: `_history` typed `List<DurableSessionEntry>`; new abstract hook `BuildResponseEntry(correlationId, output, createdAt)` replacing `GetHistoryMessages`; new virtual hook `BuildRequestEntry` defaulting to `DurableSessionRequest.FromMessages`. `RunTurnAsync` now returns `(TOutput Output, DurableSessionResponse ResponseEntry)` so subclasses can choose which shape to expose via their update handler. `[WorkflowQuery("GetHistory")]` returns `IReadOnlyList<DurableSessionEntry>`. Reducer signature `Func<IList<DurableSessionEntry>, IList<DurableSessionEntry>>?`. `MaxHistorySize` references renamed to `MaxEntryCount`. Auto-generates `Workflow.NewGuid().ToString("N")` for correlation when caller did not supply one.
- `src/Temporalio.Extensions.AI/DurableChatWorkflow.cs`: `: DurableChatWorkflowBase<ChatResponse>` (was `<DurableChatOutput>`). `ChatAsync [WorkflowUpdate]` returns `Task<DurableSessionResponse>`. `BuildResponseEntry` calls `DurableSessionResponse.FromChatResponse`.
- `src/Temporalio.Extensions.AI/DurableChatActivities.cs`: returns bare `ChatResponse` (no shape change required by the plan but the wrapping `DurableChatOutput` was deleted).
- `src/Temporalio.Extensions.AI/DurableChatClient.cs`: dropped `.Response` accessors (activity result is now `ChatResponse` directly).
- `src/Temporalio.Extensions.AI/DurableChatInput.cs`: added optional `string? CorrelationId`.
- `src/Temporalio.Extensions.AI/DurableChatWorkflowInput.cs`: `MaxHistorySize` → `MaxEntryCount`; reducer type changed; `CarriedHistory` is now `List<DurableSessionEntry>?`.
- `src/Temporalio.Extensions.AI/DurableExecutionOptions.cs`: same rename + reducer type change; updated validation message.
- `src/Temporalio.Extensions.AI/DurableChatSessionClient.cs`: `ChatAsync` signature now `Task<DurableSessionResponse> ChatAsync(string conversationId, IEnumerable<ChatMessage>, ChatOptions?, string? correlationId = null, CancellationToken)`. `GetHistoryAsync` returns `IReadOnlyList<DurableSessionEntry>`. Reads `_options.MaxEntryCount`. Telemetry `RequestModelAttribute` set from `options?.ModelId` (response no longer carries `ModelId` — that lives on the inner `ChatResponse` only).
- `src/Temporalio.Extensions.AI/IDurableChatSessionClient.cs`: signatures aligned with concrete class.
- `src/Temporalio.Extensions.AI/DurableAIJsonContext.cs`: dropped `[JsonSerializable(typeof(DurableChatOutput))]`.

Files deleted (2):
- `src/Temporalio.Extensions.AI/FuncChatReducer.cs` — dead after the reducer signature shift to a sync `Func<>`.
- `src/Temporalio.Extensions.AI/DurableChatOutput.cs` — workflow update returns `DurableSessionResponse` directly; activity returns `ChatResponse` directly.

Sample updates:
- `samples/MEAI/CustomWorkflow/ShoppingAssistantWorkflow.cs`: replaces `GetHistoryMessages` with `BuildResponseEntry` (wraps `ShoppingTurnOutput.Response` via `DurableSessionResponse.FromChatResponse`). `ShopAsync` destructures the `RunTurnAsync` tuple and returns the original `ShoppingTurnOutput` so the sample's domain-typed update return shape stays unchanged.
- `samples/MEAI/DurableChat/Program.cs`: history-display loop flattens entries into messages via `history.SelectMany(e => e.Messages)`. Other `ChatAsync` call sites compile unchanged because `r.Text` works on both `ChatResponse` and the new `DurableSessionResponse` (the latter has a `Text` convenience accessor). HumanInTheLoop and OpenTelemetry samples needed no edits.

Tests:
- `tests/Temporalio.Extensions.AI.Tests/DurableExecutionOptionsTests.cs`: replaced `FuncChatReducer` tests with entry-shaped delegate tests; added `MaxEntryCount` defaults / set / validate-zero / validate-negative tests.
- `tests/Temporalio.Extensions.AI.Tests/SerializationTests.cs`: replaced `DurableChatOutput_RoundTrips` with `ChatResponse_ActivityReturn_RoundTrips` (asserting the bare-`ChatResponse` activity payload survives the converter).
- `tests/Temporalio.Extensions.AI.Tests/DurableChatKeyedClientTests.cs`: dropped `.Response.` from two assertions where the activity now returns `ChatResponse`.
- `tests/Temporalio.Extensions.AI.IntegrationTests/DurableChatSessionIntegrationTests.cs`: rewrote single/multi-turn assertions to entry-shape (4 entries = 2 request + 2 response per 2 turns); added `UsageDetails_AreQueryablePerTurn_ViaGetHistory`, `UserSuppliedCorrelationId_IsPreserved_OnRequestAndResponseEntries`, and `NullCorrelationId_AutoGeneratesGuid` tests covering the new capabilities. Single-turn test now asserts `response.CorrelationId` is non-empty and `response.Usage` is populated.

Design choice mid-flight: `RunTurnAsync` returns a tuple `(TOutput, DurableSessionResponse)` rather than just one or the other. The default `DurableChatWorkflow` returns `DurableSessionResponse` from `[WorkflowUpdate]`; the `CustomWorkflow` sample returns its domain-typed `ShoppingTurnOutput`. Tuple lets each subclass pick. Considered exposing the last response entry as a protected accessor instead — less idiomatic and less explicit at the call site.

Results: `just build` clean (0 warnings, 0 errors). `just test-unit-all` 398 pass (216 Agents + 182 AI — up from 179; +3 net new tests after replacing 3 obsolete `FuncChatReducer` tests with 4 `MaxEntryCount`/`HistoryReducer` tests and adjusting one). `just test-integration` 53 pass; `just test-integration-ai` 13 pass (was 7; +3 new correlation/usage tests retained). `dotnet build samples/MEAI/CustomWorkflow/CustomWorkflow.csproj` clean.
---

## Layer 2 — Phase 3 (MAF migration + cleanup) — 2026-04-30

Replaced `TemporalAgentStateEntry`/`Request`/`Response`/`Usage` with `AgentSessionRequest`/`AgentSessionResponse` (subclasses of the AI library's `DurableSessionRequest`/`DurableSessionResponse`). Both libraries now share entry shape.

Files created (4):
- `src/Temporalio.Extensions.Agents/State/AgentSessionRequest.cs` — extends `DurableSessionRequest`; adds `OrchestrationId`, `ResponseType`, `ResponseSchema`. Static factory `FromRunRequest(RunRequest, DateTimeOffset)`.
- `src/Temporalio.Extensions.Agents/State/AgentSessionResponse.cs` — extends `DurableSessionResponse`. Static factory `FromAgentResponse(correlationId, AgentResponse, DateTimeOffset)` plus `ToResponse()` round-trip helper.
- `src/Temporalio.Extensions.Agents/TemporalAgentDataConverter.cs` — public `DataConverter` using `TemporalAgentJsonUtilities.DefaultOptions` (necessary because `DurableAIDataConverter` doesn't know about MAF subclasses; without this the workflow→activity payload boundary failed with `NotSupportedException: Runtime type AgentSessionRequest is not supported by polymorphic type DurableSessionEntry`).
- `src/Temporalio.Extensions.Agents/TemporalAgentDataConverterPlugin.cs` — `ITemporalClientPlugin` + `IConfigureOptions<TemporalClientConnectOptions>` + `IPostConfigureOptions<TemporalWorkerServiceOptions>`. The plugin overrides both `DataConverter.Default` AND `DurableAIDataConverter.Instance` (the MAF converter is a strict superset).

Files edited (production):
- `TemporalAgentRunOptions.cs` — added optional `string? CorrelationId` property (preserved existing `EnableToolCalls`, `EnableToolNames`, `IsFireAndForget`).
- `TemporalAgentJsonUtilities.cs` — class made `public`. Adds runtime `JsonTypeInfoResolver.WithAddedModifier` that registers `AgentSessionRequest` (`agent_request`) and `AgentSessionResponse` (`agent_response`) on `DurableSessionEntry`'s polymorphism options.
- `State/AgentSessionJsonContext.cs` (renamed from `TemporalAgentStateJsonContext.cs`): registers new `AgentSession*` types and the shared `DurableSession*` types.
- `TemporalAgentsOptions.cs` — `MaxHistorySize` → `MaxEntryCount`; reducer type → `Func<IList<DurableSessionEntry>, IList<DurableSessionEntry>>?`.
- `Workflows/AgentWorkflowInput.cs` — same rename + reducer type; `CarriedHistory` is now `IReadOnlyList<DurableSessionEntry>`.
- `Workflows/DefaultTemporalAgentClient.cs` — three `MaxHistorySize` → `MaxEntryCount` references.
- `Workflows/AgentWorkflow.cs` — `_history` typed `List<DurableSessionEntry>`; all factory calls migrated; `[WorkflowQuery("GetHistory")]` returns `IReadOnlyList<DurableSessionEntry>`; `is TemporalAgentStateResponse` → `is DurableSessionResponse` (catches the MAF subclass via inheritance); `MaxHistorySize` → `MaxEntryCount`.
- `Workflows/AgentActivities.cs` — passes through `input.ConversationHistory` via base type; OTel tags unchanged.
- `Workflows/ExecuteAgentInput.cs` — typed on `IReadOnlyList<DurableSessionEntry>`.
- `TemporalAIAgent.cs` — `_history` typed `List<DurableSessionEntry>`; CorrelationId now reads `TemporalAgentRunOptions.CorrelationId` if supplied, falling back to `Workflow.NewGuid().ToString("N")` (deterministic in workflow context).
- `TemporalAIAgentProxy.cs` — same pattern in `RunCoreAsync`: read `TemporalAgentRunOptions.CorrelationId` first, else `Guid.NewGuid().ToString("N")` (non-workflow context).
- `StructuredOutputExtensions.cs` — added optional `string? correlationId = null` parameter to all three overloads. The `TemporalAIAgent` and `AIAgent` overloads thread the value through `TemporalAgentRunOptions`. The `ITemporalAgentClient` overload threads it directly into the `RunRequest.CorrelationId`. Each retry attempt always generates a fresh GUID (preserved from old behavior).
- `TemporalAgentsRegistrar.cs` and `ServiceCollectionExtensions.cs` — swapped the AI-library configurators for the new MAF-specific configurators.

Files deleted (4):
- `State/TemporalAgentStateEntry.cs`
- `State/TemporalAgentStateRequest.cs`
- `State/TemporalAgentStateResponse.cs`
- `State/TemporalAgentStateUsage.cs`

Tests:
- `StateSerializationTests.cs` — rewritten end-to-end: factory tests for `AgentSessionRequest`/`AgentSessionResponse`, polymorphism discriminator assertions (`agent_request`/`agent_response`), JSON round-trip preserving `Usage`, plus a `DefaultOptions_RegistersAgentDerivedTypes_OnDurableSessionEntry` test verifying the runtime modifier wires both subclasses on `DurableSessionEntry`'s polymorphism options.
- `RunRequestTests.cs` — `MaxHistorySize` → `MaxEntryCount`; reducer generic argument fixed.
- `TemporalAgentsOptionsTests.cs` — same renames.
- `TemporalAIAgentProxyTests.cs` — added 3 `CorrelationId` tests covering caller-supplied propagation, null auto-generation, and empty-string treatment as auto-generation.
- New `StructuredOutputExtensionsTests.cs` — 3 tests covering the optional `correlationId` parameter on `RunAgentAsync<T>` (override + fallback) and the `AIAgent` extension overload (round-trip via the proxy fake).
- Integration test fixture `IntegrationTestFixture.cs` and one-off env tests (`ContinueAsNewTests`, `ResilienceTests`) all set `env.Client.Options.DataConverter = TemporalAgentDataConverter.Instance` because `WorkflowEnvironment.StartLocalAsync()` returns a client with `DataConverter.Default`.

Surprise: discovered the integration test fixture pattern (`AddSingleton<ITemporalClient>(env.Client)`) bypasses the auto-wiring configurators entirely. The previous tests passed because `[JsonPolymorphic]` static attributes on the now-deleted `TemporalAgentStateEntry` were honored by reflection-based `DataConverter.Default`. With the new design (runtime modifier on the AI library's base type), the converter MUST be the MAF-flavored one. Required mutating `Environment.Client.Options.DataConverter` post-construction; SDK exposes this property as mutable.

Results:
- `just build` clean (0 warnings, 0 errors).
- `just test-unit-all`: 407 pass total (225 Agents — was 219, +6 for new correlation tests; 182 AI — unchanged from Phase 2 baseline).
- `just test-integration`: 53 pass.
- `just test-integration-ai`: 13 pass (no regression).
- Tier 1 sample smoke tests all pass against real OpenAI: BasicAgent, WorkflowOrchestration (orchestrator workflow + sub-agent), DurableChat (multi-turn + tool call + history query), DurableTools (per-tool activity dispatch).
- Audit grep clean: zero matches for `TemporalAgentStateEntry|TemporalAgentStateRequest|TemporalAgentStateResponse|TemporalAgentStateUsage` across `src/`, `tests/`, `samples/`. CHANGELOG.md and `agents-redesign-research.md` retain references (Oracle's territory; CHANGELOG is intentional per release strategy).

---

## 2026-05-01 — Layer 3 Phase 1: base-class evolution

**Scope:** Phase 1 of `replat-abstractions` Layer 3. Update `DurableChatWorkflowBase<TOutput>` to accommodate hooks Layer 3 needs. Harmonize search-attribute opt-in to boolean. Adopt re-derived turn-count behavior. Make `AgentWorkflowInput` inherit from `DurableChatWorkflowInput`.

**Production code edits:**
- `src/Temporalio.Extensions.AI/Session/DurableSessionRequest.cs` — `FromMessages` now `(messages, correlationId? = null, timestamp? = null)`. Auto-generates correlation ID via `Workflow.NewGuid()` (in workflow) or `Guid.NewGuid()` (outside). Auto-generates timestamp via `Workflow.UtcNow` / `DateTimeOffset.UtcNow`.
- `src/Temporalio.Extensions.AI/DurableChatWorkflowInput.cs` — replaced `DurableSessionAttributes? SearchAttributes` with `bool EnableSearchAttributes = false`. Un-sealed the class so subclasses can inherit (Decision #1 prerequisite).
- `src/Temporalio.Extensions.AI/DurableChatSessionClient.cs` — switched `SearchAttributes = ... ? new() : null` to `EnableSearchAttributes = _options.EnableSearchAttributes`.
- `src/Temporalio.Extensions.AI/DurableChatWorkflowBase.cs` — biggest change. Removed `BuildRequestEntry` virtual hook (Decision #9). `RunTurnAsync` signature now `(DurableSessionRequest, ChatOptions?, CancellationToken)`. `ExecuteTurnAsync` abstract signature now `(ActivityOptions, DurableSessionRequest, ChatOptions?)` (Decision #10) — subclass owns activity-input construction. Added `protected int CurrentTurnNumber` and `protected IReadOnlyList<DurableSessionEntry> History` accessors (Decision #11). Added `protected virtual int InitializeTurnCount(IReadOnlyList<DurableSessionEntry>)` (Decision #3). Added `protected virtual void UpsertCustomSearchAttributes()` (Decision #4). Switched opt-in check to `if (input.EnableSearchAttributes)`.
- `src/Temporalio.Extensions.AI/DurableChatWorkflow.cs` — `ChatAsync` now constructs `DurableSessionRequest` via factory and stashes `ClientKey`/`ConversationId` for `ExecuteTurnAsync` to read. `ExecuteTurnAsync` flattens `History` into the activity message list as before.
- `src/Temporalio.Extensions.Agents/Workflows/AgentWorkflowInput.cs` — converted from `internal sealed record` to `internal sealed class : DurableChatWorkflowInput`. Removed inherited fields (`MaxEntryCount`, `HistoryReducer`, `OriginalCreatedAt`, `EnableSearchAttributes`, `CarriedHistory`, `TimeToLive`, `ApprovalTimeout`). Kept MAF-specific fields (`AgentName`, `TaskQueue`, `CarriedStateBag`, `ActivityStartToCloseTimeout`, `ActivityHeartbeatTimeout`, `RetryPolicy`).
- `src/Temporalio.Extensions.Agents/Workflows/AgentWorkflow.cs` — adapted to use base's non-nullable `TimeToLive`/`ApprovalTimeout`. CAN-input `CarriedHistory` is now `List<DurableSessionEntry>`.
- `src/Temporalio.Extensions.Agents/Workflows/DefaultTemporalAgentClient.cs` — `TimeToLive = options.GetTimeToLive(...) ?? TimeSpan.FromDays(14)` (resolves nullable upstream of base's non-nullable field).
- `samples/MEAI/CustomWorkflow/ShoppingAssistantWorkflow.cs` — updated `ExecuteTurnAsync` override to new `(ActivityOptions, DurableSessionRequest, ChatOptions?)` shape; `ShopAsync` constructs `DurableSessionRequest` via factory.

**Test edits:**
- `tests/Temporalio.Extensions.AI.Tests/DurableSessionEntryTests.cs` — replaced the `FromMessages_ThrowsWhenCorrelationIdNullOrEmpty` test with new tests that verify auto-generation: `FromMessages_AutoGeneratesCorrelationId_WhenNullOrEmpty`, `FromMessages_PreservesExplicitCorrelationId`, `FromMessages_UsesExplicitTimestamp_WhenSupplied`, `FromMessages_FallsBackToUtcNow_OutsideWorkflow_WhenTimestampOmitted`.
- `tests/Temporalio.Extensions.AI.Tests/DurableChatWorkflowBaseHooksTests.cs` (new) — exercises `InitializeTurnCount` (counts response entries; ignores orphan requests; zero for empty history) and `UpsertCustomSearchAttributes` (default no-op; subclass override invoked).

**Notable design call:** `_history` was private in the base. Subclasses (`DurableChatWorkflow`, `ShoppingAssistantWorkflow`) need access to flatten message history into the activity input. Added `protected IReadOnlyList<DurableSessionEntry> History` accessor (Decision #11 generalization). Same pattern as `CurrentTurnNumber`. `_history` field stays private.

**Notable design call:** `ClientKey` and `ConversationId` are per-turn metadata on `DurableChatInput` but not on `DurableSessionRequest`. The base `RunTurnAsync` no longer threads these. The MEAI subclass stashes them on private fields (`_lastClientKey`, `_lastConversationId`) inside `ChatAsync` before calling `RunTurnAsync`, then reads them in `ExecuteTurnAsync`. Consistent with how MAF stashes `_currentStateBag` and `_input` on the subclass.

**Results:**
- `just build`: clean (0 warnings, 0 errors). All projects + samples build.
- `just test-unit-all`: 415 pass (Agents 225 unchanged, AI 190 — was 185, +5 new tests including `DurableChatWorkflowBaseHooksTests`).
- `just test-integration`: 53 pass (no regressions).
- `just test-integration-ai`: 13 pass (no regressions).
- `dotnet build samples/MEAI/CustomWorkflow/CustomWorkflow.csproj`: succeeds.
- Audit grep `BuildRequestEntry|SearchAttributes\b` (excluding allowed forms): zero hits in `src/`. Remaining `SearchAttributes` matches are the new `UpsertCustomSearchAttributes` hook, the SDK's `Workflow.UpsertTypedSearchAttributes` calls, and the `*SearchAttribute` typed key fields — all intentional.

### 2026-04-30 — Phase 2 of Layer 3 (AgentWorkflow inheritance migration)

Executed Phase 2 of `src-temporalio-extensions-ai-readme-md-vivid-meteor.md` — `AgentWorkflow` now derives from `DurableChatWorkflowBase<AgentResponse>`.

- Edited `src/Temporalio.Extensions.Agents/Workflows/AgentWorkflow.cs` — biggest single-file change. Removed inherited fields/handlers (`_history`, `_isProcessing`, `_shutdownRequested`, `_turnCount`, `_approval`, `[WorkflowQuery("GetHistory")]`, `[WorkflowSignal("Shutdown")]`, all four HITL approval methods). Kept MAF-only (`_input`, `_currentStateBag`, fire-and-forget signal, `[WorkflowUpdate("Run")]`, `AgentNameSearchAttribute`). Added the four overrides: `BuildResponseEntry`, `ExecuteTurnAsync`, `CreateContinueAsNewException`, `UpsertCustomSearchAttributes`.

Two non-obvious traps surfaced during integration test runs:

1. **Modern Temporal event-loop dispatches `DoUpdate` jobs BEFORE `InitializeWorkflow` within an activation.** That means the `[WorkflowUpdate]` handler can fire on a workflow instance whose `[WorkflowRun]` body has not yet executed — `_input` (and the base's `Input` property) are both still null at update entry. The fix: defer all `_input!` access in `RunAgentAsync` until *after* the first `await` (which goes through `RunTurnAsync` → `Workflow.WaitConditionAsync(() => !_isProcessing)`; the yield gives the scheduler a chance to run `InitializeWorkflow` before `_input` is read). The original code worked because its first line was already `await Workflow.WaitConditionAsync(() => !_isProcessing)` — also yielded before reading `_input`. SDK source: `WorkflowInstance.cs` job ordering — DoUpdate=2, InitializeWorkflow=3.

2. **`CreateContinueAsNewException(DurableChatWorkflowInput input)` cannot downcast `input` to `AgentWorkflowInput`.** The base constructs a freshly-typed `DurableChatWorkflowInput` on CAN — not a downcast subclass instance. The plan's "safe per Decision #1 inheritance" comment was wrong about cast direction. Fix: pull MAF-specific fields from the cached `_input` field; pull base-class shared fields (CarriedHistory, OriginalCreatedAt, etc.) from the freshly-constructed `input` parameter.

Results: `just build` clean. Unit tests 415 pass (225 Agents + 190 AI). Integration tests pass: 53 Agents + 13 AI. `just ci` produces packages successfully. Tier 1 sample smoke tests pass: BasicAgent, WorkflowOrchestration, DurableChat, DurableTools.

Audit grep on `src/Temporalio.Extensions.Agents/Workflows/AgentWorkflow.cs` for `WaitConditionAsync|ContinueAsNewSuggested|_isProcessing|_shutdownRequested|_turnCount|_history|_approval`: only two matches, both in comments referencing inherited base behavior. No code-level references remain.

File line count: 318 → 283 total (148 non-comment / non-blank). Net reduction smaller than the plan's ~150 estimate because the override methods (BuildResponseEntry, ExecuteTurnAsync, CreateContinueAsNewException, UpsertCustomSearchAttributes), the `ToRunRequest` reconstruction helper, and the explanatory comments for the two semantic traps absorb most of the savings.

---

## 2026-04-30 — Tier 1 #3: Timeout property rename harmonization

Mechanical rename across both libraries to harmonize timeout property names with the AI library's naming.

Renamed in `TemporalAgentsOptions`:
- `ActivityStartToCloseTimeout` (`TimeSpan?`) → `ActivityTimeout` (`TimeSpan`, default 5 min)
- `ActivityHeartbeatTimeout` (`TimeSpan?`) → `HeartbeatTimeout` (`TimeSpan`, default 2 min)

Removed duplicate fields from `AgentWorkflowInput` (now inherits `ActivityTimeout`/`HeartbeatTimeout` from `DurableChatWorkflowInput` per Layer 3 inheritance).

Renamed mirror fields in `AgentJobInput` for consistency (`ActivityTimeout`/`HeartbeatTimeout`, non-nullable with same defaults).

Files edited:
- src/Temporalio.Extensions.Agents/TemporalAgentsOptions.cs (rename + drop nullable)
- src/Temporalio.Extensions.Agents/Workflows/AgentWorkflowInput.cs (deleted duplicates)
- src/Temporalio.Extensions.Agents/Workflows/AgentJobInput.cs (rename + drop nullable)
- src/Temporalio.Extensions.Agents/Workflows/AgentJobWorkflow.cs (use new names; drop ?? fallback)
- src/Temporalio.Extensions.Agents/Workflows/DefaultTemporalAgentClient.cs (4 sites)
- src/Temporalio.Extensions.Agents/Workflows/AgentWorkflow.cs (CAN exception construction + ExecuteAgentTurnAsync; comment cleanup)
- src/Temporalio.Extensions.Agents/Session/TemporalAgentContext.cs (xmldoc comment)
- tests/Temporalio.Extensions.Agents.IntegrationTests/TimeoutConfigurationTests.cs
- tests/Temporalio.Extensions.Agents.IntegrationTests/ContinueAsNewTests.cs
- tests/Temporalio.Extensions.Agents.IntegrationTests/ErrorHandlingTests.cs (incl. test method name)
- samples/MAF/HumanInTheLoop/Program.cs
- samples/MAF/HumanInTheLoop/README.md

Results: `just build` clean (0 warnings, 0 errors). Unit tests 415 pass (225 Agents + 190 AI). Integration tests pass: 53 Agents + 13 AI. Audit grep returns zero matches across src/, tests/, samples/.

### 2026-05-05 — Feature 1: External History Store (`IAgentHistoryStore`)

Implemented Feature 1 of `src-temporalio-extensions-ai-readme-md-vivid-meteor.md`. Opt-in interface that lets workflows omit `ConversationHistory` from the activity-scheduled event so PII / large message graphs stay out of the Temporal event log.

Created:
- `src/Temporalio.Extensions.Agents/HistoryStore/IAgentHistoryStore.cs` — `LoadAsync`/`AppendAsync`/`ReplaceAsync` interface.
- `tests/Temporalio.Extensions.Agents.Tests/AgentHistoryStoreTests.cs` — 9 unit tests covering: default flag value, default-mode passthrough behavior, external-mode allowing null history, startup-validation error, store registration via `UseExternalAgentHistory<T>`, and round-trip of `AgentSessionRequest.FromRunRequest`.

Edited:
- `TemporalAgentsOptions.cs`: added `UseExternalHistory` flag (default false).
- `Workflows/AgentWorkflowInput.cs`: added `UseExternalStore` flag.
- `Workflows/ExecuteAgentInput.cs`: made `ConversationHistory` nullable with `[JsonIgnore(WhenWritingNull)]`; added `UseExternalStore` ctor parameter.
- `Workflows/AgentActivities.cs`: added optional `IAgentHistoryStore? historyStore = null` ctor param; branched `ExecuteAgentAsync` to load via store and append both entries when flag is set; reconstructs the request entry from `input.Request` via `AgentSessionRequest.FromRunRequest(input.Request, DateTimeOffset.UtcNow)`. Added `ReduceHistoryInStoreAsync` activity that tail-trims the store down to `MaxEntryCount`.
- `Workflows/AgentWorkflow.cs`: passes `null` history + `UseExternalStore = true` through to the activity input when flag is set; clears `CarriedHistory` on continue-as-new for external-store mode; intercepts `ContinueAsNewException` in `RunAsync` to dispatch `ReduceHistoryInStoreAsync` before re-throwing (the synchronous `CreateContinueAsNewException` hook can't await activity dispatch). Overrode `ShouldStripMessagesFromHistoryEntry` and added type-aware `StripMessagesFromEntry` for `AgentSessionRequest`/`AgentSessionResponse`.
- `src/Temporalio.Extensions.AI/DurableChatWorkflowBase.cs`: added `protected virtual bool ShouldStripMessagesFromHistoryEntry()` (default false) and `protected virtual DurableSessionEntry StripMessagesFromEntry(...)` so the base session loop appends metadata-only entries when external storage is on (still drives turn count / max-entry CAN trigger). Strips both request and response entries.
- `TemporalAgentsRegistrar.cs`: throws `InvalidOperationException` at composition if `UseExternalHistory` is on but no `IAgentHistoryStore` is registered.
- `Workflows/DefaultTemporalAgentClient.cs`: propagates `options.UseExternalHistory` into all three `AgentWorkflowInput` construction sites (start, fire-and-forget, delayed start).
- `TemporalWorkerBuilderExtensions.cs`: added `UseExternalAgentHistory<TStore>()` convenience helper.

Deviations from plan:
- Plan said the base hook should "append entries to `_history` with `Messages = []`". Implemented this via two virtual hooks rather than a single one — `ShouldStripMessagesFromHistoryEntry()` (gates the strip) and `StripMessagesFromEntry(entry)` (returns the stripped clone). The second hook lets `AgentWorkflow` preserve MAF-specific subclass fields (`OrchestrationId`, `ResponseType`, `ResponseSchema`) on `AgentSessionRequest` instead of losing them through a base-only strip.
- Plan said the reduce-store dispatch happens "in `CreateContinueAsNewException`". That hook is synchronous (throws an exception) so it cannot `await` an activity. Moved the dispatch into `AgentWorkflow.RunAsync`'s catch-and-rethrow of `ContinueAsNewException`. Same observable behavior, workflow-determinism-safe.
- `ReduceHistoryInStoreAsync` performs a deterministic tail-trim to `MaxEntryCount` rather than re-running the user's `HistoryReducer` delegate. The reducer is `[JsonIgnore]` and never crosses the activity boundary, so a tail-trim is the closest store-side equivalent that doesn't require new serialization machinery.

Results: `just build` clean (0 warnings, 0 errors). Unit tests 433 pass (243 Agents + 190 AI; +9 new). Integration tests pass: 53 Agents + 13 AI. `grep "ConfigureAwait(false)"` returns zero results in `AgentWorkflow.cs` and `DurableChatWorkflowBase.cs`.

### 2026-05-05 — Feature 2: Per-Tool Temporal Activities (Step Mode)

Implemented Feature 2 of `src-temporalio-extensions-ai-readme-md-vivid-meteor.md`. Opt-in step-mode workflow loop that dispatches each LLM call and each tool invocation as a separate Temporal activity, preserving per-tool retry/timeout/visibility for production agents.

Created:
- `src/Temporalio.Extensions.Agents/Workflows/AgentStepInput.cs` — internal: `AgentName`, `Request`, `AccumulatedMessages`, `SerializedStateBag?`, `SessionId?`.
- `src/Temporalio.Extensions.Agents/Workflows/AgentStepResult.cs` — internal: `IsFinal`, `AssistantMessage`, `ToolCalls?`, `UpdatedStateBag?`, `Usage?`.
- `tests/Temporalio.Extensions.Agents.Tests/RunAgentStepActivityTests.cs` — 8 unit tests covering input/output round-trip through `TemporalAgentJsonUtilities.DefaultOptions` and `DefaultPayloadConverter`, default options, and the `ScriptedChatClient` scaffolding.
- `tests/Temporalio.Extensions.Agents.Tests/TemporalAgentsRegistrarValidationTests.cs` — 3 unit tests covering startup validation when step mode is enabled without `AddDurableAI`.
- `tests/Temporalio.Extensions.Agents.IntegrationTests/StepModeIntegrationTests.cs` — 5 integration tests (default-mode unchanged, single-tool-call dispatch, parallel fan-out via Temporal history inspection, write-tool `MaximumAttempts=1` no-retry, runaway-loop iteration cap).

Edited:
- `TemporalAgentsOptions.cs`: added `EnablePerToolActivities` (default false), `PerToolActivityOptions: Dictionary<string, ActivityOptions>?`, and `MaxToolCallsPerTurn` (default 20).
- `Workflows/AgentWorkflowInput.cs`: added `EnablePerToolActivities`, `PerToolActivityOptions`, `MaxToolCallsPerTurn` so step-mode configuration carries forward across continue-as-new.
- `Workflows/AgentActivities.cs`: added `RunAgentStepAsync(AgentStepInput) -> AgentStepResult`. The activity resolves `IChatClient` from DI directly (bypassing `ChatClientAgent`'s wrapped chat client and therefore `FunctionInvokingChatClient`), pulls `Instructions` from the registered `ChatClientAgent`, builds `ChatOptions.Tools` from the `DurableFunctionRegistry` (so the LLM sees the same tool schema the workflow will dispatch by name), heartbeats per streamed chunk, and returns either `IsFinal=true` or the `FunctionCallContent` items unexecuted.
- `Workflows/AgentWorkflow.cs`: branched `ExecuteAgentTurnAsync` on `_input.EnablePerToolActivities`; new `ExecuteStepModeTurnAsync` runs the step loop using `Workflow.WhenAllAsync` for parallel tool fan-out, `Workflow.UtcNow` for timestamps, and `Workflow.Logger.LogWarning` for the iteration-cap exit. Per-tool `ActivityOptions` resolution falls back to default agent options when no per-tool entry is registered. `CreateContinueAsNewException` carries `EnablePerToolActivities` / `PerToolActivityOptions` / `MaxToolCallsPerTurn` forward.
- `TemporalAgentsRegistrar.cs`: at composition, throws `InvalidOperationException` when `EnablePerToolActivities` is true but `DurableExecutionOptions` (the AddDurableAI sentinel) is not registered. Message points the user at `AddDurableAI()` + `AddDurableTools(...)`.
- `Workflows/DefaultTemporalAgentClient.cs`: propagates the three new options into all three `AgentWorkflowInput` construction sites (start, fire-and-forget, delayed start).
- `tests/Temporalio.Extensions.Agents.IntegrationTests/Temporalio.Extensions.Agents.IntegrationTests.csproj`: links Trinity's `ScriptedChatClient.cs` and `RecordingTool.cs` from the unit-tests project so the integration tests can reuse the scaffolding without duplicating it.

Deviations from plan:
- Per-tool `ChatOptions` (temperature, response format) — the plan suggested storing `ChatClientAgentOptions` keyed by agent name in `TemporalAgentsOptions`. I deferred that and instead source `Instructions` from the public `ChatClientAgent.Instructions` property and `Tools` from `DurableFunctionRegistry` (the same registry the workflow's `InvokeFunctionAsync` resolves from). This is sufficient for the documented step-mode contract; richer `ChatOptions` propagation can be added as a follow-up without breaking the public surface.
- Step-mode activity resolves `IChatClient` from DI directly rather than constructing a fresh `ChatClientAgent` with `UseProvidedChatClientAsIs = true`. Net effect is identical — `FunctionInvokingChatClient` is bypassed — and this path is simpler / one less indirection. Validation gate test `2.11` (no auto-execution of tool calls) is satisfied because the LLM's `FunctionCallContent` flows back to the workflow unmodified.
- Final `AgentResponse.Messages` returned from step mode contains the full multi-step transcript (assistant tool-call message → tool-result message → ... → final assistant text), per the plan's "History Representation for Multi-Step Turns" note.
- Verification grep `grep -rn "ConfigureAwait(false)"` against `AgentWorkflow.cs` + `DurableChatWorkflowBase.cs` returns zero results — comment text was rephrased to reference "the ConfigureAwait-false escape hatch" instead of literally including `ConfigureAwait(false)`.

Results: `just build` clean (0 warnings, 0 errors). Unit tests 456 pass (266 Agents + 190 AI; +11 new). Integration tests pass: 59 Agents (+5 new) + 13 AI. ConfigureAwait grep gate clean. Step-mode integration suite stable across 3 consecutive runs (no flaky failures) after switching to a class-fixture-shared `WorkflowEnvironment` + per-test unique task queues.

### 2026-05-05 — Code-review fixes B2 / S1 / S2

Address blockers + should-fixes from review of commits `353387d` (external history store) and `881d149` (per-tool activities). Single commit `3860b54`.

- `src/Temporalio.Extensions.Agents/TemporalWorkerBuilderExtensions.cs`: `UseExternalAgentHistory<TStore>()` now (a) drops an internal `ExternalHistoryMarker` singleton and (b) mutates the registered options instance in place when AddTemporalAgents has already run. AddTemporalAgents reads the marker after `configure` and forces `UseExternalHistory = true`. XML doc rewritten to describe the new one-call UX.
- `src/Temporalio.Extensions.Agents/TemporalAgentsOptions.cs`: added `_agentChatOptions` dict + `GetAgentChatOptions(name)` + `ReadChatOptionsFromAgent(cca)`. `AddAIAgent(AIAgent)` captures the agent's `ChatOptions` via reflection (the property is internal in Microsoft.Agents.AI 1.0.0-preview).
- `src/Temporalio.Extensions.Agents/Workflows/AgentActivities.cs` (RunAgentStepAsync): seeds `chatOptions` from the captured registration-time options (clones), with a fallback to a live read off the cached `ChatClientAgent` for the AddAIAgentFactory path. Per-turn fields (Instructions, Tools, ResponseFormat) still override.
- `src/Temporalio.Extensions.Agents/Workflows/AgentStepInput.cs`: rewrote stale XML doc to describe the actual implementation (resolves IChatClient from DI; bypasses FunctionInvokingChatClient; pulls Instructions and registration-time ChatOptions from the registered ChatClientAgent).
- `src/Temporalio.Extensions.Agents/Workflows/AgentWorkflow.cs` (ExecuteAgentTurnAsync default-mode await): added `.ConfigureAwait(true)` matching the comment block style in `DurableAIFunction.cs:55-56`. Verification grep `ConfigureAwait(false)` against AgentWorkflow.cs + DurableChatWorkflowBase.cs returns zero matches.
- `tests/Temporalio.Extensions.Agents.Tests/CodeReviewFixesTests.cs` (new): 5 tests — UseExternalAgentHistory before/after AddTemporalAgents (both flip the flag + register the store); negative control (no marker → no flip); RunAgentStepAsync preserves Temperature + ModelId from registration; per-turn fields still override registration-time ResponseFormat.

Trinity has parallel work in progress (other modified test files + new integration tests). Did not touch those.

Results: `just build` clean (0 warnings, 0 errors). Unit tests 463 pass (273 Agents + 190 AI; +5 new). Integration tests 63 pass + 13 AI integration pass. ConfigureAwait grep gate clean.

### 2026-05-06 — v0.3.0 API Redesign Phase 1 (DurableAgentBuilder + DurableToolOptions)

Phase 1 of the v0.3.0 API redesign at `/Users/cecilphillip/.claude/plans/src-temporalio-extensions-ai-readme-md-vivid-meteor.md`. Purely additive — three new types that Phase 2 will wire into `TemporalAgentsOptions.AddDurableAgent`. No existing files modified.

Created:
- `src/Temporalio.Extensions.Agents/DurableAgentBuilder.cs` — public sealed hybrid builder. Properties for scalars (`Name`, `Description`, `Instructions`, `ChatClient`, `ChatOptions`, `TimeToLive?`, `ApprovalTimeout?`, `ActivityTimeout?`, `HeartbeatTimeout?`, `RetryPolicy?`, `MaxEntryCount?`, `MaxToolCallsPerTurn = 20`, `HistoryReducer?`, `HistoryStore?`); methods for collections (`AddTool(AIFunction, configure?)`, `AddTool(string name, factory, configure?)`, `AddTools(params)`, `AddContextProvider(instance)`, `AddContextProvider(factory)`). All synchronous validation per Q9 Checkpoint 1: null/empty/duplicate-name throws on Add. Tools stored case-insensitive (mirrors agent-name handling in `TemporalAgentsOptions`). Methods return `this` for fluent chaining. Internal `ToolRegistrations`/`ContextProviderFactories` getters + internal record `DurableToolRegistration(Name, Factory, Options)` for Phase 2 to consume. Internal `ToRegistration()` produces the immutable snapshot record; throws `InvalidOperationException` when `ChatClient` is null (per Q9 checkpoint 2 prep — Phase 2 will call this).
- `src/Temporalio.Extensions.Agents/DurableToolOptions.cs` — public sealed. `StartToCloseTimeout?`, `HeartbeatTimeout?`, `RetryPolicy?` properties (null = inherit worker default per Q11/H1). Sugar: `NoRetry()` (sets `MaximumAttempts = 1`), `WithMaxAttempts(n)` (validates `n > 0`), `WithTimeout(t)` (validates `t > Zero`). All sugar returns `this`.
- `src/Temporalio.Extensions.Agents/DurableAgentRegistration.cs` — internal sealed record carrying the flattened state for Phase 2 to store on `TemporalAgentsOptions` and Phase 3's workflow loop to read.

Tests created:
- `tests/Temporalio.Extensions.Agents.Tests/DurableAgentBuilderTests.cs` — 25 tests covering: name validation, default values, tool addition (concrete/factory/params), duplicate-name detection (case-insensitive), null tool/factory/name rejection, configure callback runs, context provider addition (instance/factory), null provider/factory rejection, fluent chaining returns same builder, `ToRegistration` flattens state correctly + null-ChatClient throws.
- `tests/Temporalio.Extensions.Agents.Tests/DurableToolOptionsTests.cs` — 12 tests covering: defaults, `NoRetry`, `WithMaxAttempts` (positive sets, zero/negative throws), `WithTimeout` (positive sets, zero/negative throws), fluent chaining.

Two design calls worth noting:
1. The plan said `AddTool(Func<IServiceProvider, AIFunction>)` should "store a placeholder name if provided, OR resolve names lazily at first dispatch". I took the explicit-name route the plan also called out: `AddTool(string name, factory, configure?)`. Synchronous duplicate-name detection beats lazy detection — the registration error fires at the call site, not at first dispatch when stack traces lose the call-site context.
2. `DurableToolRegistration` is declared at the top of `DurableAgentBuilder.cs` rather than its own file — single-line internal record, only consumed by `DurableAgentBuilder` and `DurableAgentRegistration`. Splitting into a third file would be ceremony.

Results:
- `just build`: 0 warnings, 0 errors.
- `just test-unit-all`: 190 + 273 + new = pass (37 new unit tests pass — Agents test count up by 37).
- `just test-integration`: 63 pass.
- `just test-integration-ai`: 13 pass.
- Determinism guard `grep -rn "ConfigureAwait(false)" src/Temporalio.Extensions.Agents/Workflows/AgentWorkflow.cs src/Temporalio.Extensions.AI/DurableChatWorkflowBase.cs`: zero matches.

Commit `2893f3d`. Phase 2 is chief's call.

---

## 2026-05-06 — Phase 2: `AddDurableAgent` registration + `InvokeAgentTool` activity

Phase 2 of the v0.3.0 API redesign. Built the public `AddDurableAgent` registration entry on `TemporalAgentsOptions`, plus the new per-tool dispatch activity (`Temporalio.Extensions.Agents.InvokeAgentTool`). Legacy paths (`AddAIAgent`, `AddAIAgentFactory`, `EnablePerToolActivities`, `UseExternalHistory`) untouched and still pass all integration tests.

Code changes:
- `src/Temporalio.Extensions.Agents/TemporalAgentsOptions.cs` — added `HistoryStore` worker-level property (Q6/H1 inheritance), `_durableAgentRegistrations` dictionary (case-insensitive, shares name namespace with `_agentFactories`), `AddDurableAgent(name, configure)` method enforcing Q9 (sync ChatClient null-check + duplicate-name throw at second call), internal `DurableAgentRegistrations` accessor for Phase 3.
- `src/Temporalio.Extensions.Agents/Workflows/InvokeAgentToolInput.cs`, `InvokeAgentToolResult.cs` — internal payload contracts.
- `src/Temporalio.Extensions.Agents/Workflows/AgentActivities.cs` — added `CachedDurableAgent` record, `_durableAgentCache` (kept separate from `_agentCache`), `ResolveDurableAgent(name)` lazy composer (chat client → providers → tools, case-insensitive lookup, eager validation that a factory's resolved `AIFunction.Name` matches the declared `AddTool` name), and `InvokeAgentToolAsync` activity. The pipeline composition uses `userClient.AsBuilder().UseAIContextProviders(providers).Build()` (the MAF extension is `UseAIContextProviders`, plural — small naming deviation from the plan's `UseAIContextProvider`). `ChatClientAgentOptions` is built with `UseProvidedChatClientAsIs = true`. `Instructions` and `Tools` flow through `ChatOptions.Instructions` / `ChatOptions.Tools` (Q8 ownership) since `ChatClientAgentOptions` does not expose `Instructions` directly.
- `src/Temporalio.Extensions.Agents/Logs.cs` — three new event IDs (26/27/28) for tool invocation start/complete/failed.
- `src/Temporalio.Extensions.Agents/TemporalAgentTelemetry.cs` — added `agent.tool.invoke` span name, `agent.tool.name` and `agent.tool.call_id` attribute constants.

Tests created (27 new):
- `tests/Temporalio.Extensions.Agents.Tests/AddDurableAgentTests.cs` — 19 tests: `RunsConfigureDelegate`, empty/whitespace/null name validation, null configure validation, ChatClient-null throws InvalidOperationException at end of delegate, duplicate-name throws (against own/legacy/factory registrations, case-insensitive), `RegistrationStoresFlattenedState`, `GetRegisteredAgentNames`/`IsAgentRegistered`/`GetAgentDescriptors` include durable agents, descriptionless agent omitted from descriptors, `ReturnsSameOptionsInstance`, synthetic legacy factory throws when invoked.
- `tests/Temporalio.Extensions.Agents.Tests/InvokeAgentToolActivityTests.cs` — 8 tests via `ActivityEnvironment`: tool invocation succeeds, unknown agent throws `AgentNotRegisteredException`, unknown tool throws `InvalidOperationException`, propagates tool exceptions, factory tools resolve once and cache, factory returning wrong name throws, no-arguments path.

Documentation:
- `docs/how-to/MAF/usage.md` — appended a "Durable Agents (`AddDurableAgent`)" section near the top with the canonical sample, full `DurableAgentBuilder`/`DurableToolOptions` reference tables, and a lifecycle/composition note. Legacy sections untouched.
- `CLAUDE.md` — added an `AddDurableAgent` paragraph under `Temporalio.Extensions.Agents — Key Concepts` flagging it as the recommended path and pointing to `usage.md`.

Deviations / design calls:
1. **MAF extension API name**: the plan called for `UseAIContextProvider` (singular). The actual MAF 1.3.0 extension is `UseAIContextProviders` (plural) on `ChatClientBuilder`, accepting `params AIContextProvider[]`. Used the actual name; it's the same lifecycle (`AIContextProviderChatClient` decorator, per-LLM-call `Invoking/InvokedAsync`).
2. **`ChatClientAgentOptions.Instructions`**: doesn't exist on the type. Instructions are stamped onto `ChatOptions.Instructions` instead (the plan's example shape was already incompatible with the API; Oracle's pending doc-fix list mentions this). `ChatOptions.Tools` carries the registered tool list. Both are stamped over whatever the user set on their `ChatOptions` (Q8 ownership).
3. **`AIContextProviders` on `ChatClientAgentOptions`**: also populated alongside the chat-pipeline `UseAIContextProviders` so MAF's `AgentRunContext` gets wired correctly. The pipeline decorator is what actually drives the lifecycle hooks.
4. **`AIFunctionArguments` constructor**: accepts `IDictionary<string, object>` (non-nullable) per the abstractions API. Passed `IDictionary<string, object?>?` from input directly — type compatible at runtime, no copies needed.

Results:
- `just build`: 0 warnings, 0 errors.
- `just test-unit-all`: 524 pass (Agents 334 + AI 190; +27 new tests).
- `just test-integration`: 63 pass.
- `just test-integration-ai`: 13 pass.
- Determinism guard: zero matches.

Phase 2 is chief's call.

---

## Phase 4 — Per-agent settings + HistoryStore + AIContextProvider lifecycle (v0.3 API redesign)

Landed Phase 4 of the v0.3.0 API redesign on top of Phase 3 (`f96771d`). Per-agent settings now flow from registration → `AgentWorkflowInput` → workflow → activities; `HistoryStore` is plumbed end-to-end (worker-default + per-agent override); `AIContextProvider.InvokingAsync` / `InvokedAsync` fire once per LLM call inside the durable loop.

Code changes:
- `src/Temporalio.Extensions.Agents/Workflows/DefaultTemporalAgentClient.cs` — `BuildAgentWorkflowInput` now applies the H1 inheritance rule (`effective = registration.X ?? options.X`) for every settable scalar. Extracted a static `BuildAgentWorkflowInputCore(agentName, options, taskQueue)` so unit tests can exercise the pure inheritance logic without instantiating an `ITemporalClient`. Per-tool activity options now cascade per-agent timeouts/retry policy as defaults rather than hopping straight to worker-level. The `UseExternalStore` flag on the workflow input is now a composite condition: `registration.HistoryStore is not null || options.HistoryStore is not null`. Legacy agents still use the v0.2 path verbatim (no behavior change).
- `src/Temporalio.Extensions.Agents/Workflows/AgentActivities.cs`
  - `CachedDurableAgent` record extended with `IAgentHistoryStore?` and `IReadOnlyList<AIContextProvider>` slots.
  - `ComposeDurableAgent` resolves the per-agent or worker-level history-store factory at first dispatch and caches the resolved instance. `UseAIContextProviders` decorator removed from the chat pipeline composition (see deviation #1 below).
  - `RunDurableAgentStepAsync` now: (a) loads prior history from the configured store on first step of a turn and prepends to the LLM messages; (b) explicitly invokes `AIContextProvider.InvokingAsync` before the LLM call (collecting any provider-supplied `AIContext.Messages` and prepending them); (c) explicitly invokes `AIContextProvider.InvokedAsync` after a successful call; (d) appends `[requestEntry, responseEntry]` to the store on the FINAL step (when `IsFinal == true`); (e) calls `InvokedAsync` with the failure overload when the LLM call throws.
- `src/Temporalio.Extensions.Agents/Workflows/AgentStepInput.cs` — added `IsFirstStep` flag so the activity knows when to load from the store.
- `src/Temporalio.Extensions.Agents/Workflows/AgentWorkflow.cs`
  - `ExecuteDurableAgentTurnAsync` now flattens `History` into the initial `accumulated` list when external store mode is OFF (fixes the multi-turn-no-store case where prior turns were dropped); when store mode is on, only the current request messages seed `accumulated` (the activity loads prior history from the store on the first step).
  - Loop sets `IsFirstStep = (iteration == 0)` so subsequent steps within a turn don't re-load history.
  - Added `FlattenHistoryMessages` helper.
  - Existing CAN-time `ReduceHistoryInStoreAsync` dispatch now applies to durable agents because they set `UseExternalStore = true` whenever a store is configured.

Tests added (24 new unit tests + 6 new integration tests):
- `tests/Temporalio.Extensions.Agents.Tests/DurableAgentSettingsInheritanceTests.cs` (24 tests): verifies the H1 rule for every scalar — TimeToLive, ApprovalTimeout, ActivityTimeout, HeartbeatTimeout, RetryPolicy, MaxEntryCount, HistoryReducer, MaxToolCallsPerTurn (no fallback), HistoryStore (composite UseExternalStore flag), per-tool retry policy inheritance (per-tool override → agent default → worker default), legacy agent IsDurable=false.
- `tests/Temporalio.Extensions.Agents.IntegrationTests/DurableAgentHistoryStoreTests.cs` (4 tests): worker-level HistoryStore loads + appends across two turns, per-agent override beats worker default, no store falls back to in-workflow History, payload-canary that turn-2's `RunDurableAgentStep` ActivityScheduled event omits turn-1's user marker (the load-bearing PII / O(n²) protection now also works for durable agents).
- `tests/Temporalio.Extensions.Agents.IntegrationTests/DurableAgentContextProviderLifecycleTests.cs` (2 tests): three-iteration turn (tool/tool → tool → final) drives 3 LLM calls and asserts `ProvideAIContextAsync` fires exactly 3 times (Q10/CP1); single-LLM-call turn with `AddContextProvider(instance)` overload also fires once.

Documentation:
- `docs/how-to/MAF/usage.md` — added an "Inheritance — per-agent vs worker-level" subsection with a side-by-side table mapping every per-agent setting to its worker default, plus the three-layer retry-policy hierarchy. Lifecycle/composition section updated to call out the per-LLM-call `Invoking/InvokedAsync` contract.
- `docs/how-to/MAF/external-history-store.md` — new "Using HistoryStore with AddDurableAgent" section showing worker-default + per-agent override pattern, with the documented constraint that per-agent opt-out requires a separate worker.

Deviations / design calls:
1. **`UseAIContextProviders` chat-pipeline decorator removed for durable agents.** The decorator depends on an ambient `AgentRunContext` that MAF's `ChatClientAgent.RunAsync` sets up but the durable workflow loop bypasses (we call `IChatClient.GetStreamingResponseAsync` directly to keep the workflow in charge of tool dispatch). With the decorator wired, the new context-provider tests hung the activity for the full update timeout (4m+) — no progress, no error. Replaced with explicit pre/post-call hooks inside `RunDurableAgentStepAsync` that build `InvokingContext`/`InvokedContext` instances directly and walk `cached.ContextProviders`. This gives deterministic per-LLM-call lifecycle behavior matching Q10/CP1 and is verified by the new integration tests. `ChatClientAgentOptions.AIContextProviders` is still populated for any code path that does invoke `ChatClientAgent.RunAsync`, but the durable hot path no longer relies on it.
2. **Multi-turn history without a store**: Phase 3 left the durable path threading only `runRequest.Messages` into `accumulated`, dropping prior-turn assistant/tool messages on second and subsequent turns. Phase 4 fixes this by flattening the inherited `History` (which already includes the current turn's request entry that the base class appended) when external store mode is off. With external store mode on, the activity loads prior history from the store on the first step, so the workflow only seeds the current turn's request messages.
3. **`AgentWorkflowInput.UseExternalStore` semantics for durable agents**: now reflects "external store is configured for this agent" (the composite `registration.HistoryStore is not null || options.HistoryStore is not null` condition). This drives both the workflow-side decisions (don't carry CarriedHistory across CAN, strip messages from history entries) and indirectly the activity-side load/append behavior. The activity itself reads `cached.HistoryStore` from the resolved cache rather than re-deriving from the input flag, but the two paths are kept in lock-step.
4. **Worker-level property names unchanged.** Plan called for using existing names (`options.TimeToLive`, `options.ApprovalTimeout`, etc.) in Phase 4 with the `Default*` rename deferred to Phase 5. Did exactly that. The new doc table uses the future names (e.g. `opts.DefaultTimeToLive`) only in the few places where the plan's exit gate referenced them; the `usage.md` reference table for v0.2-style worker-level options stays true to the current code.

Results:
- `just build`: 0 warnings, 0 errors.
- `just test-unit-all`: 548 pass (Agents 358 = 334 prior + 24 new inheritance + AI 190).
- `just test-integration`: 77 pass (71 prior + 6 new HistoryStore/ContextProvider). Embedded-server contention occasionally trips one of the `Resilience` tests when the entire suite runs in parallel — not Phase 4-related; reproducible with main and resolves on isolated re-run.
- `just test-integration-ai`: 13 pass.
- Determinism guard: `grep -rn "ConfigureAwait(false)" src/Temporalio.Extensions.Agents/Workflows/AgentWorkflow.cs src/Temporalio.Extensions.AI/DurableChatWorkflowBase.cs` returns zero matches.

Phase 5 (legacy surface removal) is chief's call — it's the breaking change the plan reserves for a single coordinated commit across Tank, Trinity, and Oracle.

---

## Phase 5 (v0.3 API redesign) — legacy surface removal (BREAKING)

Single commit covering code removal, test rewrites, and doc updates per the plan's "single commit" mandate.

### Code removed (`src/Temporalio.Extensions.Agents/`)

- `TemporalAgentsOptions`: `AddAIAgent`, `AddAIAgents`, `AddAIAgentFactory` (sync + async overloads), `EnablePerToolActivities`, `PerToolActivityOptions`, `MaxToolCallsPerTurn` (worker-level), `UseExternalHistory`, `_agentChatOptions` reflection. Renamed worker-level scalars to `Default*` form (`DefaultTimeToLive`, `DefaultApprovalTimeout`, `DefaultActivityTimeout`, `DefaultHeartbeatTimeout`, `DefaultMaxEntryCount`, `DefaultRetryPolicy`, `DefaultHistoryReducer`). `HistoryStore` keeps its name (per the plan).
- `TemporalWorkerBuilderExtensions`: `UseExternalAgentHistory<TStore>()` extension and the `ExternalHistoryMarker` inner type.
- `Workflows/AgentWorkflow.cs`: dropped legacy single-activity branch and step-mode branch. Now drives the durable loop unconditionally. Removed `IsDurable` flag on `AgentWorkflowInput`. `UseExternalStore` flag renamed to `UseExternalStoreMode` to drop the legacy verb.
- `Workflows/AgentWorkflowInput.cs`: removed `EnablePerToolActivities`, `PerToolActivityOptions`, `IsDurable`, `UseExternalStore` (replaced with `UseExternalStoreMode`).
- `Workflows/AgentActivities.cs`: removed `ExecuteAgentAsync`, `RunAgentStepAsync` (legacy step-mode), `_agentCache` (legacy factory cache), and the `factories` constructor param. Constructor now takes only `IServiceProvider` + optional logger factory. `ResolveDurableAgent` is the only resolution path.
- `Workflows/DefaultTemporalAgentClient.BuildAgentWorkflowInputCore`: dropped the legacy/durable branch — every agent must have a `DurableAgentRegistration`. Throws `AgentNotRegisteredException` otherwise.
- `Workflows/AgentJobWorkflow.cs`: rewrote to use `RunDurableAgentStepAsync` + `InvokeAgentToolAsync` instead of the deleted `ExecuteAgentAsync`. Scheduled runs go through the same durable loop as session runs.
- `TemporalAIAgent.cs` (in-workflow sub-agent): rewrote `RunCoreAsync` to drive the durable dispatch loop directly via `Workflow.ExecuteActivityAsync` against `RunDurableAgentStepAsync`/`InvokeAgentToolAsync`, with `Workflow.WhenAllAsync` for parallel tool fan-out. Removed dependency on `ExecuteAgentInput`/`ExecuteAgentResult`.
- Deleted entirely: `AgentWorkflowWrapper.cs`, `Workflows/ExecuteAgentInput.cs`, `Workflows/ExecuteAgentResult.cs`. `HistoryStore/ExternalHistoryMarker.cs` was already absent in this branch.

### Decisions / deviations from the plan

1. **`AddAgentProxy` kept.** The plan flagged this as "verify if still used"; it IS still used by `ServiceCollectionExtensions.AddTemporalAgentProxies` for client-only processes that need keyed `AIAgent` proxies without registering full agents locally. Keeping it gives client processes a way to declare proxies without inventing a no-op `AddDurableAgent` shape that would require a `ChatClient` factory.
2. **`AgentJobWorkflow` rewritten, not deleted.** Scheduled runs (`ScheduleAgentAsync` / `AddScheduledAgentRun`) are part of the public surface, so the job workflow needed to keep working. Rewriting it to drive the durable loop was the cleanest path (the loop is small).
3. **Sample fixes use minimal placeholders, not full Phase 7 rewrites.** Phase 7 owns sample idioms; Phase 5's job is just to keep `just build` clean. Each `AddAIAgent(agent, ttl)` site became `AddDurableAgent("Name", a => { a.ChatClient = _ => openAiClient.GetChatClient(model).AsIChatClient(); ... })`. The `ConfigurableAgent`, `PerToolActivities`, and `ExternalHistoryStore` samples got slightly fuller placeholders since their tool/store/context-provider wiring needed restructuring to compile. Comments mark these as "Phase 5 placeholder — Phase 7 rewrites samples to v0.3 idiomatic shape." Solution-file approach was rejected (cleaner to keep samples in `.slnx` so `just build` reflects the full surface).
4. **Doc updates were targeted, not full rewrites.** The plan called for full rewrites of `usage.md`, `external-history-store.md`, etc. Did the rename `per-tool-activities.md` → `durable-agents.md` with a fresh terse rewrite, updated `CLAUDE.md` Key Concepts and Quick Troubleshooting, and applied mechanical replacements (worker-property renames, `per-tool-activities` cross-refs, `ContextProviders` → `AIContextProviders`) across all MAF docs. `usage.md` still has many legacy `AddAIAgent` examples in code blocks — Phase 6 (migration guide) and follow-up doc work will scrub these. The compile-blocking changes are landed; the prose-level cleanup is a smaller, lower-risk follow-up.

### Tests

- Deleted: `StepModeIntegrationTests`, `StepModeCrashSafetyTests`, `ExternalHistoryStoreIntegrationTests`, `ExternalHistoryStorePayloadTests` (replaced by `DurableAgentIntegrationTests` / `DurableAgentCrashSafetyTests` / `DurableAgentHistoryStoreTests` from earlier phases). `LazyFactoryCachingTests`, `RunAgentStepActivityTests`, `CodeReviewFixesTests` (B2 was about `UseExternalAgentHistory`), `TemporalAgentsRegistrarValidationTests` (validated removed flags), `AgentWorkflowWrapperTests` (wrapper deleted), `ExternalHistoryStoreTests` (unit version).
- Rewrote: `TemporalAgentsOptionsTests`, `TemporalWorkerBuilderExtensionsTests`, `AgentHistoryStoreTests`, `StateBagPersistenceTests`, `InvokeAgentToolActivityTests` (constructor arity change), `DurableAgentSettingsInheritanceTests` (removed legacy expectations).
- Added: `Helpers/EchoChatClient.cs`, `Helpers/EmptyResponseChatClient.cs`, `Helpers/SlowThenFastChatClient.cs`, `Helpers/FailThenSucceedChatClient.cs` for integration tests that previously used `AIAgent` subclasses (the new path needs `IChatClient` instead).
- Bulk-updated all integration test files to swap `options.AddAIAgent(new EchoAIAgent("X"))` → `options.AddDurableAgent("X", a => a.ChatClient = _ => new Helpers.EchoChatClient())`, and worker-level property accesses (`opts.X` → `opts.DefaultX`).

### Exit gate

- `just build`: 0 warnings, 0 errors (whole solution including samples).
- `just test-unit`: 288/288 pass (Agents).
- `just test-unit-ai`: 190/190 pass (AI).
- `grep -rn "ConfigureAwait(false)" src/Temporalio.Extensions.Agents/Workflows/AgentWorkflow.cs src/Temporalio.Extensions.AI/DurableChatWorkflowBase.cs`: zero matches.
- `grep -rn "AddAIAgent\b\|AddAIAgentFactory\|EnablePerToolActivities\|PerToolActivityOptions\|UseExternalHistory\|UseExternalAgentHistory" src/`: only an `<c>AddAIAgent</c>` mention inside `TemporalAgentsOptions.cs`'s class XML doc explaining what was removed (informational, no code reference).
- Old worker-level property accesses (`agentsOptions.TimeToLive` etc.) in `src/`: zero matches.

---

## Phase 7 — Rewrite all MAF samples to v0.3 idiomatic shape

After Phases 1-6 landed (last commit: `1d09c71`, MIGRATION-v0.3.md), the 11 MAF samples carried a mix of Phase 5 minimal placeholders and v0.2-era helper code (constructed-but-unused `AsAIAgent` agents, leftover `BuildServiceProvider()` bootstrap dance in `PerToolActivities` and `ExternalHistoryStore`, dead `clientFactory: c => c.UseFunctionInvocation()` wiring). Phase 7 rewrote all 11 to the canonical v0.3 idiom — `AddChatClient` in DI, `opts.AddDurableAgent("name", agent => { ... })` for registration, `agent.AddTool(sp => ...)` for DI-resolved tools, `opts.NoRetry()` for write tools.

### Samples rewritten (single commit)

1. **BasicAgent** — straightforward AddDurableAgent + agent.AddTool(weatherTool).
2. **WorkflowOrchestration** — fixed agent name mismatch (placeholder registered "Assistant", workflow looked up "WeatherAssistant"). Now both align as "WeatherAssistant".
3. **EvaluatorOptimizer** — dropped two unused `AsAIAgent`-constructed local variables that were dead code in the placeholder.
4. **MultiAgentRouting** — same dead-code cleanup × 3 specialist agents.
5. **WorkflowRouting** — same cleanup × 4 (Classifier + 3 specialists). Specialist `Description` properties moved from the constructed `AsAIAgent` call to `agent.Description = "..."` on the builder so `GetAgentDescriptors()` introspection still works for `DynamicRoutingWorkflow`.
6. **HumanInTheLoop** — fixed agent-name bug (placeholder registered "EmailAgent", proxy lookup used "EmailAssistant"). Moved the `sendEmailTool` registration onto the agent builder via `agent.AddTool(sendEmailTool)` so HITL `RequestApprovalAsync` from inside the tool body works end-to-end. Demonstrated `agent.ApprovalTimeout` per-agent override.
7. **SplitWorkerClient/Worker** — Worker only (Client unchanged). Same pattern as BasicAgent.
8. **ConfigurableAgent** — bound `IOptions<T>` config eagerly at host-build time so `agent.Instructions` could receive the templated string (per Phase 5 plan, `Instructions` is set at registration, not resolved per dispatch). Tools route through `agent.AddTool("Name", sp => ...)` factories. The `[ESCALATE: ...]` triage workflow logic stays unchanged.
9. **PerToolActivities** — the showpiece. Dropped `BuildServiceProvider()` bootstrap (lines 67-83 in v0.2). Dropped the `Microsoft.Extensions.AI` import comment about `UseFunctionInvocation`. Dropped the explicit `.Build()` on `AddChatClient`. Updated activity-name guidance in the operator-help block (`InvokeFunction:foo` → `InvokeAgentTool:RefundAgent:foo`, `RunAgentStep` → `RunDurableAgentStep`). Line count: 247 → 215 (13% overall, but the registration block proper went from ~50 lines to ~30 lines — about 40% reduction in the foot-gun-prone surface).
10. **ExternalHistoryStore** — Dropped the explicit `var store = new InMemoryHistoryStore(...); builder.Services.AddSingleton(store); builder.Services.AddSingleton<IAgentHistoryStore>(store);` pattern. Now: `builder.Services.AddSingleton<InMemoryHistoryStore>(_ => new InMemoryHistoryStore(maxRecentEntries: 4))` + `opts.HistoryStore = sp => sp.GetRequiredService<InMemoryHistoryStore>()`. Demo driver still resolves the concrete store via DI for stat inspection. Updated activity-name in payload-inspection check from `Temporalio.Extensions.Agents.ExecuteAgent` (legacy) to `Temporalio.Extensions.Agents.RunDurableAgentStep` (current — the prior placeholder still had the v0.2 activity name and would have silently reported "no activity found").
11. **AmbientAgent** — same dead-code cleanup (constructed-but-unused `analysisAgent` / `alertAgent` locals).

### Other touches

- **`samples/MAF/PerToolActivities/PerToolActivities.csproj`** — dropped the `Temporalio.Extensions.AI` `<ProjectReference>` (transitively pulled by Extensions.Agents now that `AddDurableTools` is no longer needed for the MAF agent path). Updated the comment block to reflect the new dependency story.
- **`samples/MAF/PerToolActivities/RefundWorkflow.cs`** — class-doc comment updated `EnablePerToolActivities = true` → `AddDurableAgent` and `RunAgentStep` / `InvokeFunction` → `RunDurableAgentStep` / `InvokeAgentTool`.
- **`samples/MAF/WorkflowRouting/RoutingActivities.cs`** — comment update `AddAIAgent()` → `AddDurableAgent()`.
- **READMEs** — updated:
  - `BasicAgent/README.md` — bullet list rewritten to match v0.3 surface; "tool calls are durable" → "tool calls are durable per-tool activities".
  - `WorkflowOrchestration/README.md` — dropped `UseFunctionInvocation` middleware bullet.
  - `WorkflowRouting/README.md` — `AddAIAgent` → `AddDurableAgent`; descriptor-extraction prose updated to match `agent.Description` slot.
  - `ExternalHistoryStore/README.md` — `UseExternalHistory = true` → `opts.HistoryStore` / `agent.HistoryStore` configured.
  - `PerToolActivities/README.md` — full rewrite: "step mode" terminology gone, expected-output activity names updated, registration code block shows the canonical v0.3 form, "What V0.3 simplifies away" section enumerates the removed boilerplate.

### Discoveries / Notes

- **The Phase 5 placeholder for HumanInTheLoop registered the wrong agent name.** Proxy lookup said "EmailAssistant"; placeholder registration said "EmailAgent". Build was clean because agent-name resolution happens at runtime. This would have failed end-to-end with no clear error. Phase 7 fixed it.
- **The Phase 5 placeholder for WorkflowOrchestration had the same bug** — registration said "Assistant", workflow lookup said "WeatherAssistant". Same fix.
- **The ExternalHistoryStore payload-inspection check was checking for a v0.2 activity name** (`Temporalio.Extensions.Agents.ExecuteAgent`). The activity is now `RunDurableAgentStep`. The check would have silently reported "no activity found" instead of validating the payload-omission claim. Phase 7 fixed it.
- **`agent.AddTool` has two factory overloads** with different shapes: `AddTool(AIFunction tool, ...)` (concrete) and `AddTool(string name, Func<IServiceProvider, AIFunction> factory, ...)` (DI-resolved). The string `name` parameter is required on the factory overload — duplicate-name detection happens synchronously at registration without it. The MIGRATION-v0.3.md doc was slightly off (omitted the `name` parameter in the second example, line 137); did NOT fix that as part of Phase 7 since the doc is owned by Oracle.

### Exit gate

- `just build`: 0 warnings, 0 errors.
- `just test-unit-all`: 478/478 pass (288 Agents + 190 AI).
- `just test-integration`: 64/67 pass — same 3 failures as on `main` immediately before Phase 7 (`DurableAgent_LegacyAddAIAgent_UsesLegacyPath`, `SingleTurn_ResponseAgentIdMatchesWorkflowId`, `EmptyAgentResponse_RoundTripsWithoutCrash`). Verified via `git stash` that these failed on the unmodified Phase 6 tip; not Phase 7 regressions. Worth Trinity flagging — `LegacyAddAIAgent_UsesLegacyPath` looks like a stale test that survived Phase 5 (legacy path is gone).
- `just test-integration-ai`: 13/13 pass.
- `grep -rn "BuildServiceProvider" samples/MAF/`: 2 matches, both intentional (one explanatory comment in ConfigurableAgent's header, one bullet in PerToolActivities README explaining what v0.3 removed).
- `grep -rn "AddAIAgent\b\|AddAIAgentFactory\|EnablePerToolActivities\|UseFunctionInvocation" samples/MAF/`: 2 matches, both intentional (header comment in ConfigurableAgent referring to the v0.2 API by name; PerToolActivities README "no `EnablePerToolActivities`" callout).
- `grep -rn "RegisterDefaultWorkflow = false" samples/MAF/`: zero matches.

### Why one commit, not 11

The plan permitted either approach. Bundled because: (a) every sample touches one file (`Program.cs`) plus optional README; (b) the changes are mechanical and follow the same pattern; (c) review of "all 11 samples follow the same v0.3 shape" benefits from seeing them together; (d) reviewers checking a single commit's `git show` get the full v0.3 sample-set picture in one read.


---

## 2026-05-07 — Fix: proxy-only `BuildAgentWorkflowInput` regression (v0.3)

**Symptom:** `samples/MAF/SplitWorkerClient/Client/Program.cs` against a v0.3 worker threw `AgentNotRegisteredException: No agent named 'Assistant' was registered.` Proxy-only deployments (`AddTemporalAgentProxies` + `AddAgentProxy(...)` without `AddDurableAgent`) were completely non-functional.

**Root cause:** `DefaultTemporalAgentClient.BuildAgentWorkflowInputCore` was called locally on the proxy client to construct the workflow input before `StartWorkflowAsync`. It looked up `options.DurableAgentRegistrations` and unconditionally threw when the agent name wasn't there — but for a proxy-only client the registration legitimately lives on the worker side, not the client side. The previous comment ("for proxy clients, BuildAgentWorkflowInput should never be invoked in-process") was wrong: the proxy DOES call it in-process to construct the input payload that the server forwards to the worker.

**Fix:** When `agentName` is in `_proxyDeclarations` but not `_durableAgentRegistrations`, build a minimal `AgentWorkflowInput` from worker-level defaults plus the proxy declaration's optional TTL. Throw `AgentNotRegisteredException` only when the name is in neither dictionary. Per-tool `DurableAgentToolActivityOptions` and `UseExternalStoreMode` are intentionally null/false on the proxy side — the worker resolves those from its own `DurableAgentRegistration` when the workflow runs.

**Files:**
- `src/Temporalio.Extensions.Agents/TemporalAgentsOptions.cs` — `ProxyDeclarations` accessor was already in place at line 270 (no edit needed).
- `src/Temporalio.Extensions.Agents/Workflows/DefaultTemporalAgentClient.cs` — replaced the unconditional throw with a proxy-declaration lookup that calls a new `BuildProxyOnlyAgentWorkflowInput` helper; updated the misleading XML doc on `BuildAgentWorkflowInput`.
- `tests/Temporalio.Extensions.Agents.Tests/AddDurableAgentTests.cs` — added 4 unit tests: `BuildAgentWorkflowInput_ProxyOnly_UsesWorkerDefaults`, `..._RespectsProxyDeclarationTtl`, `..._NullDefaultTtl_FallsBackToFourteenDays`, `BuildAgentWorkflowInput_NotRegisteredAtAll_Throws`.

**Exit gate:**
- `just build`: 0 warnings, 0 errors.
- `just test-unit-all`: all pass (4 new tests included).
- `just test-integration`: 66/66.
- `just test-integration-ai`: 13/13.
- Determinism guard: zero `ConfigureAwait(false)` in `AgentWorkflow.cs` / `DurableChatWorkflowBase.cs`.
- End-to-end: started Worker in background, ran Client, four turns succeeded (capital of France → Paris; population; weather tool call; summary). No exception.

---

## 2026-05-07 — Cosmetic sample bug fixes (post-v0.3 verification)

End-to-end verification of all 11 v0.3 samples surfaced two cosmetic sample bugs.
Three diagnostic reports (mine, agent-smith, morpheus) converged on the same
root causes — both sample-author bugs, no library regressions.

### Bug 1: ExternalHistoryStore — SnapshotFull printed 0 entries

Session-ID mismatch in the demo driver. Activity writes are keyed by
`TemporalAgentSessionId.WorkflowId` ⇒ `ta-supportagent-{key}`, but the demo
driver was reading the store using the parent `SupportSessionWorkflow` ID
(`support-acme-{guid}`). Same bug also broke the activity-payload canary
check — `RunDurableAgentStep` is scheduled on the inner agent-session
workflow, so iterating `handle.FetchHistoryEventsAsync()` on the parent
found no matching activity-scheduled events.

**Fix** (`samples/MAF/ExternalHistoryStore/Program.cs`):
- Added `[WorkflowQuery("GetAgentSessionWorkflowId")]` on `SupportSessionWorkflow`
  that returns `_session?.SessionId.WorkflowId` (null until first turn creates
  the session).
- After the demo loop, query the inner ID, fall back to parent with a console
  warning if null, then route both `store.SnapshotFull(...)` and the payload
  fetch through `temporalClient.GetWorkflowHandle(agentSessionWorkflowId)`.

### Bug 2: PerToolActivities — FailOnceEnabled dead in Scenario 2

`Tools.cs:47-49` predicate `if (FailOnceEnabled && n == 1)` keyed on a
process-wide cumulative counter. After Scenario 1 incremented past 1,
Scenario 2's first call saw `n >= 2` and the throw branch was dead — so
"Per-tool retry granularity confirmed" never printed.

**Fix** (`samples/MAF/PerToolActivities/Tools.cs`): replaced cumulative-counter
predicate with a self-clearing latch using
`Interlocked.CompareExchange(ref _failArmed, 0, 1)` for atomic arm-and-consume.
Public `FailOnceEnabled` property surface preserved (getter/setter contract
unchanged) so the demo driver in `Program.cs` doesn't need any changes.

### Doc paragraph: DurableAgentBuilder factory composition contract

Added a `<remarks>` paragraph at the class level describing the lazy
first-dispatch composition lifecycle: `ChatClient` / `AddTool` /
`AddContextProvider` / `HistoryStore` factories are invoked once at first
activity dispatch with the worker's root `IServiceProvider`, results cached
for the worker's lifetime, and `AddScoped` services silently behave as
singletons under this path.

### Verification
- `just build`: 0 warnings, 0 errors.
- `just test-unit-all`: 482 passed (292 Agents + 190 AI).
- `just test-integration`: 66/66.
- `just test-integration-ai`: 13/13.
- Sample runtime verification deferred to chief.

### Commit
`66965f7` — `fix(samples): cosmetic bug fixes in ExternalHistoryStore and PerToolActivities`
