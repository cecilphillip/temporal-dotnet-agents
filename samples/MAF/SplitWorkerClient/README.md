# Split Worker/Client: Separate Processes

## Overview

Demonstrates the production deployment pattern where the AI worker and the calling client run in separate processes. The Worker owns all AI infrastructure; the Client sends messages over Temporal without any knowledge of the underlying model.

This sample demonstrates:
- Worker process: hosts `AgentWorkflow`, `AgentActivities`, and the `IChatClient`
- Client process: sends messages via `WorkflowUpdate` with no AI credentials required
- Session reuse: reconnect to an existing session from its workflow ID using `TemporalAgentSessionId.Parse`
- `AddTemporalAgentProxies()` for lightweight client-only registration

## Architecture

```
┌─────────────────────────────────────┐     ┌────────────────────────────────────┐
│          Worker Process             │     │          Client Process             │
│                                     │     │                                     │
│  IChatClient (OpenAI)               │     │  No IChatClient, no worker          │
│  AgentWorkflow (durable session)    │◄────│  AddTemporalAgentProxies()          │
│  AgentActivities (AI inference)     │     │  GetTemporalAgentProxy("Assistant") │
│  AddTemporalAgents()                │     │  proxy.RunAsync(...)                │
│                                     │     │                                     │
│  Task queue: "agents"               │     │  Task queue: "agents"               │
└─────────────────────────────────────┘     └────────────────────────────────────┘
              ▲                                              │
              └──────────── Temporal server ────────────────┘
                       WorkflowUpdate: RunAgent
```

## Highlights

- **Credential isolation.** Only the Worker needs an `OPENAI_API_KEY`. The Client connects only to Temporal and never touches the AI backend.
- **Session reuse across client instances.** `TemporalAgentSessionId.Parse(session.ToString())` reconstructs a session handle from its workflow ID string, allowing a second client process (or restart) to continue an existing conversation.
- **`AddTemporalAgentProxies()` for client-only registration.** Registers `ITemporalAgentClient` and named proxies without a worker, activities, or `IChatClient` — exactly what a lightweight caller needs.
- **Task queue coupling.** Worker and Client must agree on the task queue name (`"agents"`). Mismatches result in the client's updates timing out while waiting for a worker to pick them up.

## Getting Started

### Prerequisites

- [.NET 10 SDK](https://dot.net) or later
- A local Temporal server: `temporal server start-dev`
- An OpenAI-compatible API key (Worker only)

### Configure API credentials

```bash
dotnet user-secrets set "OPENAI_API_KEY" "sk-..." --project samples/MAF/SplitWorkerClient/Worker
```

### Run

Start the Worker first, then the Client in a separate terminal:

```bash
# Terminal 1 — Worker
dotnet run --project samples/MAF/SplitWorkerClient/Worker/Worker.csproj

# Terminal 2 — Client
dotnet run --project samples/MAF/SplitWorkerClient/Client/Client.csproj
```

### Expected Output

**Worker terminal:**
```
Agent worker started. Listening on task queue 'agents'...
Start the Client in another terminal, then press Ctrl+C here to stop.
```

**Client terminal:**
```
Session workflow ID: ta-assistant-<guid>

User : What is the capital of France?
Agent: The capital of France is Paris.

User : What is its population?
Agent: Paris has a population of approximately 2.1 million...

User : What's the current weather condition
Agent: The current weather is sunny.

User : Summarize what we discussed.
Agent: We discussed the capital of France (Paris), its population...

Done.
```
