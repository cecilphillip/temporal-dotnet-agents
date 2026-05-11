# Usage Guide

Detailed usage patterns and configuration for Temporalio.Extensions.Agents. For a quick overview, see the [README](../../../README.md).

---

## Durable Agents (`AddDurableAgent`)

> **v0.3 surface.** `AddDurableAgent` is the only registration path. The v0.2 entry points were removed without `[Obsolete]` aliases — see [`MIGRATION-v0.3.md`](../../../MIGRATION-v0.3.md) for before/after migration snippets covering every removed API.

`AddDurableAgent(string name, Action<DurableAgentBuilder> configure)` is a single registration entry point that consolidates everything an agent needs — chat client, instructions, tools (with per-tool retry overrides), context providers, per-agent timeouts, and external-history opt-in — onto one fluent builder. DI access is provided via per-slot factories on the builder, so you do not need to call `BuildServiceProvider()` to wire dependencies.

### Canonical example

```csharp
builder.Services.AddSingleton<OrderService>();
builder.Services.AddSingleton<RefundService>();
builder.Services.AddSingleton<EmailService>();
builder.Services.AddChatClient(openAiClient.GetChatClient(model).AsIChatClient()).Build();

builder.Services
    .AddHostedTemporalWorker(taskQueue)
    .AddTemporalAgents(opts =>
    {
        opts.AddDurableAgent("RefundAgent", agent =>
        {
            agent.Description = "Issues refunds and notifies the customer.";
            agent.Instructions = "You are a refund specialist.";
            agent.ChatClient   = sp => sp.GetRequiredService<IChatClient>();

            agent.AddTool(sp => AIFunctionFactory.Create(
                sp.GetRequiredService<OrderService>().LookupOrder,
                "lookup_order"));

            // Write tools must opt out of retry — non-idempotent re-execution is the foot-gun.
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

            agent.MaxToolCallsPerTurn = 10;
        });
    })
    .AddWorkflow<RefundWorkflow>();
```

### `DurableAgentBuilder` reference

| Property / Method | Purpose |
|-------------------|---------|
| `Name` (read-only) | Case-insensitive agent name passed in to `AddDurableAgent`. |
| `Description` | Used in `GetAgentDescriptors()` for routing prompts. Optional. |
| `Instructions` | Agent system prompt. Library stamps onto every LLM call's `ChatOptions.Instructions`. Optional. |
| `ChatClient` | **Required.** `Func<IServiceProvider, IChatClient>` invoked once at first dispatch. |
| `ChatOptions` | LLM-call template (Temperature, ResponseFormat, MaxOutputTokens, etc.). `Tools` and `Instructions` set on this property are ignored. |
| `AddTool(AIFunction tool, Action<DurableToolOptions>? configure = null)` | Registers a concrete `AIFunction`. Per-tool retry / timeout via `configure`. |
| `AddTool(string name, Func<IServiceProvider, AIFunction> factory, Action<DurableToolOptions>? configure = null)` | DI-resolving tool factory. |
| `AddTools(params AIFunction[] tools)` | Bulk registration of concrete tools. |
| `AddContextProvider(AIContextProvider provider)` / `AddContextProvider(Func<IServiceProvider, AIContextProvider>)` | Wires a provider into the chat pipeline. `Invoking/InvokedAsync` fire once per LLM call. |
| `TimeToLive`, `ApprovalTimeout`, `ActivityTimeout`, `HeartbeatTimeout` | Per-agent overrides. `null` inherits the worker-level default on `TemporalAgentsOptions`. |
| `RetryPolicy` | Retry policy for the agent's `RunAgentStep` activity (the LLM call). Per-tool retry is configured separately via `DurableToolOptions`. |
| `MaxEntryCount`, `HistoryReducer` | Per-agent continue-as-new bounds and reducer. Inherit worker defaults when unset. |
| `MaxToolCallsPerTurn` | Cap on LLM-step iterations per agent turn (default `20` when not set). Applies across all three execution paths: session-based workflows, scheduled jobs, and sub-agent orchestration via `GetAgent()`. No worker-level fallback. |
| `HistoryStore` | Per-agent `IAgentHistoryStore` factory. `null` inherits `opts.HistoryStore`; if both are `null`, history is carried in workflow state. |

### `DurableToolOptions` reference

| Property / Method | Purpose |
|-------------------|---------|
| `StartToCloseTimeout`, `HeartbeatTimeout`, `RetryPolicy` | Standard Temporal activity overrides. `null` inherits worker default. |
| `NoRetry()` | Sets `RetryPolicy = new() { MaximumAttempts = 1 }`. Use on write tools. |
| `WithMaxAttempts(int n)` | Sets a fixed-retry policy. |
| `WithTimeout(TimeSpan t)` | Sets `StartToCloseTimeout`. |

### Inheritance — per-agent vs worker-level

For every scalar setting the rule is: **if you set it on the agent, it overrides the worker default; if you leave it `null`, the worker-level default applies.**

| Per-agent setting (`DurableAgentBuilder`) | Worker default (`TemporalAgentsOptions`) |
|-------------------------------------------|------------------------------------------|
| `agent.TimeToLive` | `opts.DefaultTimeToLive` |
| `agent.ApprovalTimeout` | `opts.DefaultApprovalTimeout` |
| `agent.ActivityTimeout` | `opts.DefaultActivityTimeout` |
| `agent.HeartbeatTimeout` | `opts.DefaultHeartbeatTimeout` |
| `agent.RetryPolicy` | `opts.DefaultRetryPolicy` |
| `agent.MaxEntryCount` | `opts.DefaultMaxEntryCount` |
| `agent.HistoryReducer` | `opts.DefaultHistoryReducer` |
| `agent.HistoryStore` | `opts.HistoryStore` |
| `agent.MaxToolCallsPerTurn` | *no worker fallback — defaults to `20`; propagates to scheduled jobs and sub-agent orchestration* |

