# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

---

## [Unreleased]

### Added

- **`DurableChatWorkflowBase<TOutput>` virtual hooks** for subclass extension:
  `InitializeTurnCount(carriedHistory)`, `UpsertCustomSearchAttributes()`, and a
  `protected int CurrentTurnNumber { get; }` accessor. Enables future workflow
  subclasses (e.g., the agents library's `AgentWorkflow` in Layer 3 Phase 2) to
  share the session-loop body while customizing turn-count semantics, search
  attributes, and per-turn metadata access.

- **`DurableSessionRequest.FromMessages` auto-generates correlation ID and
  timestamp** when arguments are null. Uses `Workflow.NewGuid()`/`Workflow.UtcNow`
  inside workflow context, `Guid.NewGuid()`/`DateTimeOffset.UtcNow` otherwise.
  Collapses the null-fallback boilerplate that previously lived at every
  call site.

### Changed (BREAKING)

- **`SearchAttributes` field renamed to `EnableSearchAttributes`** on
  `DurableExecutionOptions`, `DurableChatWorkflowInput`, and the equivalent
  fields on the agents library's `TemporalAgentsOptions` and `AgentWorkflowInput`.
  Type changes from a nullable opt-in object to a `bool` (default `false`).
  Same opt-in semantics, more discoverable name, simpler type.

  **Migration:** wherever you set `opts.SearchAttributes = ...` (or the
  equivalent), change to `opts.EnableSearchAttributes = true`.

- **`TurnCount` search attribute now monotonically grows** across
  continue-as-new boundaries. Previously reset to 0 on each CAN. The new
  behavior is more useful for monitoring (turn count over a workflow's
  lifetime, not per-CAN-segment). Affects both `DurableChatWorkflow` and
  `AgentWorkflow`.

  **Migration:** if you have dashboards or queries against the `TurnCount`
  search attribute that assumed CAN-segment semantics, update them to expect
  monotonic growth.

- **`DurableChatWorkflowBase<TOutput>.RunTurnAsync` signature changed.**
  From `(IReadOnlyList<ChatMessage> userMessages, string? correlationId,
  ChatOptions? chatOptions, ...)` to `(DurableSessionRequest requestEntry,
  ChatOptions? chatOptions = null, CancellationToken cancellationToken =
  default)`. Caller constructs the request entry directly via
  `DurableSessionRequest.FromMessages(...)`.

- **`DurableChatWorkflowBase<TOutput>.ExecuteTurnAsync` abstract signature
  changed.** From `(ActivityOptions, DurableChatInput)` to `(ActivityOptions,
  DurableSessionRequest, ChatOptions?)`. Subclass overrides own activity-input
  construction.

- **`BuildRequestEntry` virtual hook removed** from `DurableChatWorkflowBase`.
  Subclasses construct request entries at the `[WorkflowUpdate]` call site
  via `DurableSessionRequest.FromMessages(...)` or library-specific factories.

- **`AgentWorkflowInput` inherits from `DurableChatWorkflowInput`.** Shared
  fields (`MaxEntryCount`, `HistoryReducer`, `OriginalCreatedAt`, etc.) come
  from the base. MAF-specific fields (`AgentName`, `TaskQueue`,
  `CarriedStateBag`, `RetryPolicy`) stay on the subclass.

### Changed

- **`AgentWorkflow` now inherits from `DurableChatWorkflowBase<AgentResponse>`.**
  Internal refactor — public API surface (`TemporalAIAgentProxy.RunAsync`,
  `DefaultTemporalAgentClient`, `[WorkflowQuery("GetHistory")]`,
  `[WorkflowSignal("RequestShutdown")]`, HITL approval methods) is unchanged.
  The session-loop body, turn mutex, continue-as-new triggering, and HITL
  handlers are now provided by the base class. `AgentWorkflow` shrinks by
  ~150 lines; future session-loop improvements land once and benefit both
  libraries.

  MAF-specific concerns retained on the `AgentWorkflow` subclass:
  fire-and-forget signal handler (`RunAgentFireAndForgetAsync`), StateBag
  carry-forward across continue-as-new, and agent-name structured logging.
  The `AgentName` search attribute is upserted via the new
  `UpsertCustomSearchAttributes` virtual hook. Activity-input construction
  (`ExecuteAgentInput`) lives in the subclass's `ExecuteTurnAsync` override;
  `BuildResponseEntry` produces an `AgentSessionResponse` from the
  `AgentResponse` output via `AgentSessionResponse.FromAgentResponse(...)`.

---

## [0.2.0] - 2026-04-30

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

- **`Temporalio.Extensions.Agents` shares session-entry types with the AI
  library.** `AgentSessionRequest` and `AgentSessionResponse` are now
  subclasses of `DurableSessionRequest` and `DurableSessionResponse` (in
  `Temporalio.Extensions.AI`). MAF-specific fields (`OrchestrationId`,
  `ResponseType`, `ResponseSchema`) live on the subclasses; messages,
  `CorrelationId`, `CreatedAt`, and `Usage` live on the shared base types.
  Polymorphism is wired across the assembly boundary via a runtime
  `JsonTypeInfoResolver` modifier in `TemporalAgentJsonUtilities`, with
  discriminator strings `"agent_request"` / `"agent_response"` (alongside
  the AI library's own `"ai_request"` / `"ai_response"`).

- **`TemporalAgentRunOptions`** — a new public class extending
  `Microsoft.Agents.AI.ChatClientAgentRunOptions` with a `CorrelationId`
  property. Pass an instance to `AIAgent.RunAsync(...)`'s `options`
  parameter to thread a caller-supplied correlation ID through agent
  execution. When omitted, the workflow auto-generates one via
  `Workflow.NewGuid()` (or `Guid.NewGuid()` outside workflow context).

- **Optional `correlationId` parameter on
  `StructuredOutputExtensions.RunAsync<T>`.** Direct parameter on owned
  extension surfaces. The MAF proxy's inherited `RunAsync` uses
  `TemporalAgentRunOptions` instead (see above) — different mechanism,
  same capability.

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

- **`Temporalio.Extensions.Agents` state types removed.**
  `TemporalAgentStateEntry`, `TemporalAgentStateRequest`,
  `TemporalAgentStateResponse`, and `TemporalAgentStateUsage` are deleted.
  Replaced by `AgentSessionRequest` and `AgentSessionResponse` (extending
  the AI library's `DurableSessionRequest` / `DurableSessionResponse`).
  `Microsoft.Extensions.AI.UsageDetails` replaces the removed
  `TemporalAgentStateUsage` wrapper — token counts are now stored as the
  MEAI type directly with no per-library wrapper.

  **Migration:** drain in-flight `AgentWorkflow` workflows before
  upgrading. Update consumers of `[WorkflowQuery("GetHistory")]` to expect
  `IReadOnlyList<DurableSessionEntry>`; pattern-match on
  `DurableSessionResponse` (or the MAF subclass `AgentSessionResponse`) to
  read `Usage`, and cast to `AgentSessionRequest` to access
  `OrchestrationId`, `ResponseType`, and `ResponseSchema`.

- **`TemporalAgentsOptions.MaxHistorySize` renamed to `MaxEntryCount`.**
  Symmetric with the AI library's rename. Default (1000) unchanged.
  `AgentWorkflowInput.MaxHistorySize` is renamed to match.

- **`TemporalAgentsOptions.HistoryReducer` shape changed** from
  `Func<IList<TemporalAgentStateEntry>, IList<TemporalAgentStateEntry>>?`
  to `Func<IList<DurableSessionEntry>, IList<DurableSessionEntry>>?`.
  Existing reducer delegates need their generic argument updated to the
  new entry type. The reducer is invoked at continue-as-new boundaries
  with the full pre-trim history; the returned subset becomes the initial
  history of the new workflow run.

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
