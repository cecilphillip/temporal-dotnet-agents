# Temporalio.Extensions.Agents

A [Temporal](https://temporal.io/) integration for
the [Microsoft Agent Framework](https://github.com/microsoft/agents) (`Microsoft.Agents.AI`). This library provides
durable, stateful AI agent sessions backed by Temporal workflows.

## Overview

Temporal gives AI agents **durability by default** — every agent session maps to a long-lived workflow whose state
survives process crashes and restarts. Conversation history, tool calls, and even human-in-the-loop approval gates are
all persisted in Temporal's event history and replayed deterministically.

Key benefits over in-memory agent frameworks:

- **Request/Response via `[WorkflowUpdate]`** — direct response, no polling
- **Long sessions** — continue-as-new transfers history to fresh runs automatically
- **Observability** — full Temporal Web UI, event history, and distributed tracing
- **Multi-agent orchestration** — first-class workflow fan-out and routing

## Feature Highlights

- Durable multi-turn conversations with automatic history management
- LLM-powered routing (`IAgentRouter` / `AIModelAgentRouter`)
- Parallel agent execution inside workflows (`ExecuteAgentsInParallelAsync`)
- Human-in-the-loop approval gates via `[WorkflowUpdate]`
- Typed structured output with `RunAsync<T>` (markdown fence stripping + retry)
- Recurring and one-time scheduled agent runs
- MCP tool integration via async agent factory
- External memory with `AIContextProvider` and `AgentSessionStateBag` persistence
- Streaming responses via `IAgentResponseHandler`
- OpenTelemetry distributed tracing (two-layer span hierarchy + search attributes)

## How It Works

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

Each agent session maps to a long-lived Temporal **workflow** (`AgentWorkflow`). When an external caller sends a
message, it uses a Temporal **Update** — a durable, acknowledged request/response primitive — to deliver the message and
receive the agent's response in a single call. All AI inference runs inside Temporal **activities**, preserving
determinism.

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download) or later
- A running [Temporal server](https://docs.temporal.io/cli#start-dev) (`temporal server start-dev`)
- An LLM provider (e.g., Azure OpenAI, OpenAI)

Install the NuGet package:

```bash
dotnet add package Temporalio.Extensions.Agents
```

## Getting Started

### 1. Register an Agent

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

### 2. Send a Message

```csharp
// Resolve the agent proxy from DI
AIAgent proxy = services.GetTemporalAgentProxy("MyAgent");

// Create a session and send a message
var session = await proxy.CreateSessionAsync();
var response = await proxy.RunAsync("Hello, agent!", session);

Console.WriteLine(response.Messages[0].Text);
```

### 3. Run a Sample

```bash
# Start Temporal (in a separate terminal)
temporal server start-dev --namespace default

# Run a sample
dotnet run --project samples/BasicAgent
```

## Samples

| Sample | Description |
|--------|-------------|
| [BasicAgent](../../samples/BasicAgent) | External caller pattern — send messages to an agent from a console app |
| [SplitWorkerClient](../../samples/SplitWorkerClient) | Worker and client in separate processes |
| [WorkflowOrchestration](../../samples/WorkflowOrchestration) | Sub-agent orchestration inside a Temporal workflow |
| [EvaluatorOptimizer](../../samples/EvaluatorOptimizer) | Generator + evaluator loop pattern |
| [MultiAgentRouting](../../samples/MultiAgentRouting) | LLM-powered routing, parallel execution, and OpenTelemetry |
| [HumanInTheLoop](../../samples/HumanInTheLoop) | HITL approval gates via `[WorkflowUpdate]` |

## Core Components

- **`AgentWorkflow`** — Long-lived workflow with `[WorkflowUpdate]` for request/response
- **`AgentJobWorkflow`** — Fire-and-forget workflow for scheduled and deferred runs
- **`TemporalAIAgent`** — For use inside Temporal workflows (via `GetAgent`)
- **`TemporalAIAgentProxy`** — For external callers (via `GetTemporalAgentProxy`)
- **`ITemporalAgentClient`** — Update-based client with routing, scheduling, and HITL support
- **`TemporalAgentContext`** — Async-local context for agent tools running inside activities
- **`StructuredOutputExtensions`** — `RunAsync<T>` with markdown fence stripping and retry

## Documentation

### How-To Guides

- [Usage Guide](../../docs/how-to/usage.md) — structured output, orchestration, HITL, scheduling, OTel, and more
- [Routing Patterns](../../docs/how-to/routing.md) — LLM-powered, static, and dynamic routing
- [Testing Agents](../../docs/how-to/testing-agents.md) — unit and integration testing patterns
- [Observability](../../docs/how-to/observability.md) — OpenTelemetry spans, search attributes, and operational queries
- [Scheduling](../../docs/how-to/scheduling.md) — recurring and one-time agent runs, lifecycle management
- [Structured Output](../../docs/how-to/structured-output.md) — typed responses with `RunAsync<T>`, fence stripping, and retry
- [Human-in-the-Loop](../../docs/how-to/hitl-patterns.md) — approval gates, dashboards, timeouts, and testing
- [History & Token Optimization](../../docs/how-to/prompt-caching.md) — managing conversation history and reducing costs
- [Do's and Don'ts](../../docs/how-to/dos-and-donts.md) — common mistakes and best practices

### Architecture

- [Durability & Determinism](../../docs/architecture/durability-and-determinism.md) — how replay preserves completed agent calls
- [Agent Sessions & Workflow Loop](../../docs/architecture/agent-sessions-and-workflow-loop.md) — session lifecycle, message flow, crash recovery
- [Session StateBag & Context Providers](../../docs/architecture/session-statebag-and-context-providers.md) — AIContextProvider integration and StateBag persistence
- [Pub/Sub & Event-Driven Patterns](../../docs/architecture/pub-sub-and-event-driven.md) — Temporal equivalents of pub/sub fan-out
- [Agent-to-Agent Communication](../../docs/architecture/agent-to-agent-communication.md) — sub-agent calls, parallel fan-out, and cross-workflow signaling

### External References

- [Temporal Documentation](https://docs.temporal.io/)
- [Temporal .NET SDK](https://github.com/temporalio/sdk-dotnet)
- [Microsoft Agent Framework](https://github.com/microsoft/agents)

## License

[MIT](../../LICENSE)