The retry-policy hierarchy adds one more layer specifically for tools. From most to least specific:

1. `agent.AddTool(t, opts => opts.DefaultRetryPolicy = ...)` — the per-tool override (use `opts.NoRetry()` on write tools).
2. `agent.RetryPolicy` — the agent-level default for any tool that doesn't override.
3. `opts.DefaultRetryPolicy` — the worker-level default used by agents that don't override.

There is **no per-agent "default for all my tools" cascade beyond `agent.RetryPolicy`** — set policies per tool when the per-tool default is genuinely different.

### Lifecycle and composition

The chat client, tool factories, context providers, and history-store factory all run lazily on first activity dispatch and are cached for the lifetime of the worker process. Concurrent first-dispatches for the same agent compose at most once.

The library composes the chat pipeline internally and passes `UseProvidedChatClientAsIs = true` to MAF so that `FunctionInvokingChatClient` is **not** auto-injected — the workflow owns the tool-dispatch loop. Register a bare `IChatClient` in DI (do not call `.UseFunctionInvocation()`).

`AIContextProvider.InvokingAsync` and `InvokedAsync` fire **once per LLM call** (per `RunDurableAgentStep` activity). A turn that takes 3 LLM-step iterations to converge will see 3 invocation pairs. Make these hooks idempotent and cheap, or cache results via `StateBag` to skip redundant work within a turn.

For the workflow-loop semantics (per-tool fan-out, crash safety, continue-as-new) see [`docs/architecture/MAF/agent-sessions-and-workflow-loop.md`](../../architecture/MAF/agent-sessions-and-workflow-loop.md).

---

## Library Dependencies

`Temporalio.Extensions.Agents` depends on `Temporalio.Extensions.AI`. Installing the Agents NuGet package pulls in the AI package automatically — no separate `<PackageReference>` for `Temporalio.Extensions.AI` is needed.

The HITL types (`DurableApprovalRequest`, `DurableApprovalDecision`) are defined in `Temporalio.Extensions.AI` and used by both libraries as the shared wire protocol for approval flows. They are available in your project as soon as you reference `Temporalio.Extensions.Agents`.

---

## Table of Contents

