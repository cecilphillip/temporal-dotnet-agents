# Temporalio.Extensions.Agents

A [Temporal](https://temporal.io/) integration for the [Microsoft Agent Framework](https://github.com/microsoft/agents) (`Microsoft.Agents.AI`). This library provides durable, stateful AI agent sessions backed by Temporal workflows.

## Why Temporal?

Temporal provides:
- **Request/Response**: `[WorkflowUpdate]` — direct response, no polling
- **Long sessions**: Continue-as-new transfers history to a fresh run
- **Observability**: Full Temporal Web UI, event history, tracing
- **Multi-agent**: First-class workflow orchestration

## Feature Overview

| Feature | Description |
|---|---|
| Durable sessions | Each agent session maps to a long-lived Temporal workflow; history survives worker restarts |
| Multi-turn conversations | Conversation history is stored in workflow state and replayed automatically |
| LLM-powered routing | `IAgentRouter` / `LlmAgentRouter` classifies messages and dispatches to the best-matching agent |
| Parallel agent execution | `ExecuteAgentsInParallelAsync` runs multiple agents concurrently inside a workflow |
| Human-in-the-loop approval | Tools can pause and request human review; external systems submit decisions |
| MCP tool integration | Async agent factory connects to a Model Context Protocol server at startup |
| External memory (AIContextProvider) | `ChatClientAgent.ContextProviders` runs before inference; session state persists across turns |
| OpenTelemetry distributed tracing | Two-layer span hierarchy covering SDK internals and agent turns |
| Streaming responses | `IAgentResponseHandler` receives streaming chunks for SSE or SignalR push |

## How It Works

Each agent session maps to a long-lived Temporal **workflow** (`AgentWorkflow`). When an external caller sends a message, it uses a Temporal **Update** — a durable, acknowledged request/response primitive — to deliver the message and receive the agent's response in a single call. All AI inference runs inside Temporal **activities**, preserving determinism.

```
External Caller
    │
    │  ExecuteUpdateAsync (RunRequest)
    ▼
AgentWorkflow (long-lived workflow)
    │
    │  ExecuteActivityAsync
    ▼
AgentActivities.ExecuteAgentAsync
    │
    └─► Real AIAgent (e.g., ChatClientAgent backed by Azure OpenAI)
```

## Setup

### 1. Register Temporal Agents

#### Fluent Builder API (Recommended)

Use `AddTemporalAgents` on the worker builder. This follows the same pattern as other Temporal SDK extensions and is fully composable with other worker configuration:

```csharp
using Microsoft.Agents.AI;
using Temporalio.Extensions.Agents;
using Temporalio.Extensions.Hosting;

var builder = WebApplication.CreateBuilder(args);

var chatAgent = new ChatClientAgent(chatClient, "MyAgent")
{
    Instructions = "You are a helpful assistant."
};

builder.Services
    .AddHostedTemporalWorker("localhost:7233", "default", "agents")
    .AddTemporalAgents(opts =>
    {
        opts.AddAIAgent(chatAgent, timeToLive: TimeSpan.FromHours(24));
    });
```

`AddTemporalAgents` registers:
- An `ITemporalAgentClient` for sending messages to agent workflows
- A hosted Temporal worker running `AgentWorkflow` + `AgentActivities`
- A keyed `AIAgent` proxy singleton for each registered agent
- An `IAgentRouter` (when a router agent is configured — see [LLM-Powered Routing](#llm-powered-routing))

When you need to control the Temporal client separately (e.g., split worker/client processes):

```csharp
// Register the client once
services.AddTemporalClient("localhost:7233", "default");

// Configure the worker with full composability
services.AddHostedTemporalWorker("agents")
    .AddTemporalAgents(opts => opts.AddAIAgent(agent))
    .ConfigureOptions(opts => opts.MaxConcurrentActivities = 20);
```

#### Legacy API

`ConfigureTemporalAgents` is still supported for backwards compatibility. It couples client setup, worker setup, and agent setup into a single call:

```csharp
builder.Services.ConfigureTemporalAgents(
    configure: options =>
    {
        options.AddAIAgent(chatAgent, timeToLive: TimeSpan.FromHours(24));
    },
    taskQueue: "agents",
    targetHost: "localhost:7233",   // omit if you register ITemporalClient yourself
    @namespace: "default");
```

### 2. Access the Agent Proxy

Inject the proxy by name from DI, or resolve it from the service provider:

```csharp
// Option A — resolve from IServiceProvider
AIAgent proxy = services.GetTemporalAgentProxy("MyAgent");

// Option B — inject with a keyed service
public class MyController(
    [FromKeyedServices("MyAgent")] AIAgent agentProxy)
{ ... }
```

## Usage Examples

### Sending a Message (External Caller)

```csharp
// Create (or resume) a session
AgentSession session = await agentProxy.CreateSessionAsync();

// Send a message and get a response
AgentResponse response = await agentProxy.RunAsync("Hello, agent!", session);

Console.WriteLine(response.Messages[0].Text);
```

The session ID encodes the agent name and a unique key as a Temporal workflow ID (`ta-myagent-{key}`). Passing the same session across calls routes all messages to the same `AgentWorkflow` instance, preserving conversation history.

### Multi-Turn Conversation

```csharp
var session = await agentProxy.CreateSessionAsync();

var r1 = await agentProxy.RunAsync("What is the capital of France?", session);
Console.WriteLine(r1.Messages[0].Text);  // Paris

var r2 = await agentProxy.RunAsync("What is its population?", session);
Console.WriteLine(r2.Messages[0].Text);  // ~2.1 million (context preserved)
```

### Fire-and-Forget

For notifications or background tasks where you don't need to wait for the agent's response:

```csharp
var options = new TemporalAgentRunOptions { IsFireAndForget = true };
await agentProxy.RunAsync("Process this in the background.", session, options);
// Returns immediately with an empty AgentResponse
```

### Structured Output

Request a JSON response conforming to a specific schema:

```csharp
var options = new TemporalAgentRunOptions
{
    ResponseFormat = ChatResponseFormat.ForJsonSchema<WeatherReport>()
};

var session = await agentProxy.CreateSessionAsync();
var response = await agentProxy.RunAsync("What's the weather in Seattle?", session, options);
var report = response.Messages[0].GetContent<WeatherReport>();
```

### Tool Filtering

Restrict which tools the agent may use for a specific request:

```csharp
var options = new TemporalAgentRunOptions
{
    EnableToolNames = ["get_weather", "search_web"],
    // EnableToolCalls = false  // disable all tools for this request
};

var response = await agentProxy.RunAsync("Look up the latest news.", session, options);
```

## Agent Orchestration (Inside Temporal Workflows)

Use `TemporalWorkflowExtensions.GetAgent` to interact with agents from within an orchestrating Temporal workflow. The agent's conversation history is stored in the workflow's event history and replayed automatically.

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

`TemporalAIAgent` (returned by `GetAgent`) stores the conversation history as workflow state. This means it survives worker restarts, supports retries, and is durable by design — all without any extra persistence code.

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

## Session TTL

Sessions expire after the configured TTL (default: 14 days). Configure per-agent overrides:

```csharp
options.AddAIAgentFactory(
    name: "ShortLivedAgent",
    factory: sp => sp.GetRequiredService<MyCustomAgent>(),
    timeToLive: TimeSpan.FromHours(1));

// Or configure the default for all agents
options.DefaultTimeToLive = TimeSpan.FromDays(7);
```

When the TTL elapses, the `AgentWorkflow` completes naturally. The next message to that session ID starts a fresh workflow run.

## Activity Timeouts

Every agent turn — one call to `RunAsync` — executes inside a Temporal activity. Two timeouts govern that activity:

| Option | Default | What it limits |
|---|---|---|
| `ActivityStartToCloseTimeout` | 30 minutes | Total wall-clock time for one turn, including tool calls and retries |
| `ActivityHeartbeatTimeout` | 5 minutes | Maximum gap between heartbeats; Temporal retries the activity if exceeded (most relevant when streaming) |

Both are nullable `TimeSpan?` on `TemporalAgentsOptions`. When `null`, the workflow falls back to the defaults shown above.

```csharp
builder.Services
    .AddHostedTemporalWorker("localhost:7233", "default", "agents")
    .AddTemporalAgents(opts =>
    {
        opts.AddAIAgent(agent, timeToLive: TimeSpan.FromHours(1));

        // Increase for slow models or long tool-call chains
        opts.ActivityStartToCloseTimeout = TimeSpan.FromMinutes(10);

        // Increase if streaming heartbeats arrive slowly
        opts.ActivityHeartbeatTimeout = TimeSpan.FromMinutes(2);
    });
```

### Activity Timeouts for In-Workflow Agents

When using `TemporalWorkflowExtensions.GetAgent` inside an orchestrating workflow, pass `ActivityOptions` directly at the call site:

```csharp
var researcher = TemporalWorkflowExtensions.GetAgent(
    "ResearcherAgent",
    activityOptions: new ActivityOptions
    {
        StartToCloseTimeout = TimeSpan.FromMinutes(5),
        HeartbeatTimeout    = TimeSpan.FromMinutes(1)
    });
```

## Accessing Temporal from Agent Tools

Agent tools executing inside `AgentActivities.ExecuteAgentAsync` can access Temporal capabilities through `TemporalAgentContext.Current`:

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

    public ValueTask OnAgentResponseAsync(AgentResponse message, CancellationToken ct)
        => ValueTask.CompletedTask;
}
```

## Core Components

- **`AgentWorkflow`** — Long-lived workflow with `[WorkflowUpdate]` for request/response
- **`TemporalAIAgent`** — For use inside Temporal workflows
- **`TemporalAIAgentProxy`** — For external callers
- **`TemporalAgentContext`** — Async-local context for tools
- **`ITemporalAgentClient`** — Update-based client (no polling)
- **`TemporalAgentSessionId`** — Encodes agent name + key
- **`AddTemporalAgents(...)`** — Fluent worker builder registration (recommended)
- **`ConfigureTemporalAgents(...)`** — Legacy one-shot service registration

---

## LLM-Powered Routing

When multiple agents are registered, `IAgentRouter` / `LlmAgentRouter` can classify an incoming message and dispatch it to the best-matching agent automatically.

### Configuration

Register descriptors for each routable agent, then set a lightweight router agent whose sole job is to return the name of the best match:

```csharp
var routerChatClient = openAiClient.GetChatClient("gpt-4o-mini")
    .AsIChatClient();

var routerAgent = new ChatClientAgent(routerChatClient, "Router")
{
    Instructions = "You are a routing assistant. Given a list of agents and a user message, " +
                   "respond with ONLY the name of the most appropriate agent. Nothing else."
};

builder.Services
    .AddHostedTemporalWorker("localhost:7233", "default", "agents")
    .AddTemporalAgents(opts =>
    {
        opts.AddAIAgent(weatherAgent);
        opts.AddAIAgent(bookingAgent);

        // Describe each agent so the router can classify messages
        opts.AddAgentDescriptor("WeatherAgent", "Handles weather queries and forecasts");
        opts.AddAgentDescriptor("BookingAgent", "Handles travel bookings and reservations");

        // LlmAgentRouter is registered automatically when a router agent is set
        opts.SetRouterAgent(routerAgent);
    });
```

### Using RouteAsync

Call `ITemporalAgentClient.RouteAsync` instead of targeting a specific agent. The router classifies the messages, selects the best agent, and runs it — all in one call:

```csharp
ITemporalAgentClient client = // resolved from DI

var messages = new List<ChatMessage>
{
    new(ChatRole.User, "What will the weather be like in Boston tomorrow?")
};

AgentResponse response = await client.RouteAsync(
    sessionKey: userId,
    new RunRequest(messages));
```

The `sessionKey` is used to construct a `TemporalAgentSessionId` from the chosen agent name and that key, so the same user always routes to the same session for a given agent.

`LlmAgentRouter` uses a fuzzy-match fallback to tolerate minor formatting variation in the model's output. To use a custom routing strategy, implement `IAgentRouter` and register it in DI before calling `AddTemporalAgents`.

---

## Parallel Agent Execution

`TemporalWorkflowExtensions.ExecuteAgentsInParallelAsync` dispatches multiple agent calls concurrently inside a workflow using `Workflow.WhenAllAsync` — the workflow-safe equivalent of `Task.WhenAll`.

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

Results are returned in the same order as the input tuples. Each agent runs inside its own activity and the workflow waits for all of them before continuing.

---

## Human-in-the-Loop (HITL) Approval Gates

Agent tools can pause mid-turn and wait for a human decision before proceeding. The backing `AgentWorkflow` exposes a `[WorkflowUpdate]` for both sides of this interaction.

### Requesting Approval (Inside a Tool)

Call `TemporalAgentContext.Current.RequestApprovalAsync` from inside a tool implementation. The call blocks the activity until a human submits a decision:

```csharp
public class DataDeletionTool
{
    [Description("Deletes all records for the specified user")]
    public static async Task<string> DeleteUserDataAsync(string userId)
    {
        var ticket = await TemporalAgentContext.Current.RequestApprovalAsync(
            new ApprovalRequest
            {
                Action  = "Delete all data for user",
                Details = $"userId={userId}. This action is irreversible."
            });

        if (!ticket.Approved)
        {
            return $"Action rejected by reviewer: {ticket.Comment}";
        }

        // Proceed with deletion...
        return $"Data for user {userId} has been deleted.";
    }
}
```

Because the tool runs inside a Temporal activity, the pause is fully durable. If the worker restarts while waiting for approval, the activity resumes from exactly the same point once a new worker picks it up.

Set `ActivityStartToCloseTimeout` to a value that exceeds your expected review time:

```csharp
opts.ActivityStartToCloseTimeout = TimeSpan.FromHours(24);
```

### Checking for Pending Approvals (External System)

Poll the workflow from a UI, monitoring tool, or approval service:

```csharp
ITemporalAgentClient client = // resolved from DI
var sessionId = new TemporalAgentSessionId("MyAgent", userId);

ApprovalRequest? pending = await client.GetPendingApprovalAsync(sessionId);

if (pending is not null)
{
    Console.WriteLine($"Pending approval: {pending.Action}");
    Console.WriteLine($"Details: {pending.Details}");
    Console.WriteLine($"RequestId: {pending.RequestId}");
}
```

### Submitting a Decision (External System)

```csharp
ApprovalTicket ticket = await client.SubmitApprovalAsync(
    sessionId,
    new ApprovalDecision
    {
        RequestId = pending.RequestId,
        Approved  = true,
        Comment   = "Reviewed and approved by operations team."
    });

Console.WriteLine($"Decision submitted. Approved={ticket.Approved}");
```

`SubmitApprovalAsync` unblocks the tool in the workflow, and `RequestApprovalAsync` in the tool returns the same `ApprovalTicket`.

---

## MCP Tool Integration

The async `AddAIAgentFactory` overload supports setup that requires async work at startup, such as connecting to a [Model Context Protocol](https://modelcontextprotocol.io/) server and listing its tools. Add the `ModelContextProtocol` NuGet package to your project.

```csharp
builder.Services
    .AddHostedTemporalWorker("localhost:7233", "default", "agents")
    .AddTemporalAgents(opts =>
    {
        opts.AddAIAgentFactory("McpAgent", async sp =>
        {
            var mcpClient = await McpClientFactory.CreateAsync(
                new SseServerTransport("http://localhost:3000/sse"));

            // McpClientTool implements AIFunction (MEAI-native) — no adapter needed
            var mcpTools = await mcpClient.ListToolsAsync();

            return openAiClient.GetChatClient("gpt-4o")
                .AsAIAgent("McpAgent", tools: [.. mcpTools]);
        });
    });
```

The async factory is invoked once during worker startup (blocking is safe during DI container construction, not on hot paths). After startup the agent instance is cached and reused for every session.

---

## External Memory with AIContextProvider

`ChatClientAgent.ContextProviders` runs before each inference call inside `AgentActivities.ExecuteAgentAsync`. This allows external memory providers (such as [Mem0](https://mem0.ai/)) to inject relevant context from previous conversations automatically, with no additional Temporal code required.

`AgentSessionStateBag` state — including provider-managed state such as Mem0 thread IDs — is serialized and carried across continue-as-new boundaries automatically.

```csharp
var mem0Provider = new Mem0ContextProvider(mem0Client, userId: "user-001");

var agent = new ChatClientAgent(chatClient, "MemoryAgent")
{
    Instructions   = "You are a helpful assistant with long-term memory.",
    ContextProviders = [mem0Provider]
};

builder.Services
    .AddHostedTemporalWorker("localhost:7233", "default", "agents")
    .AddTemporalAgents(opts =>
    {
        opts.AddAIAgent(agent);
    });
```

Each turn the provider injects previously stored memories into the prompt; after the turn it can persist new memories. The `AgentSessionStateBag` stores any state the provider needs to resume in a future turn (e.g., thread identifiers), and that bag is serialized inside `AgentWorkflow` so it survives worker restarts and continue-as-new transitions.

---

## OpenTelemetry Integration

The library emits two layers of spans that compose with the Temporal SDK's own tracing interceptor.

### Setup

Install `Temporalio.Extensions.OpenTelemetry` alongside your preferred OTel exporter, then register both the Temporal tracing interceptor and the agent activity source:

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
    .AddTemporalAgents(opts => opts.AddAIAgent(agent));
```

### Span Hierarchy

A single `RunAsync` call produces a two-level span tree:

```
agent.client.send          (DefaultTemporalAgentClient — before the Update reaches Temporal)
  └── StartWorkflow / RunActivity   (Temporal SDK spans via TracingInterceptor)
        └── agent.turn     (AgentActivities.ExecuteAgentAsync — inside the activity)
```

| Span | Source | Key Attributes |
|---|---|---|
| `agent.client.send` | `TemporalAgentTelemetry.ActivitySourceName` | `agent.name`, `agent.session_id`, `agent.correlation_id` |
| `agent.turn` | `TemporalAgentTelemetry.ActivitySourceName` | `agent.name`, `agent.session_id`, `agent.input_tokens`, `agent.output_tokens`, `agent.total_tokens` |
| SDK spans | `TracingInterceptor.*Source` | Standard Temporal attributes |

The span name constants are available on `TemporalAgentTelemetry`:

```csharp
TemporalAgentTelemetry.ActivitySourceName    // "Temporalio.Extensions.Agents"
TemporalAgentTelemetry.AgentTurnSpanName     // "agent.turn"
TemporalAgentTelemetry.AgentClientSendSpanName // "agent.client.send"
```
