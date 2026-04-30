# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

---

## [Unreleased]

### Added

- **`Temporalio.Extensions.AI` per-turn observability.**
  `DurableChatSessionClient.GetHistoryAsync()` now returns
  `IReadOnlyList<DurableSessionEntry>` instead of `IReadOnlyList<ChatMessage>`.
  Entries carry per-turn `CorrelationId`, `CreatedAt`, and (on responses)
  `UsageDetails`, enabling queries like "show me the token usage for turn N"
  directly against workflow state — no external telemetry pipeline required.

- **Optional `correlationId` parameter on `DurableChatSessionClient.ChatAsync`.**
  Callers can supply their own correlation ID for cross-system log/trace
  threading. When omitted, the workflow auto-generates one via
  `Workflow.NewGuid()`. The same value is stamped on the request and response
  entries, so it is recoverable later via `GetHistoryAsync`.

- **`DurableSessionResponse.Text` convenience accessor.** Returns the last
  assistant message's text (or empty string if none). Replaces
  `ChatResponse.Text` for the common `response.Text` pattern at user call
  sites; `[JsonIgnore]` so it does not appear on the wire.

### Changed (BREAKING)

- **`Temporalio.Extensions.Agents` workflow history wire format.** Conversation
  history entries serialized by prior versions are not deserializable by this
  version. `TemporalAgentStateEntry.Messages` changed from a TA-custom
  `TemporalAgentStateMessage[]` shape to MEAI's `ChatMessage[]`. The
  `TemporalAgentStateMessage` and `TemporalAgentStateContent` hierarchies
  (13 types in total) are removed; `Microsoft.Extensions.AI.ChatMessage` and
  the `AIContent` subtypes are used directly. Polymorphism is preserved
  end-to-end through `DurableAIDataConverter` (which is auto-wired by both
  `AddTemporalAgents()` and `AddWorkerPlugin(new TemporalAgentsPlugin(...))`).
  New MEAI content types are now picked up automatically — no per-type
  wrapper to add.

  **Migration:** drain in-flight workflows before upgrading. Stop new
  workflow starts on the prior version, wait for in-flight workflows to
  complete (or use `ContinueAsNew` to roll history), then deploy the new
  version. No dual-reader compatibility shim is provided; the library is
  in preview.

- **`Temporalio.Extensions.AI` workflow history wire format.**
  `DurableChatWorkflow` history entries now serialize as `DurableSessionEntry`
  (with `ai_request` / `ai_response` polymorphic discriminators on the
  `$type` property) instead of flat `ChatMessage`. In-flight workflows from
  prior versions are not deserializable by this version.

  **Migration:** drain in-flight workflows before upgrading; no dual-reader
  compatibility shim is provided.

- **`DurableChatSessionClient.ChatAsync` return type.** Changed from
  `Task<ChatResponse>` to `Task<DurableSessionResponse>`. The new return type
  carries the per-turn metadata (`Usage`, `CorrelationId`, `CreatedAt`)
  directly. Use `response.Text` (now a property on `DurableSessionResponse`)
  for the common `response.Text` pattern. To reconstruct a `ChatResponse`
  for downstream code that requires it:
  `var chatResponse = new ChatResponse(response.Messages.ToList());`.

- **`DurableChatSessionClient.GetHistoryAsync()` return type.** Changed from
  `IReadOnlyList<ChatMessage>` to `IReadOnlyList<DurableSessionEntry>`.

  **Migration:** to flatten back to a `ChatMessage` log, use
  `entries.SelectMany(e => e.Messages)`. Pattern-match on
  `DurableSessionResponse` to access per-turn `Usage`.

- **`DurableChatWorkflowBase<TOutput>` virtual hooks.** Replaced
  `GetHistoryMessages` with `BuildResponseEntry` (and an optional
  `BuildRequestEntry`). Custom subclasses must update their overrides to
  produce a `DurableSessionResponse` from their output type rather than
  emitting a `ChatMessage` sequence.

- **`DurableExecutionOptions.MaxHistorySize` renamed to `MaxEntryCount`.**
  Equivalent fields on workflow inputs renamed to match. Default (1000)
  unchanged. **Note for MEAI users:** the unit shifted from "1000 messages"
  to "1000 entries", and each turn now produces two entries (one
  `DurableSessionRequest` and one `DurableSessionResponse`). At the same
  numeric value, `MaxEntryCount` retains roughly half the turn count
  `MaxHistorySize` did — recheck the threshold if you previously tuned it
  to control workflow lifetime.

