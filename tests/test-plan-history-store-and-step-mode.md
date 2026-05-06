# Test Plan: External History Store + Per-Tool Activities (Step Mode)

**Plan source**: `/Users/cecilphillip/.claude/plans/src-temporalio-extensions-ai-readme-md-vivid-meteor.md`
**Author**: Trinity (Test Engineer)
**Status**: Strategy + scaffolding (tests follow Tank's implementation)

This document is the test strategy for the two features under construction by Tank:

1. **Feature 1** — External history store via `IAgentHistoryStore` (gated by `TemporalAgentsOptions.UseExternalHistory`).
2. **Feature 2** — Per-tool Temporal activities via step-mode workflow loop (gated by `TemporalAgentsOptions.EnablePerToolActivities`).

Tests are split between the existing test projects:

| Project | Type | When |
|---|---|---|
| `Temporalio.Extensions.Agents.Tests` | xUnit unit tests, no embedded server | Default; use for type-level, validation, and capture assertions |
| `Temporalio.Extensions.Agents.IntegrationTests` | xUnit integration tests, `WorkflowEnvironment.StartLocalAsync()` | When the assertion needs real workflow scheduling, replay, or worker restarts |

---

## Conventions

- xUnit `Fact` / `Theory`. **Hand-written stubs only** (no FakeItEasy / Moq for new code) per project convention (`StubAIAgent`, `TestChatClient`).
- `Assert.Throws<T>` requires the **exact** exception type — `ArgumentNullException` for nulls, `InvalidOperationException` for missing config.
- Integration tests use `TestEnvironmentHelper.StartLocalAsync()` only when `EnableSearchAttributes = true`. Otherwise plain `WorkflowEnvironment.StartLocalAsync()`.
- Test method naming follows the existing pattern: `MethodOrScenario_Condition_Expected`.

---

## Scaffolding (delivered alongside this strategy)

These four helpers live under the `Tests` project and have no dependency on Tank's in-progress source. They are written so that Tank's tests can plug in immediately when his PR lands.

| File | Purpose |
|---|---|
| `tests/Temporalio.Extensions.Agents.Tests/HistoryStore/FakeAgentHistoryStore.cs` | Concurrent-safe in-memory test double for `IAgentHistoryStore`. Records every call (`Load` / `Append` / `Replace`), supports failure injection and pre-op hooks for ordering tests. |
| `tests/Temporalio.Extensions.Agents.Tests/HistoryStore/CapturingPayloadConverter.cs` | Wraps the production `DefaultPayloadConverter(AIJsonUtilities.DefaultOptions)` and records every `ToPayload` / `ToValue` call. Lets tests assert "the activity-scheduled event would not contain `ConversationHistory`" by inspecting the captured `ExecuteAgentInput` instance directly — no live server round-trip required. |
| `tests/Temporalio.Extensions.Agents.Tests/StepMode/RecordingTool.cs` | Records every tool call (count, arguments, timestamp). Behavior is configurable: `AlwaysSucceed`, `AlwaysFail`, `FailOnceThenSucceed`. Drives retry-policy tests and crash-safety counters. |
| `tests/Temporalio.Extensions.Agents.Tests/StepMode/ScriptedChatClient.cs` | `IChatClient` that returns a pre-defined sequence of `ChatResponse` values, including responses that carry `FunctionCallContent`. Drives the step-mode loop deterministically. Has a `WithToolCallsThenFinal` factory for the canonical two-turn pattern. |

When Tank's `IAgentHistoryStore` interface lands, the `FakeAgentHistoryStore` only needs `: IAgentHistoryStore` added on the class declaration — its public surface already matches the planned interface (`LoadAsync` / `AppendAsync` / `ReplaceAsync`).

---

## Feature 1 — External History Store

### Test matrix

| # | Test | Type | Test class | Test method |
|---|---|---|---|---|
| 1.1 | Default behavior is unchanged: `UseExternalHistory = false` (the default) produces the same `ExecuteAgentInput` shape as today, with `ConversationHistory` populated. | Unit | `ExternalHistoryStoreTests` | `UseExternalHistory_False_LeavesConversationHistoryPopulated` |
| 1.2 | With store enabled and an empty store, turn 1 calls `LoadAsync` first, then `AppendAsync` exactly once with `[requestEntry, responseEntry]`. | Integration | `ExternalHistoryStoreIntegrationTests` | `FirstTurn_WithEmptyStore_LoadsThenAppendsRequestAndResponse` |
| 1.3 | Turn 2 reconstructs history from `LoadAsync` (which now returns turn-1's entries), then appends turn-2's entries. Assert append ordering: turn-1 entries appear before turn-2 entries in the recorded calls. | Integration | `ExternalHistoryStoreIntegrationTests` | `SecondTurn_WithSeededStore_ReconstructsHistoryAndAppends` |
| 1.4 | When `UseExternalStore = true`, the `ExecuteAgentInput` payload sent to the activity has `ConversationHistory == null`. Assert via `CapturingPayloadConverter.OfInputType<ExecuteAgentInput>()`. | Unit | `ExternalHistoryStoreTests` | `ExecuteAgentInput_WithExternalStore_OmitsConversationHistory` |
| 1.5 | Continue-as-new with store enabled sets `CarriedHistory = null` on the new run input. | Integration | `ExternalHistoryStoreIntegrationTests` | `ContinueAsNew_WithExternalStore_OmitsCarriedHistory` |
| 1.6 | Continue-as-new with `HistoryReducer` configured + store enabled invokes `ReduceHistoryInStoreAsync` (load → reduce → `ReplaceAsync`) **before** the CAN throw. Assert `Replace` precedes the next workflow run's `Load`. | Integration | `ExternalHistoryStoreIntegrationTests` | `ContinueAsNew_WithReducer_DispatchesReduceBeforeCAN` |
| 1.7 | Startup: `UseExternalHistory = true` without a registered `IAgentHistoryStore` throws a clear `InvalidOperationException` from the registrar. | Unit | `TemporalAgentsRegistrarValidationTests` | `Validate_ExternalHistoryEnabled_NoStoreRegistered_Throws` |
| 1.8 | Migration: a workflow started before the upgrade has `AgentWorkflowInput.UseExternalStore = false`. After deployment with `UseExternalHistory = true` in options, the running workflow continues to use in-memory history (its `Input.UseExternalStore` flag still says `false`). | Integration | `ExternalHistoryStoreIntegrationTests` | `InFlightWorkflow_PreUpgrade_KeepsInMemoryHistoryAfterDeploymentToggle` |
| 1.9 | Concurrent turns on the same session call `AppendAsync` in turn order — the `RunTurnAsync` mutex serializes them. Use `FakeAgentHistoryStore.PreOperationHook` to delay turn-1's append, dispatch turn-2 concurrently, assert turn-2's append timestamp is strictly after turn-1's. | Integration | `ExternalHistoryStoreIntegrationTests` | `ConcurrentTurns_AppendInOrder` |
| 1.10 | `GetHistoryAsync` query under external store returns metadata-only entries (empty `Messages`). | Integration | `ExternalHistoryStoreIntegrationTests` | `GetHistoryQuery_WithExternalStore_ReturnsMetadataOnly` |
| 1.11 | `FunctionCallContent` round-trips correctly through `TemporalAgentDataConverter` — risk-validation test from plan §"Risks and Validation Gates" item 1. | Unit | `ExternalHistoryStoreTests` | `DurableSessionEntry_WithFunctionCallContent_RoundTripsThroughDataConverter` |

### Test class locations

- New file: `tests/Temporalio.Extensions.Agents.Tests/ExternalHistoryStoreTests.cs` — for unit tests 1.1, 1.4, 1.7 (validation lives elsewhere — see below), 1.11.
- New file: `tests/Temporalio.Extensions.Agents.Tests/TemporalAgentsRegistrarValidationTests.cs` (or extend the existing `TemporalAgentsOptionsTests.cs`) — for 1.7. The existing `AgentWorkflowValidatorTests.cs` is a good model.
- New file: `tests/Temporalio.Extensions.Agents.IntegrationTests/ExternalHistoryStoreIntegrationTests.cs` — for 1.2, 1.3, 1.5, 1.6, 1.8, 1.9, 1.10.

### How to assert "ConversationHistory is null in the activity-scheduled event"

The plan's verification section calls for inspecting the activity-scheduled event payload directly. Two complementary techniques:

1. **Unit-level (preferred for fast feedback)** — wire `CapturingPayloadConverter` into the `DataConverter` passed to a unit-test client / worker. Since the workflow's call to `Workflow.ExecuteActivityAsync(...)` ultimately serializes the input through this converter, every `ExecuteAgentInput` instance is captured as a CLR object before going to the wire. Assert:
   ```csharp
   var capturedInputs = capturing.OfInputType<ExecuteAgentInput>().ToList();
   Assert.All(capturedInputs, i => Assert.Null(i.ConversationHistory));
   ```
2. **Integration-level (one canary test)** — pull the workflow history via `WorkflowHandle.FetchHistoryAsync()` and verify the `ActivityTaskScheduledEventAttributes.Input` payload bytes don't contain `"ConversationHistory"`. Slow but proves the end-to-end claim.

### Notes for Tank

- The `ShouldStripMessagesFromHistoryEntry()` virtual hook on `DurableChatWorkflowBase` referenced in the plan needs `StripMessagesFromEntry(...)` and `StripMessagesFromResponse(...)` helpers. The current WIP at `src/Temporalio.Extensions.AI/DurableChatWorkflowBase.cs:256,275` references these names but they aren't defined yet — they need to land before the test project will compile.
- Tests 1.5 and 1.6 require a way to observe the new run input from the prior run. The standard Temporal pattern is to read it back via `WorkflowHandle.GetResultAsync()` after the workflow signals completion, or via a workflow query that reflects the current input.

---

## Feature 2 — Per-Tool Temporal Activities (Step Mode)

### Test matrix

| # | Test | Type | Test class | Test method |
|---|---|---|---|---|
| 2.1 | Default behavior unchanged: `EnablePerToolActivities = false` keeps the existing `ExecuteAgentAsync`-as-one-activity path; no `RunAgentStepAsync` activity is scheduled. | Integration | `StepModeIntegrationTests` | `EnablePerToolActivities_False_UsesSingleActivityPath` |
| 2.2 | With step mode + a successful tool: `RunAgentStepAsync` is invoked (≥ 2 times — one for the tool call, one for the final), and `InvokeFunctionAsync` is invoked exactly once for the single tool call. | Integration | `StepModeIntegrationTests` | `StepMode_SingleToolCall_InvokesStepThenToolThenStep` |
| 2.3 | Final `AgentResponse.Messages` includes the full multi-message history: assistant message with `FunctionCallContent`, the tool's `FunctionResultContent`, then the final assistant text. | Integration | `StepModeIntegrationTests` | `StepMode_FinalResponse_ContainsAllIntermediateMessages` |
| 2.4 | Parallel fan-out: scripted client returns three `FunctionCallContent` items in one turn → `Workflow.WhenAllAsync` schedules three `InvokeFunctionAsync` activities concurrently. Assert via Temporal history: three `ActivityTaskScheduled` events appear before the first `ActivityTaskCompleted`. | Integration | `StepModeIntegrationTests` | `StepMode_ThreeParallelToolCalls_AllScheduledBeforeAnyCompletes` |
| 2.5 | Per-tool retry: tool registered with `PerToolActivityOptions["t"] = { MaximumAttempts = 1 }` and `Behavior = AlwaysFail` is called exactly once (no retry); the workflow surfaces the failure. | Integration | `StepModeIntegrationTests` | `StepMode_WriteToolWithMaxAttemptsOne_DoesNotRetry` |
| 2.6 | Default retry policy: tool with `Behavior = FailOnceThenSucceed` is called exactly twice and the workflow completes successfully. | Integration | `StepModeIntegrationTests` | `StepMode_ReadToolWithDefaultPolicy_RetriesOnceAndSucceeds` |
| 2.7 | Crash safety: start a step-mode workflow, kill the worker mid-tool execution, restart with the same `RecordingTool` instance — assert `RecordingTool.CallCount == 1`. Uses `AtomicInt`-style counter (`Interlocked.Increment` inside the tool). | Integration | `StepModeIntegrationTests` | `StepMode_WorkerCrashMidTool_ToolInvokedExactlyOnce` |
| 2.8 | Loop iteration cap: scripted client returns `FunctionCallContent` indefinitely; after `MaxToolCallsPerTurn` iterations the loop exits with a structured error response (not a hang). | Integration | `StepModeIntegrationTests` | `StepMode_RunawayToolCalls_ExitsAtIterationCap` |
| 2.9 | Replay determinism: run a step-mode workflow to completion, then feed the recorded history through `WorkflowReplayer` — replay must produce byte-identical output. | Integration | `StepModeIntegrationTests` | `StepMode_RecordedHistory_ReplaysDeterministically` |
| 2.10 | Step-mode requires `AddDurableAI`: enabling `EnablePerToolActivities = true` without `DurableFunctionActivities` registered throws `InvalidOperationException` at registrar / startup. | Unit | `TemporalAgentsRegistrarValidationTests` | `Validate_PerToolEnabled_NoDurableFunctionActivities_Throws` |
| 2.11 | `RunAgentStepAsync` does not execute tool calls — calls `IChatClient.GetResponseAsync` without `FunctionInvokingChatClient` and returns `FunctionCallContent` items in `AgentStepResult.ToolCalls`. Risk-validation test from plan §"Risks and Validation Gates" item 3. | Unit | `RunAgentStepActivityTests` | `RunAgentStep_LLMReturnsToolCalls_ReturnsThemUnexecuted` |
| 2.12 | `FunctionCallContent` and `FunctionResultContent` round-trip through `TemporalAgentDataConverter` — risk-validation test from plan §"Risks and Validation Gates" item 1. (Shares fixtures with 1.11 but covers result content.) | Unit | `RunAgentStepActivityTests` | `AgentStepResult_WithFunctionCallContent_RoundTripsThroughDataConverter` |

### Test class locations

- New file: `tests/Temporalio.Extensions.Agents.Tests/RunAgentStepActivityTests.cs` — for 2.11, 2.12.
- Validation test 2.10 lives in the same `TemporalAgentsRegistrarValidationTests.cs` file from Feature 1.
- New file: `tests/Temporalio.Extensions.Agents.IntegrationTests/StepModeIntegrationTests.cs` — for 2.1, 2.2, 2.3, 2.4, 2.5, 2.6, 2.7, 2.8, 2.9.

### How to assert parallel fan-out (test 2.4)

Three approaches, in order of preference:

1. **History inspection** — use `WorkflowHandle.FetchHistoryAsync()` and walk events. For the "all scheduled before any completes" claim, find the indices of `ActivityTaskScheduled` events for `Temporalio.Extensions.AI.InvokeFunction` and the first `ActivityTaskCompleted` event of the same type — assert all three schedule events have indices < the first complete event.
2. **Tool-side timing** — `RecordingTool.RecordedInvocation` carries `StartedAt`. With three `RecordingTool` instances (each with a small `await Task.Yield()` inside the body), assert all three `StartedAt` values are within a tight window. Less rigorous than (1) but useful as a sanity check.
3. **Replay** — combine with test 2.9.

### How to crash-test the worker (test 2.7)

Existing pattern: `tests/Temporalio.Extensions.Agents.IntegrationTests/ResilienceTests.cs` already starts and kills workers. The recipe:

1. Configure the `RecordingTool` with a delay (e.g., `Task.Delay(TimeSpan.FromSeconds(2))` inside the body) so we have time to crash.
2. Start the workflow and wait until `RecordingTool.CallCount == 1`.
3. Cancel the worker's host token / dispose the worker.
4. Restart a fresh worker registered against the same `RecordingTool` instance and `WorkflowEnvironment`.
5. Wait for the workflow to complete.
6. Assert `RecordingTool.CallCount == 1` (exactly one — not two).

This validates that the crash happened during activity execution, the `ActivityTaskCompleted` event was already written, and the replay path skipped re-invocation. Note: the crash window is small; if `CallCount == 1` is hit too late (after the activity already completed), the test still passes meaningfully. If CallCount can't be observed = 1 in time, increase the tool delay.

---

## Cross-cutting validation gates (from plan §Risks)

These three risk-validation tests block implementation and should run as part of the unit-test pre-flight. They live in their feature-specific test classes but are tagged here for visibility.

| Plan risk | Test | Where |
|---|---|---|
| 1: `FunctionCallContent` round-trips through `TemporalAgentDataConverter` | 1.11, 2.12 | `ExternalHistoryStoreTests`, `RunAgentStepActivityTests` |
| 2: `ChatClientAgent` exposes instructions / options | Manual code-read by Tank during impl. Cover with integration test 2.2 (which runs through the full agent factory and asserts the system prompt reaches the LLM in `ScriptedChatClient.Calls[0].Messages`). | `StepModeIntegrationTests` |
| 3: `FunctionInvokingChatClient` absence works as expected | 2.11 | `RunAgentStepActivityTests` |
| 4: `DurableFunctionActivities` accessible from Agents library | Asserted indirectly by every step-mode integration test (they all schedule `InvokeFunction`). 2.10 covers the negative case (missing registration throws). | All step-mode tests |

---

## ConfigureAwait verification

Per plan §"ConfigureAwait(false) — current state and action required", a grep gate:

```bash
grep -rn "ConfigureAwait(false)" \
  src/Temporalio.Extensions.Agents/Workflows/AgentWorkflow.cs \
  src/Temporalio.Extensions.AI/DurableChatWorkflowBase.cs
```

Must return zero results. This is a `just` recipe candidate (`just verify-workflow-configureawait`) but at minimum should be in the PR review checklist for both features.

---

## Summary for Tank

What I've shipped now:

- This document.
- Four scaffolding files in `tests/Temporalio.Extensions.Agents.Tests/HistoryStore/` and `tests/Temporalio.Extensions.Agents.Tests/StepMode/` (compile-clean against unmodified `main`; verified via stash + build).

What I'm waiting on:

- Tank's `IAgentHistoryStore` interface — once it lands, `FakeAgentHistoryStore` adopts it with a one-line edit.
- Tank's `StripMessagesFromEntry` / `StripMessagesFromResponse` helpers in `DurableChatWorkflowBase.cs` — currently the WIP references them by name but the methods don't exist, causing CS0103 build failures.
- Tank's `AgentStepInput` / `AgentStepResult` / `RunAgentStepAsync` activity for tests 2.11 and 2.12.
- Tank's `EnablePerToolActivities` and `UseExternalHistory` options + registrar validation for tests 1.7 and 2.10.

When Tank's PR lands, the test classes named in the matrices above slot in alongside the scaffolding with no further plumbing.
