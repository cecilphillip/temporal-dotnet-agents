# Per-Tool Temporal Activities (Step Mode)

How to opt the Agents library into a workflow-driven tool-dispatch loop where every LLM call and every tool call becomes a separately retryable, separately observable Temporal activity. This is the recommended path for agents that perform write-style tool calls (send email, write record, charge card) and for any production workload where independent per-tool retry, per-tool timeout, or per-tool visibility matters.

---

## Table of Contents

1. [What It Is](#what-it-is)
2. [Why Use It](#why-use-it)
3. [Default Mode vs. Step Mode](#default-mode-vs-step-mode)
4. [Quick Start](#quick-start)
5. [Agent Registration Constraint](#agent-registration-constraint)
6. [Per-Tool `ActivityOptions`](#per-tool-activityoptions)
7. [Iteration Cap (`MaxToolCallsPerTurn`)](#iteration-cap-maxtoolcallsperturn)
8. [What the Temporal Web UI Shows](#what-the-temporal-web-ui-shows)
9. [Migration and Coexistence](#migration-and-coexistence)
10. [When NOT to Use It](#when-not-to-use-it)
11. [References](#references)

---

## What It Is

Step mode is an opt-in alternative to the default single-activity-per-turn execution model in `Temporalio.Extensions.Agents`.

In the default mode, an entire agent turn — the LLM call plus every tool call performed by `FunctionInvokingChatClient` — is wrapped in one opaque `ExecuteAgent` Temporal activity. The activity returns when the model produces its final answer. If the worker crashes mid-turn, the whole turn replays from scratch on a new worker.

In step mode, the agentic loop moves into the workflow itself:

- Each LLM call is a separate `RunAgentStep` activity that returns either a final assistant message or the model's pending tool calls (without executing them).
- Each tool call is a separate `InvokeFunction` activity dispatched in parallel from the workflow via `Workflow.WhenAllAsync`.
- The workflow alternates between the two activity types until the model produces a final answer or the iteration cap is hit.

Tools are sourced from the same `DurableFunctionRegistry` that the AI library uses for `Temporalio.Extensions.AI` durable tools — there is no separate registration step.

Step mode is enabled with one flag:

```csharp
opts.EnablePerToolActivities = true;
```

When the flag is `false` (the default), behavior is identical to today. No existing user is affected by the feature's existence.

---

## Why Use It

Three production concerns motivate the feature.

### 1. Independent retry granularity

In the default mode, a transient failure in any tool call — a 503 from the third tool the model called this turn — fails the whole `ExecuteAgent` activity and replays everything: the LLM call, the first two successful tool calls, and the failing third one. The two billable LLM calls and the two side-effect-bearing tool calls all re-execute.

In step mode, the failing `InvokeFunction(tool_3)` activity retries on its own retry policy. The successful sibling tool calls do not re-execute, and the LLM call that produced the tool-call batch is not repeated.

### 2. Idempotent write tools

Write-style tools — `send_email`, `write_record`, `charge_card`, `create_ticket` — are typically not safe to retry. In the default mode, any worker crash or transient failure inside the agent turn re-runs the whole turn, which means re-running the write tool. There is no safe knob to turn that off, because the retry policy applies to the whole turn.

Step mode lets you set a per-tool retry policy. Mark a write tool with `MaximumAttempts = 1` in `PerToolActivityOptions` and Temporal will not re-execute it on retry. Read tools (`lookup_order`, `search_inventory`) keep the default unbounded-retry posture.

```csharp
opts.PerToolActivityOptions ??= new();
opts.PerToolActivityOptions["send_email"] = new ActivityOptions
{
    StartToCloseTimeout = TimeSpan.FromSeconds(30),
    RetryPolicy = new RetryPolicy { MaximumAttempts = 1 },  // never double-fire
};
```

### 3. Per-tool visibility in the Temporal Web UI

In the default mode, the Temporal Web UI shows a single `ExecuteAgent` activity per turn. Token counts, tool latencies, and retry counts roll up into one row.

In step mode, each LLM call produces a distinct `RunAgentStep` activity row, and each tool call produces a distinct `InvokeFunction` activity row labeled with the tool's name. Operators can answer "which tool failed and how many times did it retry?" by reading the activity list directly, without correlating logs.

---

## Default Mode vs. Step Mode

| Concern | Default mode | Step mode |
|---|---|---|
| Activities per turn | 1 (`ExecuteAgent`) | `2N + 1` for `N` tool rounds (`N+1` LLM steps + `N` tool batches) |
| Tool dispatch loop runs in | Activity (`FunctionInvokingChatClient`) | Workflow |
| Per-tool retry policy | No (turn-level only) | Yes (`PerToolActivityOptions`) |
| Per-tool timeout | No (turn-level only) | Yes (`PerToolActivityOptions`) |
| Web UI rows per tool | 0 (rolled into `ExecuteAgent`) | 1 (named `InvokeFunction`) |
| Required call to `AddDurableAI` | No | Yes |
| Required call to `AddDurableTools` | No (tools attached to agent) | Yes (workflow dispatches them) |
| Agent uses `FunctionInvokingChatClient` | Yes | No (workflow owns the loop) |
| `AIContextProvider` (e.g. Mem0) | Supported | Supported on the LLM step activity |
| Streaming heartbeats | Yes | Yes (per LLM step) |
| Default value | (no flag) | `EnablePerToolActivities = false` |

Pick step mode when independent retry, write-tool safety, or per-tool visibility is load-bearing for the workload. Pick default mode for single-shot agents, short tool chains, or workloads where the per-call activity overhead exceeds the benefit.

---

## Quick Start

Enable step mode on the worker, register the durable tools, and register the agent without `FunctionInvokingChatClient` in its pipeline.

```csharp
using Microsoft.Extensions.AI;
using Temporalio.Common;
using Temporalio.Extensions.Agents;
using Temporalio.Extensions.AI;
using Temporalio.Workflows;

var builder = Host.CreateApplicationBuilder(args);

// Tools — registered as MEAI AIFunctions, picked up by both the LLM (as schema)
// and the InvokeFunction activity (by name) via DurableFunctionRegistry.
AIFunction sendEmailTool   = AIFunctionFactory.Create(SendEmailAsync);
AIFunction lookupOrderTool = AIFunctionFactory.Create(LookupOrderAsync);

builder.Services
    .AddHostedTemporalWorker("localhost:7233", "default", "agents")
    .AddDurableAI(opts => { /* DurableExecutionOptions */ })
    .AddDurableTools(sendEmailTool, lookupOrderTool)
    .AddTemporalAgents(opts =>
    {
        opts.EnablePerToolActivities = true;
        opts.MaxToolCallsPerTurn     = 20;          // optional — default 20

        // Per-tool retry policy (write tools should not double-fire)
        opts.PerToolActivityOptions ??= new();
        opts.PerToolActivityOptions["send_email"] = new ActivityOptions
        {
            StartToCloseTimeout = TimeSpan.FromSeconds(30),
            RetryPolicy = new RetryPolicy { MaximumAttempts = 1 },
        };

        // Agent factory — note: NO UseFunctionInvocation() in the pipeline.
        // The workflow owns the tool-dispatch loop in step mode.
        opts.AddAIAgentFactory("SupportAgent", sp =>
            sp.GetRequiredService<IChatClient>()
              .AsAIAgent(
                  name: "SupportAgent",
                  instructions: "You are a customer-support assistant.",
                  tools: [sendEmailTool, lookupOrderTool])); // schema only
    });

var host = builder.Build();
await host.RunAsync();
```

The agent now runs in step mode: every LLM call is a `RunAgentStep` activity, every tool invocation is a separately named `InvokeFunction` activity, and `send_email` will not re-execute on retry.

> **Worker-startup validation**: Setting `EnablePerToolActivities = true` without calling `AddDurableAI()` on the same worker builder fails fast with `InvalidOperationException` at composition time. The library does not silently fall back to the default path — silent fallback would defeat the per-tool retry guarantee that callers opted in for. See `TemporalAgentsRegistrar.Register`.

---

## Agent Registration Constraint

The mental model is: **step mode bypasses `FunctionInvokingChatClient` so tool calls return raw to the workflow.** The workflow is then the thing that dispatches each tool call as a separate activity.

Concretely, the agent's `IChatClient` pipeline must NOT include `UseFunctionInvocation()`. If it does, the call to `RunAgentStep` would auto-execute tools inside the activity (the same behavior as default mode), and the workflow would never see `FunctionCallContent` to dispatch.

Two equivalent ways to register the agent:

```csharp
// (a) chatClient.AsAIAgent(...) — the convenience overload does NOT add
// UseFunctionInvocation by default. Pass tools as schema only.
opts.AddAIAgentFactory("MyAgent", sp =>
    sp.GetRequiredService<IChatClient>()
      .AsAIAgent(
          name: "MyAgent",
          instructions: "...",
          tools: [tool1, tool2]));   // schema → ChatOptions.Tools

// (b) Hand-built ChatClientAgent — same constraint: pipeline must not include
// UseFunctionInvocation.
opts.AddAIAgentFactory("MyAgent", sp =>
    new ChatClientAgent(
        sp.GetRequiredService<IChatClient>(),
        new ChatClientAgentOptions
        {
            Name         = "MyAgent",
            Instructions = "...",
            Tools        = [tool1, tool2],
        }));
```

The same tools must also be registered via `AddDurableTools(...)` so they are resolvable by name in `DurableFunctionRegistry` when the workflow dispatches `InvokeFunction`. Tool names are matched case-insensitively; arguments flow through as a `Dictionary<string, object?>`.

> If a registered agent is *not* a `ChatClientAgent`, the step activity falls back to running without instructions. Most production agents are `ChatClientAgent` instances; if you have a custom `AIAgent` subclass that needs instructions injected for step mode, register it as a `ChatClientAgent` wrapping your inner client.

---

## Per-Tool `ActivityOptions`

`TemporalAgentsOptions.PerToolActivityOptions` is a case-insensitive dictionary keyed by tool name (`AIFunction.Name`):

```csharp
public Dictionary<string, ActivityOptions>? PerToolActivityOptions { get; set; }
```

When the workflow dispatches `InvokeFunction(toolName)`, it looks the name up in this dictionary. If found, the dictionary entry's `ActivityOptions` are used verbatim. If not found, the workflow falls back to:

| Field | Default value |
|---|---|
| `StartToCloseTimeout` | `TemporalAgentsOptions.ActivityTimeout` (5 minutes by default) |
| `HeartbeatTimeout` | `TemporalAgentsOptions.HeartbeatTimeout` (2 minutes by default) |
| `Summary` | The tool's name (visible in the Web UI activity list) |
| `RetryPolicy` | `TemporalAgentsOptions.RetryPolicy` (Temporal SDK default unbounded retry when null) |

Common patterns:

```csharp
opts.PerToolActivityOptions ??= new();

// Write tool — never double-fire
opts.PerToolActivityOptions["send_email"] = new ActivityOptions
{
    StartToCloseTimeout = TimeSpan.FromSeconds(30),
    RetryPolicy = new RetryPolicy { MaximumAttempts = 1 },
};

// Slow read tool — generous timeout, full retries
opts.PerToolActivityOptions["semantic_search"] = new ActivityOptions
{
    StartToCloseTimeout = TimeSpan.FromMinutes(2),
    RetryPolicy = new RetryPolicy
    {
        InitialInterval    = TimeSpan.FromSeconds(1),
        BackoffCoefficient = 2.0,
        MaximumInterval    = TimeSpan.FromSeconds(30),
        MaximumAttempts    = 0,  // unbounded
    },
};
```

> **Idempotency caveat**: even with `MaximumAttempts = 1`, an activity may run more than once if the worker crashes after the side effect but before Temporal records the activity result. This is a Temporal-wide property, not specific to step mode. For tools that absolutely must not double-fire, embed a caller-supplied idempotency key in the tool arguments and check it inside the tool body.

---

## Iteration Cap (`MaxToolCallsPerTurn`)

`TemporalAgentsOptions.MaxToolCallsPerTurn` (default `20`) bounds how many times the step loop may iterate within a single user turn. Each iteration is one `RunAgentStep` activity plus zero-or-one batches of `InvokeFunction` activities.

When the cap is exceeded, the workflow does NOT throw. It returns a structured assistant message of the form:

```
Maximum tool-call iterations (20) exceeded for this turn. The agent did not converge on a final answer.
```

This message is appended to the multi-step transcript and surfaced in the final `AgentResponse` as a normal assistant `ChatMessage`. From the caller's perspective, the turn completes successfully with a response that the calling code can detect and handle.

The cap exists to prevent runaway tool-calling on adversarial or buggy inputs from growing workflow history without bound. Each step iteration consumes Temporal events (one `ActivityScheduled` + one `ActivityCompleted` per LLM call, plus one of each per tool call); without a cap, a model that loops forever calling tools would eventually hit Temporal's history-size limit and force a continue-as-new mid-turn — which is harder to reason about than a clean structured failure.

Tune it up for agents that legitimately chain many tools per turn (research agents, multi-stage planners). Tune it down for hardening against adversarial inputs.

---

## What the Temporal Web UI Shows

A two-tool turn in step mode produces this activity timeline:

```
RunAgentStep                                           ← LLM call #1 (returns 2 tool calls)
  ├─ InvokeFunction (Summary: "lookup_order")          ← tool call A (parallel)
  └─ InvokeFunction (Summary: "search_inventory")      ← tool call B (parallel)
RunAgentStep                                           ← LLM call #2 (returns final answer)
```

Compare to the same turn in default mode:

```
ExecuteAgent (Summary: "MyAgent")                      ← whole turn, opaque
```

In the Web UI activity list, each `InvokeFunction` row is named by the tool — making "which tools ran, in what order, with what retry counts" a one-glance question. Each `RunAgentStep` row shows token usage in its OTel `agent.turn` span, exactly the same as `ExecuteAgent` in default mode.

For OTel users, the existing `agent.turn` span is emitted once per `RunAgentStep` activity rather than once per turn. If you have token-cost dashboards that aggregate by turn, switch to summing across the steps that share an `agent.session_id`.

---

## Migration and Coexistence

Sessions that started before the upgrade keep using the default path. The flag travels with the workflow input:

| Session state at deploy | Behavior after the deploy |
|---|---|
| Workflow started with `EnablePerToolActivities = false`, still running | Continues using the single-activity path — `AgentWorkflowInput.EnablePerToolActivities` carries the original `false` |
| Workflow started with `EnablePerToolActivities = false`, completes naturally | Done — the next session created by the client reads the current options |
| Workflow started with `EnablePerToolActivities = false`, hits continue-as-new | Carries `EnablePerToolActivities = false` forward — the new run still uses the default path |
| New workflow started after the deploy | Reads the current `EnablePerToolActivities` value, uses step mode from turn 1 |

Both modes can coexist on the same worker. If you have agents `WriteHeavyAgent` (step-mode) and `ReadOnlyAgent` (default-mode) on the same worker process, register both, set `opts.EnablePerToolActivities = true`, and configure `PerToolActivityOptions` only for the write tools. The flag is global, but agents whose tool sets do not need per-tool retry are not penalized — they simply produce one `RunAgentStep` per turn followed by no `InvokeFunction` activities (the model returns a final answer immediately).

There is no data-migration step. In-flight sessions cannot be retroactively migrated to step mode without a workflow-replay rewrite of historical events, which Temporal does not support.

---

## When NOT to Use It

Step mode adds workflow-history events per tool call. The default path is the right answer for:

- **Single-shot agents that don't call tools.** The model produces a final answer in one LLM call. Step mode adds one `RunAgentStep` activity for zero benefit.
- **Short tool chains where per-call overhead dominates.** A tight three-call chain of fast read tools (each well under a second) pays Temporal's activity-scheduling overhead three times in step mode versus once in default mode. For these, the default path is faster end-to-end.
- **Agents with non-idempotent multi-step internal logic that benefits from atomic execution.** If a tool internally manages state across calls (e.g., a tool that reads-then-writes within its own body), splitting the LLM call from the tool call gives no extra safety; you already trust the tool's atomicity.
- **Demos and prototypes.** The provisioning overhead — registering tools twice (`AddDurableTools` plus the agent's `tools:` parameter), deciding per-tool retry policies, planning around the iteration cap — is larger than the problem.

For agents that fan out to fewer than ~3 read-only tools per turn with no PII or compliance concerns around tool-argument exposure in Temporal events, default mode is the recommendation.

---

## See Also

- Runnable sample: [`samples/MAF/PerToolActivities/`](../../../samples/MAF/PerToolActivities/) — drives a refund agent with read + two write tools and demonstrates that a transient lookup failure retries without re-firing the write tools.

## References

- [Agent Sessions and the Workflow Loop — Step-Mode Agentic Loop](../../architecture/MAF/agent-sessions-and-workflow-loop.md#step-mode-agentic-loop) — workflow-loop diagram and determinism rationale
- [Usage Guide — `TemporalAgentsOptions` reference](./usage.md) — full options table
- [Do's and Don'ts](./dos-and-donts.md) — `Workflow.WhenAllAsync` over `Task.WhenAll`, `ConfigureAwait(false)` warnings, write-tool retry pattern
- [LLM-Call Interception](./llm-call-interception.md) — per-LLM-call observability via `ChatClientFactory` (composes with step mode)
- `src/Temporalio.Extensions.Agents/Workflows/AgentStepInput.cs` — step activity input
- `src/Temporalio.Extensions.Agents/Workflows/AgentStepResult.cs` — step activity result
- `src/Temporalio.Extensions.Agents/Workflows/AgentActivities.cs` — `RunAgentStepAsync` implementation
- `src/Temporalio.Extensions.Agents/Workflows/AgentWorkflow.cs` — `ExecuteStepModeTurnAsync` workflow loop
- `src/Temporalio.Extensions.Agents/TemporalAgentsOptions.cs` — `EnablePerToolActivities`, `PerToolActivityOptions`, `MaxToolCallsPerTurn`

---

_Last updated: 2026-05-05_