- **`DurableExecutionOptions.HistoryReducer` shape changed** from
  `IChatReducer?` to
  `Func<IList<DurableSessionEntry>, IList<DurableSessionEntry>>?`. Reducers
  now operate on entries (not flat messages), preserving per-turn `Usage`
  and `CorrelationId` metadata across `ContinueAsNew` boundaries. The
  reducer must be synchronous and deterministic (it runs in workflow
  context). Existing `IChatReducer` implementations must migrate to the
  entry-shaped delegate; in-pipeline reducers passed to
  `ChatClientBuilder.UseChatReducer(...)` are unaffected.

### Fixed

- **`ChatMessage.AdditionalProperties` now round-trips through agent
  conversation history.** Prior versions silently dropped this field
  during the `TemporalAgentStateMessage.FromChatMessage` conversion. With
  direct `ChatMessage` storage, the field is preserved end-to-end and is
  visible in `GetHistory()` query results.

---

## [0.1.4] - 2026-03-13 

### Added

- **`StructuredOutputExtensions.RunAsync<T>`** — typed structured output deserialization for
  `TemporalAIAgent`, `AIAgent`, and `ITemporalAgentClient`. Strips markdown code fences before
  JSON parsing and retries with LLM error context when deserialization fails.

- **`MarkdownCodeFenceHelper`** — internal utility that strips markdown code fences (`` ```json ... ``` ``)
  and extracts the first balanced JSON object or array from LLM output. Handles nested braces,
  string escaping, and escaped quotes. 

- **`StructuredOutputOptions`** — configurable retry count (`MaxRetries`, default: 2), error
  context inclusion (`IncludeErrorContext`, default: true), and custom `JsonSerializerOptions`
  for structured output deserialization.

- **`[WorkflowUpdateValidator]` methods** on `AgentWorkflow` — `ValidateRunAgent`,
  `ValidateRequestApproval`, and `ValidateSubmitApproval` reject malformed updates before they
  enter workflow event history, preventing wasted activity executions and polluted event logs.

- **Temporal search attributes** — `AgentWorkflow` now upserts `AgentName` (keyword),
  `SessionCreatedAt` (datetime), and `TurnCount` (long) search attributes. Enables operational
  queries like `AgentName = "Billing" AND TurnCount > 10` in the Temporal UI.

- **`ITemporalAgentClient.RunAgentAsync(string agentName, string message)`** — convenience
  overload that resolves an agent by name and sends a single text message with an auto-generated
  session, simplifying the most common calling pattern.

### Changed

- **`AgentActivities` lazy factory caching** — agent factories are now invoked once per agent
  name and cached in a `ConcurrentDictionary`. Concurrent activity executions for the same agent
  share a single resolved `AIAgent` instance instead of calling the factory repeatedly.

- **Heartbeating on non-streaming path** — `AgentActivities` now heartbeats on every streamed
  chunk even when no `IAgentResponseHandler` is registered. Previously, the default non-streaming
  path could hit the heartbeat timeout during long LLM calls.

### Fixed

- **`SubmitApprovalAsync` validation moved to validator** — the pending-approval and request-ID
  mismatch checks that previously ran inside the update handler (after entering history) now run
  in `ValidateSubmitApproval` (before entering history), preventing invalid decisions from being
  recorded in the workflow event log.

---

## [0.1.0] - 2026-02-28

### Added

#### Scheduling infrastructure

- **`AgentJobWorkflow`** — a new internal Temporal workflow for scheduled and deferred agent
  runs. Unlike `AgentWorkflow`, it carries no conversation history, no `StateBag`, no TTL loop,
  and no `[WorkflowUpdate]` handlers. It executes a single `AgentActivities.ExecuteAgentAsync`
  call and exits. Workflow ID convention: `ta-{agentName}-scheduled-{scheduleId}`.

- **`AgentJobInput`** — internal input record for `AgentJobWorkflow`. Carries `AgentName`,
  `TaskQueue`, `Request`, and optional `ActivityStartToCloseTimeout` / `ActivityHeartbeatTimeout`
  overrides.