1. [Sending Messages](#sending-messages)
2. [Multi-Turn Conversations](#multi-turn-conversations)
3. [Reducing the LLM Context Window](#reducing-the-llm-context-window)
4. [Fire-and-Forget](#fire-and-forget)
5. [Structured Output](#structured-output)
6. [Tool Filtering](#tool-filtering)
7. [Agent Orchestration (Inside Workflows)](#agent-orchestration-inside-workflows)
8. [Session Identity](#session-identity)
9. [Session TTL](#session-ttl)
10. [Activity Timeouts](#activity-timeouts)
11. [Accessing Temporal from Agent Tools](#accessing-temporal-from-agent-tools)
12. [Streaming Responses](#streaming-responses)
13. [Routing](#routing)
14. [Parallel Agent Execution](#parallel-agent-execution)
15. [Human-in-the-Loop (HITL) Approval Gates](#human-in-the-loop-hitl-approval-gates)
16. [Scheduling](#scheduling)
17. [MCP Tool Integration](#mcp-tool-integration)
18. [External Memory with AIContextProvider](#external-memory-with-aicontextprovider)
19. [External History Store](#external-history-store)
20. [Per-Tool Activity Configuration](#per-tool-activity-configuration)
21. [OpenTelemetry Integration](#opentelemetry-integration)

---

## Sending Messages

### External Caller

```csharp
// Create (or resume) a session
AgentSession session = await agentProxy.CreateSessionAsync();

// Send a message and get a response
AgentResponse response = await agentProxy.RunAsync("Hello, agent!", session);

Console.WriteLine(response.Messages[0].Text);
```

The session ID encodes the agent name and a unique key as a Temporal workflow ID (`ta-myagent-{key}`). Passing the same
session across calls routes all messages to the same `AgentWorkflow` instance, preserving conversation history.

### Quick One-Shot Call

For simple one-off requests where you don't need to manage sessions, create an explicit session with a well-known key or use a randomly-keyed session:

```csharp
ITemporalAgentClient client = // resolved from DI

// Explicit session — recommended pattern
var session = new TemporalAgentSessionId("MyAgent", Guid.NewGuid().ToString("N"));
AgentResponse response = await client.RunAgentAsync(session, new RunRequest("What is the capital of France?"));
```

> **Note:** The `RunAgentAsync(string agentName, string message)` string convenience overload is deprecated (`[Obsolete]`). Prefer constructing a `TemporalAgentSessionId` and calling `RunAgentAsync(sessionId, request)` directly so you retain a handle to the session for follow-up turns.

---

## Multi-Turn Conversations

```csharp
var session = await agentProxy.CreateSessionAsync();

var r1 = await agentProxy.RunAsync("What is the capital of France?", session);
Console.WriteLine(r1.Messages[0].Text);  // Paris

var r2 = await agentProxy.RunAsync("What is its population?", session);
Console.WriteLine(r2.Messages[0].Text);  // ~2.1 million (context preserved)
```

---

## Reducing the LLM Context Window

For long-running agent sessions the conversation history accumulated in the
`AgentWorkflow` can grow large enough to make each LLM call expensive.
`Temporalio.Extensions.Agents` works with the same MEAI `IChatReducer` family
as `Temporalio.Extensions.AI`: register a stateless reducer such as
`MessageCountingChatReducer` on the underlying `IChatClient` that backs your
`AIAgent`. The reducer applies a sliding window at the LLM-call boundary —
inside `AgentActivities.RunDurableAgentStepAsync` — so it does not need to be replay-safe.

```csharp
var chatClient = openAiClient.GetChatClient("gpt-4o-mini")
    .AsBuilder()
    .UseChatReducer(new MessageCountingChatReducer(20))   // 20-message window to the LLM
    .Build();
//  Note: do NOT call .UseFunctionInvocation() — the durable-agent path composes
//  the chat pipeline internally and tools are dispatched as separate Temporal activities.

builder.Services.AddChatClient(chatClient);

builder.Services
    .AddHostedTemporalWorker("localhost:7233", "default", "agents")
    .AddTemporalAgents(opts =>
    {
        opts.AddDurableAgent("MyAgent", agent =>
        {
            agent.Instructions = "You are a helpful assistant.";
            agent.ChatClient   = sp => sp.GetRequiredService<IChatClient>();
        });
    });
```

With this configuration:

- The `AgentWorkflow`'s `_history` retains every message ever exchanged in the
  session — that is the durable, replay-safe source of truth and survives worker
  restarts and continue-as-new transitions.
- The reducer passes only the most recent 20 messages to the LLM on each turn.
- Querying `_history` (e.g., for audit) still returns the full unreduced log.

> **Design rationale:** Conversation history lives on the workflow itself, where
> it is replay-safe via Temporal event history. Reducers shape only what is sent
> to the LLM per turn — they never own conversation state.

> **Note:** `MessageCountingChatReducer` is provided by the MEAI library
> (`Microsoft.Extensions.AI`). Any `IChatReducer` implementation works —
> token-counting reducers, summarization reducers, etc. — as long as it is
> stateless or scoped per call.

See the equivalent guidance for `Temporalio.Extensions.AI` in
[the MEAI usage guide](../MEAI/usage.md#reducing-the-llm-context-window).

---

## Fire-and-Forget

For notifications or background tasks where you don't need to wait for the agent's response:

```csharp
var options = new TemporalAgentRunOptions { IsFireAndForget = true };
await agentProxy.RunAsync("Process this in the background.", session, options);
// Returns immediately with an empty AgentResponse
```

---

## Structured Output

### Using `RunAsync<T>` (Recommended)

`StructuredOutputExtensions.RunAsync<T>` deserializes the agent's text response directly into a typed object. It
automatically strips markdown code fences (`` ```json ... ``` ``) that many models wrap around JSON output, and retries
with error context when deserialization fails — allowing the LLM to self-correct:

```csharp
var session = await agentProxy.CreateSessionAsync();

// Automatically strips code fences, deserializes, and retries on failure
WeatherReport report = await agentProxy.RunAsync<WeatherReport>(
    new List<ChatMessage> { new(ChatRole.User, "What's the weather in Seattle?") },
    session);
```

Control retry behavior with `StructuredOutputOptions`:

```csharp
var report = await agentProxy.RunAsync<WeatherReport>(
    new List<ChatMessage> { new(ChatRole.User, "What's the weather in Seattle?") },
    session,
    new StructuredOutputOptions
    {
        MaxRetries = 3,                // default: 2
        IncludeErrorContext = true,     // default: true — appends error details to retry prompt
        JsonSerializerOptions = myOpts  // default: null — uses JsonSerializerOptions.Default
    });
```

`RunAsync<T>` is also available on `TemporalAIAgent` (inside workflows) and `ITemporalAgentClient`:

```csharp
// Inside a workflow
var agent = TemporalWorkflowExtensions.GetAgent("AnalystAgent");
var session = await agent.CreateSessionAsync();
var analysis = await agent.RunAsync<AnalysisResult>(messages, session);

// Via the client
var result = await client.RunAgentAsync<WeatherReport>(sessionId, request);
```

### Using `ChatResponseFormat` (Format Hint Only)

To hint the response format without automatic deserialization:

```csharp
var options = new TemporalAgentRunOptions
{
    ResponseFormat = ChatResponseFormat.ForJsonSchema<WeatherReport>()
};

var session = await agentProxy.CreateSessionAsync();
var response = await agentProxy.RunAsync("What's the weather in Seattle?", session, options);
var report = response.Messages[0].GetContent<WeatherReport>();
```

---

## Tool Filtering

Restrict which tools the agent may use for a specific request:

```csharp
var options = new TemporalAgentRunOptions
{
    EnableToolNames = ["get_weather", "search_web"],
    // EnableToolCalls = false  // disable all tools for this request
};

var response = await agentProxy.RunAsync("Look up the latest news.", session, options);
```

---

## Agent Orchestration (Inside Workflows)

Use `TemporalWorkflowExtensions.GetAgent` to interact with agents from within an orchestrating Temporal workflow. The
agent's conversation history is stored in the workflow's event history and replayed automatically.

```csharp
using Temporalio.Workflows;
using Temporalio.Extensions.Agents;

[Workflow]
public class ResearchWorkflow
{
    [WorkflowRun]
    public async Task<string> RunAsync(string topic)
    {
        // Get a TemporalAIAgent — runs inference via activity, history tracked in workflow state
        var researcher = TemporalWorkflowExtensions.GetAgent("ResearcherAgent");
        var session = await researcher.CreateSessionAsync();

        var outline = await researcher.RunAsync($"Create an outline about: {topic}", session);

        var writer = TemporalWorkflowExtensions.GetAgent("WriterAgent");
        var writerSession = await writer.CreateSessionAsync();

        var draft = await writer.RunAsync(
            $"Write a short article based on this outline:\n{outline.Messages[0].Text}",
            writerSession);

        return draft.Messages[0].Text;
    }
}
```

`TemporalAIAgent` (returned by `GetAgent`) stores the conversation history as workflow state. This means it survives
worker restarts, supports retries, and is durable by design — all without any extra persistence code.

---

## Session Identity

A `TemporalAgentSessionId` directly maps to a Temporal workflow ID:

```
ta-{agentName (lowercase)}-{key}
```

You can create sessions with explicit keys for deterministic session routing (e.g., one session per user ID):

```csharp
// Deterministic: always routes to the same workflow for a given userId
var sessionId = new TemporalAgentSessionId("MyAgent", userId);
var session = new TemporalAgentSession(sessionId);

var response = await agentProxy.RunAsync("Hello!", session);
```

---

## Session TTL

Sessions expire after the configured TTL (default: 14 days). Configure per-agent overrides on the builder:

```csharp
opts.AddDurableAgent("ShortLivedAgent", agent =>
{
    agent.ChatClient = sp => sp.GetRequiredService<IChatClient>();
    agent.TimeToLive = TimeSpan.FromHours(1);
});

// Or configure the default for all agents on this worker
opts.DefaultTimeToLive = TimeSpan.FromDays(7);
```

When the TTL elapses, the `AgentWorkflow` completes naturally. The next message to that session ID starts a fresh
workflow run.

---

## Activity Timeouts

Every agent turn — one call to `RunAsync` — executes inside a Temporal activity. Two timeouts govern that activity:

| Option              | Default   | What it limits                                                                                           |
|---------------------|-----------|----------------------------------------------------------------------------------------------------------|
| `ActivityTimeout`   | 5 minutes | Total wall-clock time for one turn, including tool calls and retries                                     |
| `HeartbeatTimeout`  | 2 minutes | Maximum gap between heartbeats; Temporal retries the activity if exceeded (most relevant when streaming) |

Both are non-nullable `TimeSpan` properties on `TemporalAgentsOptions` with the defaults shown above. Override either
to tune for slow models, long tool-call chains, or streaming workloads.

```csharp
builder.Services
    .AddHostedTemporalWorker("localhost:7233", "default", "agents")
    .AddTemporalAgents(opts =>
    {
        // Increase for slow models or long tool-call chains
        opts.DefaultActivityTimeout = TimeSpan.FromMinutes(10);

        // Increase if streaming heartbeats arrive slowly
        opts.DefaultHeartbeatTimeout = TimeSpan.FromMinutes(2);

        opts.AddDurableAgent("MyAgent", agent =>
        {
            agent.ChatClient = sp => sp.GetRequiredService<IChatClient>();
            agent.TimeToLive = TimeSpan.FromHours(1);
        });
    });
```

### Activity Timeouts for In-Workflow Agents

When using `TemporalWorkflowExtensions.GetAgent` inside an orchestrating workflow, pass `ActivityOptions` directly at
the call site:

```csharp
var researcher = TemporalWorkflowExtensions.GetAgent(
    "ResearcherAgent",
    activityOptions: new ActivityOptions
    {
        StartToCloseTimeout = TimeSpan.FromMinutes(5),
        HeartbeatTimeout    = TimeSpan.FromMinutes(1)
    });
```

---

## Accessing Temporal from Agent Tools

Agent tools executing inside `AgentActivities.InvokeAgentToolAsync` can access Temporal capabilities through
`TemporalAgentContext.Current`:

```csharp
public class MyAgentTool
{
    [Description("Start a background processing job")]
    public static async Task<string> StartJobAsync(string payload)
    {
        var context = TemporalAgentContext.Current;

        // Start a Temporal workflow from within an agent tool
        var workflowId = await context.StartWorkflowAsync(
            (ProcessingWorkflow wf) => wf.RunAsync(payload),
            new WorkflowOptions("job-" + Guid.NewGuid(), taskQueue: "jobs"));

        return $"Job started with ID: {workflowId}";
    }
}
```

`TemporalAgentContext` also exposes the current session:

```csharp
var sessionId = context.CurrentSession.SessionId;
Console.WriteLine($"Processing request for session: {sessionId.WorkflowId}");
```

---

## Streaming Responses

Register an `IAgentResponseHandler` to stream agent responses as they are generated (e.g., for server-sent events):

```csharp
builder.Services.AddSingleton<IAgentResponseHandler, MyStreamingHandler>();

public class MyStreamingHandler : IAgentResponseHandler
{
    public async ValueTask OnStreamingResponseUpdateAsync(
        IAsyncEnumerable<AgentResponseUpdate> stream,
        CancellationToken ct)
    {
        await foreach (var update in stream.WithCancellation(ct))
        {
            // Push each chunk to the client (e.g., via SignalR or SSE)
        }
    }
}
```

---

## Routing

Routing belongs inside your workflow, where every decision is durable, visible in history, and replayed from cache on crash recovery. The library provides two patterns:

- **Static routing** — a classifier agent runs inside the workflow and the result drives a switch statement with hardcoded agent names. Best for a fixed agent set.
- **Dynamic routing via activity** — the workflow discovers available agents by querying the descriptor registry inside an activity (whose result is cached in history). Best when the set of agents changes across deployments.

Both patterns are covered in detail in [Routing Patterns](./routing.md), with complete working code in `samples/MAF/WorkflowRouting/`.

---

## Parallel Agent Execution

`TemporalWorkflowExtensions.ExecuteAgentsInParallelAsync` dispatches multiple agent calls concurrently inside a workflow
using `Workflow.WhenAllAsync` — the workflow-safe equivalent of `Task.WhenAll`.

```csharp
using Temporalio.Workflows;
using Temporalio.Extensions.Agents;

[Workflow]
public class ResearchAndSummarizeWorkflow
{
    [WorkflowRun]
    public async Task<string> RunAsync(string topic)
    {
        var researchAgent  = TemporalWorkflowExtensions.GetAgent("ResearchAgent");
        var summaryAgent   = TemporalWorkflowExtensions.GetAgent("SummaryAgent");

        var researchSession = TemporalWorkflowExtensions.NewAgentSessionId("ResearchAgent");
        var summarySession  = TemporalWorkflowExtensions.NewAgentSessionId("SummaryAgent");

        var researchMessages = new List<ChatMessage>
            { new(ChatRole.User, $"Research the topic: {topic}") };
        var summaryMessages  = new List<ChatMessage>
            { new(ChatRole.User, $"Summarize the latest findings on: {topic}") };

        IReadOnlyList<AgentResponse> results =
            await TemporalWorkflowExtensions.ExecuteAgentsInParallelAsync(new[]
            {
                (researchAgent, (IList<ChatMessage>)researchMessages, (AgentSession)new TemporalAgentSession(researchSession)),
                (summaryAgent,  (IList<ChatMessage>)summaryMessages,  (AgentSession)new TemporalAgentSession(summarySession)),
            });

        return $"Research: {results[0].Messages[0].Text}\n\nSummary: {results[1].Messages[0].Text}";
    }
}
```

Results are returned in the same order as the input tuples. Each agent runs inside its own activity and the workflow
waits for all of them before continuing.

---

## Human-in-the-Loop (HITL) Approval Gates

Agent tools can pause mid-turn and wait for a human decision before proceeding. The backing `AgentWorkflow` exposes
two `[WorkflowUpdate]` handlers — `RequestApprovalAsync` (called from inside a tool) and `SubmitApprovalAsync` (called
from an external system) — and one `[WorkflowQuery]` handler, `GetPendingApproval`, for polling the current pending
request without modifying workflow state.

### Requesting Approval (Inside a Tool)

Call `TemporalAgentContext.Current.RequestApprovalAsync` from inside a tool implementation. The call blocks the activity
until a human submits a decision:

```csharp
public class DataDeletionTool
{
    [Description("Deletes all records for the specified user")]
    public static async Task<string> DeleteUserDataAsync(string userId)
    {
        var decision = await TemporalAgentContext.Current.RequestApprovalAsync(
            new DurableApprovalRequest
            {
                RequestId   = Guid.NewGuid().ToString("N"),
                Description = $"Delete all data for user — userId={userId}. This action is irreversible."
            });

        if (!decision.Approved)
        {
            return $"Action rejected by reviewer: {decision.Reason}";
        }

        // Proceed with deletion...
        return $"Data for user {userId} has been deleted.";
    }
}
```

Because the tool runs inside a Temporal activity, the pause is fully durable. If the worker restarts while waiting for
approval, the activity resumes from exactly the same point once a new worker picks it up.

Set `ActivityTimeout` to a value that exceeds your expected review time:

```csharp
opts.DefaultActivityTimeout = TimeSpan.FromHours(24);
```

### Checking for Pending Approvals (External System)

Poll the workflow from a UI, monitoring tool, or approval service:

```csharp
ITemporalAgentClient client = // resolved from DI
var sessionId = new TemporalAgentSessionId("MyAgent", userId);

DurableApprovalRequest? pending = await client.GetPendingApprovalAsync(sessionId);

if (pending is not null)
{
    Console.WriteLine($"Pending approval: {pending.Description}");
    Console.WriteLine($"RequestId: {pending.RequestId}");
}
```

### Submitting a Decision (External System)

```csharp
DurableApprovalDecision decision = await client.SubmitApprovalAsync(
    sessionId,
    new DurableApprovalDecision
    {
        RequestId = pending.RequestId,
        Approved  = true,
        Reason    = "Reviewed and approved by operations team."
    });

Console.WriteLine($"Decision submitted. Approved={decision.Approved}");
```

`SubmitApprovalAsync` unblocks the tool in the workflow, and `RequestApprovalAsync` in the tool returns the same
`DurableApprovalDecision`.

---

## Scheduling

Four primitives cover every proactive agent invocation pattern. They all run `AgentJobWorkflow` —
a lightweight, fire-and-forget workflow with no conversation history, no StateBag, and no TTL loop.
Results are visible in the Temporal Web UI; to capture output, start a regular agent session from
inside the job using `TemporalAgentContext`.

| Primitive                                         | Context           | Recurrence              |
|---------------------------------------------------|-------------------|-------------------------|
| `AddScheduledAgentRun`                            | Config time       | Recurring               |
| `ITemporalAgentClient.ScheduleAgentAsync`         | Runtime           | Recurring               |
| `ScheduleActivities.ScheduleOneTimeAgentRunAsync` | Inside a workflow | One-time                |
| `ITemporalAgentClient.RunAgentDelayedAsync`       | External caller   | One-time (full session) |

### Recurring Schedules

#### Config-time registration

Declare scheduled runs inside `AddTemporalAgents`. The `ScheduleRegistrationService` creates them
automatically when the worker starts. If the schedule already exists (e.g., on subsequent restarts)
a warning is logged and the existing schedule is left untouched.

```csharp
builder.Services
    .AddHostedTemporalWorker("localhost:7233", "default", "agents")
    .AddTemporalAgents(opts =>
    {
        opts.AddDurableAgent("SummaryAgent", agent =>
        {
            agent.Instructions = "Summarize the day's activity report.";
            agent.ChatClient   = sp => sp.GetRequiredService<IChatClient>();
        });

        opts.AddScheduledAgentRun(
            agentName: "SummaryAgent",
            scheduleId: "daily-summary",
            request: new RunRequest("Summarize today's activity report."),
            spec: new ScheduleSpec
            {
                Intervals = [new ScheduleIntervalSpec(Every: TimeSpan.FromDays(1))]
            });
    });
```

#### Programmatic scheduling

Call `ScheduleAgentAsync` at any time to create a Temporal Schedule. The returned `ScheduleHandle`
lets you pause, trigger, update, or delete the schedule:

```csharp
ITemporalAgentClient client = // resolved from DI

ScheduleHandle handle = await client.ScheduleAgentAsync(
    agentName: "ReportAgent",
    scheduleId: "weekly-report",
    request: new RunRequest("Generate the weekly metrics report."),
    spec: new ScheduleSpec
    {
        Calendars =
        [
            new ScheduleCalendarSpec { Hour = [new ScheduleRange(9)], DayOfWeek = [new ScheduleRange(1)] }
        ]
    });

// Trigger immediately (outside the normal cadence)
await handle.TriggerAsync();

// Pause and resume
await handle.PauseAsync(note: "Pausing during maintenance window.");
await handle.UnpauseAsync();

// Retrieve an existing handle by ID
ScheduleHandle existing = client.GetAgentScheduleHandle("weekly-report");
await existing.DeleteAsync();
```

> **Schedule orphaning**: Temporal Schedules are independent of workers. Removing an agent from
> `TemporalAgentsOptions` does **not** delete its schedule — it will keep firing. Always call
> `DeleteAsync()` via `GetAgentScheduleHandle` when decommissioning a scheduled agent.

> **Config drift**: if you change a schedule's spec in code, the change is silently skipped on
> restart (the existing schedule is kept). To apply the updated spec, delete the schedule first via
> `GetAgentScheduleHandle`, then restart the worker.

---

### Deferred One-Time Runs

#### From inside an orchestrating workflow

Use `ScheduleActivities.ScheduleOneTimeAgentRunAsync` to schedule a future agent run from within a
`[WorkflowRun]` method. This uses Temporal's `StartDelay` — a single workflow execution is created
with a delayed start, leaving no persistent schedule entity behind after it completes.

```csharp
[Workflow]
public class ResearchWorkflow
{
    [WorkflowRun]
    public async Task RunAsync(string topic)
    {
        // Run the main analysis immediately
        var analyst = TemporalWorkflowExtensions.GetAgent("AnalystAgent");
        var session = await analyst.CreateSessionAsync();
        await analyst.RunAsync($"Analyze: {topic}", session);

        // Schedule a follow-up comparison in 7 days — fire-and-forget, no blocking
        await Workflow.ExecuteActivityAsync(
            (ScheduleActivities a) => a.ScheduleOneTimeAgentRunAsync(new OneTimeAgentRun
            {
                AgentName = "AnalystAgent",
                RunId     = $"followup-{topic}",
                Request   = new RunRequest($"Compare today's findings on '{topic}' against last week's."),
                RunAt     = Workflow.UtcNow + TimeSpan.FromDays(7)
            }),
            new ActivityOptions { StartToCloseTimeout = TimeSpan.FromSeconds(30) });
    }
}
```

The activity is idempotent on retry: `WorkflowIdConflictPolicy.UseExisting` ensures that a second
`StartWorkflowAsync` call (after a crash-before-ack) finds the already-scheduled execution and
returns normally. If `RunAt` is in the past when the activity executes, the run starts immediately.

#### From an external caller

`RunAgentDelayedAsync` defers the start of a **full agent session** (`AgentWorkflow`, with
conversation history and StateBag). It is intended for external callers, not workflow code.

```csharp
ITemporalAgentClient client = // resolved from DI

var sessionId = new TemporalAgentSessionId("OnboardingAgent", userId);

// Workflow is created now but does not start executing for 24 hours
await client.RunAgentDelayedAsync(
    sessionId,
    new RunRequest("Welcome! Your trial period has started. How can I help you get set up?"),
    delay: TimeSpan.FromHours(24));
```

> **Known limitation**: if a workflow with the same session ID is already running (`UseExisting`
> policy), `StartDelay` is ignored and the existing workflow is reused immediately. This method
> only applies the delay when starting a brand-new session.

---

## MCP Tool Integration

[Model Context Protocol](https://modelcontextprotocol.io/) tool servers expose `AIFunction`-shaped tools (`McpClientTool : AIFunction`) that plug into a durable agent's tool list. Because tool factories on `DurableAgentBuilder` are evaluated lazily at first activity dispatch, you can connect to the MCP server inside a tool factory using a singleton-cached `McpClient`.

Add the `ModelContextProtocol` NuGet package to your project, then register the MCP client and tools:

```csharp
// 1. Register the MCP client as a singleton — one connection per worker process.
builder.Services.AddSingleton(async sp =>
    await McpClientFactory.CreateAsync(
        new SseServerTransport("http://localhost:3000/sse")));
builder.Services.AddSingleton<IReadOnlyList<AIFunction>>(sp =>
{
    var mcp = sp.GetRequiredService<Task<McpClient>>().GetAwaiter().GetResult();
    return mcp.ListToolsAsync().GetAwaiter().GetResult().Cast<AIFunction>().ToList();
});

builder.Services.AddChatClient(openAiClient.GetChatClient("gpt-4o").AsIChatClient());

builder.Services
    .AddHostedTemporalWorker("localhost:7233", "default", "agents")
    .AddTemporalAgents(opts =>
    {
        opts.AddDurableAgent("McpAgent", agent =>
        {
            agent.Instructions = "You can call the configured MCP tools.";
            agent.ChatClient   = sp => sp.GetRequiredService<IChatClient>();

            // Resolve MCP tools at first dispatch — the singleton above caches the client.
            // Each tool factory below registers one tool against the agent's per-agent tool registry.
            // For a non-trivial number of tools, prefer enumerating ListToolsAsync() during
            // host construction and calling agent.AddTool(...) per discovered tool there.
        });
    });
```

For very large MCP catalogs, register the discovered tools at host-construction time (before `builder.Build()`) and call `agent.AddTool(tool)` on the builder for each one — the builder accepts concrete `AIFunction` instances directly via the `AddTool(AIFunction, ...)` and `AddTools(params AIFunction[])` overloads.

---

## External Memory with AIContextProvider

`AIContextProvider` instances run before each inference call inside `AgentActivities`. They allow external memory providers (such as [Mem0](https://mem0.ai/)) to inject relevant context from previous conversations automatically, with no additional Temporal code required.

`AgentSessionStateBag` state — including provider-managed state such as Mem0 thread IDs — is serialized and carried across continue-as-new boundaries automatically.

Register a provider on the builder via `AddContextProvider`:

```csharp
builder.Services.AddSingleton<Mem0ContextProvider>(sp =>
    new Mem0ContextProvider(sp.GetRequiredService<Mem0Client>(), userId: "user-001"));

builder.Services.AddChatClient(chatClient);

builder.Services
    .AddHostedTemporalWorker("localhost:7233", "default", "agents")
    .AddTemporalAgents(opts =>
    {
        opts.AddDurableAgent("MemoryAgent", agent =>
        {
            agent.Instructions = "You are a helpful assistant with long-term memory.";
            agent.ChatClient   = sp => sp.GetRequiredService<IChatClient>();

            // DI-resolved context provider — runs once per worker per agent (cached).
            agent.AddContextProvider(sp => sp.GetRequiredService<Mem0ContextProvider>());
        });
    });
```

`AddContextProvider` has two overloads — `AddContextProvider(AIContextProvider)` for concrete instances and `AddContextProvider(Func<IServiceProvider, AIContextProvider>)` for DI factories. The factory runs once at first activity dispatch; the resolved instance is cached for the lifetime of the worker process and shared across all sessions on that worker. Treat provider fields as effectively read-only after construction; per-session state goes through MAF's `StateBag`.

In durable mode, `InvokingAsync` and `InvokedAsync` fire **once per LLM call** (per `RunDurableAgentStep` activity), not once per turn — make these hooks idempotent and cheap, or cache results via `StateBag` to skip redundant work within a turn.

For a deep dive into how StateBag persistence works, see [Session StateBag & Context Providers](../architecture/MAF/session-statebag-and-context-providers.md).

---

## External History Store

For regulated workloads (HIPAA, PCI) or long-running sessions where Temporal event size becomes a concern, configure an `IAgentHistoryStore` factory to keep conversation history in a backend you control instead of in Temporal's event log.

| Slot | Type | Default | What it controls |
|---|---|---|---|
| `opts.HistoryStore` | `Func<IServiceProvider, IAgentHistoryStore>?` | `null` | Worker-level default factory. When non-null, every durable agent on this worker that doesn't override uses this store. |
| `agent.HistoryStore` | `Func<IServiceProvider, IAgentHistoryStore>?` | `null` | Per-agent override. Wins over `opts.HistoryStore` for this one agent. |

Presence of a non-null factory is the opt-in — there is no boolean flag.

```csharp
builder.Services.AddSingleton<MyCosmosHistoryStore>();
builder.Services.AddChatClient(chatClient);

builder.Services
    .AddHostedTemporalWorker("localhost:7233", "default", "agents")
    .AddTemporalAgents(opts =>
    {
        opts.HistoryStore = sp => sp.GetRequiredService<MyCosmosHistoryStore>();

        opts.AddDurableAgent("MyAgent", agent =>
        {
            agent.ChatClient = sp => sp.GetRequiredService<IChatClient>();
        });
    });
```

When opted in, conversation messages no longer appear in `ActivityScheduled` event payloads, and `GetHistoryAsync()` returns metadata-only entries (callers should query the store directly for full content). For the full how-to including per-agent override patterns, the relationship to `AIContextProvider` / `ChatHistoryProvider`, migration behavior, and a reference store implementation, see [External History Store](./external-history-store.md).

---

## Per-Tool Activity Configuration

Every tool registered via `agent.AddTool(...)` is dispatched as a Temporal activity (`Temporalio.Extensions.Agents.InvokeAgentTool`). The default policy is the worker-level `opts.DefaultRetryPolicy` (or Temporal SDK defaults when null). Override per tool via the `configure` callback on `AddTool`.

| Property / Method on `DurableToolOptions` | Purpose |
|---|---|
| `StartToCloseTimeout` | Per-tool activity timeout. `null` inherits worker default. |
| `HeartbeatTimeout` | Per-tool heartbeat timeout. `null` inherits worker default. |
| `RetryPolicy` | Per-tool retry policy. `null` inherits worker default. |
| `NoRetry()` | Sugar for `RetryPolicy = new() { MaximumAttempts = 1 }`. Use on write tools. |
| `WithMaxAttempts(int n)` | Sugar for fixed-attempt retry. |
| `WithTimeout(TimeSpan t)` | Sugar for `StartToCloseTimeout`. |

`agent.MaxToolCallsPerTurn` (default `20` when not set) caps step-loop iterations per single agent turn. The value propagates from the agent's registration into session-based workflows, scheduled jobs, and sub-agent calls via `GetAgent()` — you configure it once on the builder and it takes effect everywhere. When exceeded, the workflow returns a structured "iteration cap exceeded" assistant message rather than letting workflow history grow unbounded.

```csharp
builder.Services
    .AddHostedTemporalWorker("localhost:7233", "default", "agents")
    .AddTemporalAgents(opts =>
    {
        opts.AddDurableAgent("SupportAgent", agent =>
        {
            agent.Instructions = "You help customers with support requests.";
            agent.ChatClient   = sp => sp.GetRequiredService<IChatClient>();

            // Read tool — inherits the worker default retry policy.
            agent.AddTool(lookupOrderTool);

            // Write tool — bind NoRetry() to the AIFunction reference. Cannot mistype the name.
            agent.AddTool(sendEmailTool, opts => opts.NoRetry().WithTimeout(TimeSpan.FromSeconds(30)));

            agent.MaxToolCallsPerTurn = 10;
        });
    });
```

For the canonical write-vs-read tool example, the per-tool retry hierarchy, and what the Temporal Web UI shows, see [Durable Agents](./durable-agents.md).

---

## OpenTelemetry Integration

The library emits two layers of spans that compose with the Temporal SDK's own tracing interceptor.

### Setup

Install `Temporalio.Extensions.OpenTelemetry` alongside your preferred OTel exporter, then register both the Temporal
tracing interceptor and the agent activity source:

```csharp
using OpenTelemetry.Trace;
using Temporalio.Extensions.OpenTelemetry;
using Temporalio.Extensions.Agents;

// 1. Configure the OTel tracer provider with all relevant sources
using var tracerProvider = Sdk.CreateTracerProviderBuilder()
    .AddSource(
        TracingInterceptor.ClientSource.Name,      // Temporal client spans (StartWorkflow, etc.)
        TracingInterceptor.WorkflowsSource.Name,   // Temporal workflow spans
        TracingInterceptor.ActivitiesSource.Name,  // Temporal activity spans (RunActivity)
        TemporalAgentTelemetry.ActivitySourceName) // "Temporalio.Extensions.Agents"
    .AddOtlpExporter()
    .Build();

// 2. Add the tracing interceptor to the Temporal client
builder.Services.AddTemporalClient(opts =>
{
    opts.TargetHost  = "localhost:7233";
    opts.Interceptors = new[] { new TracingInterceptor() };
});

builder.Services
    .AddHostedTemporalWorker("agents")
    .AddTemporalAgents(opts =>
    {
        opts.AddDurableAgent("MyAgent", agent =>
        {
            agent.ChatClient = sp => sp.GetRequiredService<IChatClient>();
        });
    });
```

### Span Hierarchy

A single `RunAsync` call produces a two-level span tree:

```
agent.client.send          (DefaultTemporalAgentClient — before the Update reaches Temporal)
  └── StartWorkflow / RunActivity   (Temporal SDK spans via TracingInterceptor)
        └── agent.turn     (AgentActivities.RunDurableAgentStepAsync — inside the activity)
```

| Span                | Source                                      | Key Attributes                                                                                                                  |
|---------------------|---------------------------------------------|---------------------------------------------------------------------------------------------------------------------------------|
| `agent.client.send` | `TemporalAgentTelemetry.ActivitySourceName` | `agent.name`, `agent.session_id`                                                                                                |
| `agent.turn`        | `TemporalAgentTelemetry.ActivitySourceName` | `agent.name`, `agent.session_id`, `agent.correlation_id`, `agent.input_tokens`, `agent.output_tokens`, `agent.total_tokens`    |
| SDK spans           | `TracingInterceptor.*Source`                | Standard Temporal attributes                                                                                                    |

The span name constants are available on `TemporalAgentTelemetry`:

```csharp
TemporalAgentTelemetry.ActivitySourceName    // "Temporalio.Extensions.Agents"
TemporalAgentTelemetry.AgentTurnSpanName     // "agent.turn"
TemporalAgentTelemetry.AgentClientSendSpanName // "agent.client.send"
```

### Search Attributes

Search attribute upserts are **opt-in** — set `EnableSearchAttributes = true` in `TemporalAgentsOptions` to enable them:

```csharp
builder.Services
    .AddHostedTemporalWorker("localhost:7233", "default", "agents")
    .AddTemporalAgents(opts =>
    {
        opts.AddDurableAgent("Agent", a => a.ChatClient = sp => sp.GetRequiredService<IChatClient>());
        opts.EnableSearchAttributes = true;
    });
```

When enabled, `AgentWorkflow` upserts three [custom search attributes](https://docs.temporal.io/visibility#custom-search-attributes)
on each workflow, enabling operational queries in the Temporal Web UI and via `ListWorkflowsAsync`:

| Attribute          | Type           | Description                                            |
|--------------------|----------------|--------------------------------------------------------|
| `AgentName`        | Keyword        | The registered agent name                              |
| `SessionCreatedAt` | DateTimeOffset | When the workflow first started                        |
| `TurnCount`        | Long           | Number of completed agent responses in this session    |

Example queries in the Temporal UI:

```
AgentName = "BillingAgent" AND TurnCount > 10
SessionCreatedAt > "2026-03-01T00:00:00Z"
```

> **Note:** Custom search attributes must be registered with the Temporal server before use.
> With `temporal server start-dev` they are created automatically. For production clusters, register
> them via the CLI:
>
> ```bash
> temporal operator search-attribute create --name AgentName --type Keyword
> temporal operator search-attribute create --name SessionCreatedAt --type Datetime
> temporal operator search-attribute create --name TurnCount --type Int
> ```