#### Recurring Temporal Schedules (`ITemporalAgentClient`)

- **`ITemporalAgentClient.ScheduleAgentAsync`** — creates a Temporal Schedule that fires
  `AgentJobWorkflow` on a caller-supplied `ScheduleSpec`. 

- **`ITemporalAgentClient.GetAgentScheduleHandle`** — retrieves an existing `ScheduleHandle`
  by schedule ID for out-of-band lifecycle operations (e.g., decommissioning an agent's
  schedule without restarting the worker).

#### Config-time Schedule Registration (`TemporalAgentsOptions`)

- **`TemporalAgentsOptions.AddScheduledAgentRun`** — declares a recurring scheduled run at
  configuration time. Runs are registered with Temporal automatically on worker startup via a
  new `ScheduleRegistrationService` hosted service. Startup is idempotent: if a schedule
  already exists (e.g., on subsequent worker restarts), a warning is logged and creation is
  skipped rather than overwriting the existing schedule.

- **`ScheduleRegistrationService`** — internal `IHostedService` that calls
  `ITemporalAgentClient.ScheduleAgentAsync` for every run declared via `AddScheduledAgentRun`.
  Catches `ScheduleAlreadyRunningException` and logs a warning instead of throwing.

#### Deferred One-Time Runs from Inside Workflows (`ScheduleActivities`)

- **`ScheduleActivities`** — new public activity class for use inside orchestrating workflows.
  Contains a single `[Activity]`-decorated method:

  - **`ScheduleOneTimeAgentRunAsync(OneTimeAgentRun run)`** — schedules a future, one-time
    `AgentJobWorkflow` run using `WorkflowOptions.StartDelay`. Uses
    `WorkflowIdConflictPolicy.UseExisting` for idempotency on activity retry. If `RunAt` is
    in the past when the activity executes, the run starts immediately (delay clamped to zero).

- **`OneTimeAgentRun`** — public record describing a deferred one-time run: `AgentName`,
  `RunId` (used to build the deterministic workflow ID), `Request`, and `RunAt`.

#### Deferred Session Start (`ITemporalAgentClient`)

- **`ITemporalAgentClient.RunAgentDelayedAsync`** — starts an agent session workflow with a
  `StartDelay`, so execution is deferred by the specified `TimeSpan`. The workflow is created
  immediately in Temporal but does not begin executing until the delay elapses. If a workflow
  with the same session ID is already running (`UseExisting` policy), the delay is ignored and
  the existing workflow is reused.

- **`TemporalAIAgentProxy.RunDelayedAsync`** (internal) — surfaces `RunAgentDelayedAsync`
  on the proxy for callers that hold a `TemporalAIAgentProxy` reference directly.

#### Registration changes (`TemporalWorkerBuilderExtensions.AddTemporalAgents`)

- Registers `AgentJobWorkflow` alongside `AgentWorkflow` on the worker.
- Pre-registers `ScheduleActivities` as a singleton (factory closes over `taskQueue`) before
  calling `AddSingletonActivities<ScheduleActivities>()`, so the task-queue binding is correct.
- Conditionally registers `ScheduleRegistrationService` as an `IHostedService` when at least
  one scheduled run has been declared via `AddScheduledAgentRun`.

### Known Limitations

- **Schedule orphaning**: Temporal Schedules are independent of workers. Removing an agent from
  `TemporalAgentsOptions` does **not** delete its schedule — it will keep firing. Use
  `GetAgentScheduleHandle` to retrieve the handle and call `DeleteAsync()` when decommissioning.

- **Config drift in `AddScheduledAgentRun`**: if a schedule's spec changes in code (e.g., from
  daily to twice-daily), the change is silently ignored on restart because the existing schedule
  is skipped. To apply the update, delete the schedule first via `GetAgentScheduleHandle`, then
  restart the worker.

- **`RunAgentDelayedAsync` delay ignored for existing sessions**: `StartDelay` only applies when
  starting a brand-new session. If a workflow with the same session ID is already running, the
  delay is ignored and the existing workflow is reused immediately.

- **Scheduled run results are not captured**: scheduled runs are fire-and-forget by design.
  Run status and workflow event history are visible in the Temporal Web UI.

[0.1.0]: https://github.com/cecilphillip/TemporalAgents/releases/tag/v0.1.0
