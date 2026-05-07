# Basic Agent: Single-Process Durable Conversation

## Overview

The simplest way to get started with `Temporalio.Extensions.Agents`. A single process registers a Temporal worker and an AI agent, then runs a multi-turn conversation where every exchange is durably recorded in workflow state.

This sample demonstrates:
- Registering an agent with `AddTemporalAgents()` + `opts.AddDurableAgent("name", agent => { ... })`
- Resolving the agent's `IChatClient` via `agent.ChatClient = sp => ...`
- Adding a tool with `agent.AddTool(weatherTool)`
- Opening a session with `CreateSessionAsync()` and sending turns with `RunAsync()`
- Multi-turn context: follow-up questions reference earlier answers without re-sending history

## Highlights

- **WorkflowUpdate as request/response.** Each `RunAsync()` call is a Temporal `[WorkflowUpdate]` — the caller blocks until the agent responds, with no polling loop required.
- **History lives in the workflow.** Conversation context is stored in `AgentWorkflow` state, not in the client. Any process with the session ID can send a follow-up.
- **Tool calls are durable per-tool activities.** `get_weather` executes inside its own `InvokeAgentTool` activity. If the worker restarts between the tool call and the model response, the tool result is replayed from history.
- **Single-process simplicity.** Worker, agent, and caller all live in the same `IHost` — the minimum viable setup before splitting into separate processes.

## Getting Started

### Prerequisites

- [.NET 10 SDK](https://dot.net) or later
- A local Temporal server: `temporal server start-dev`
- An OpenAI-compatible API key

### Configure API credentials

```bash
dotnet user-secrets set "OPENAI_API_KEY" "sk-..." --project samples/MAF/BasicAgent
dotnet user-secrets set "OPENAI_API_BASE_URL" "https://api.openai.com/v1" --project samples/MAF/BasicAgent
```

### Run

```bash
dotnet run --project samples/MAF/BasicAgent/BasicAgent.csproj
```

### Expected Output

```
Worker started. Sending messages...

Session workflow ID: ta-assistant-<guid>

User : What is the capital of France?
Agent: The capital of France is Paris.

User : What is its population?
Agent: Paris has a population of approximately 2.1 million in the city proper...

Done.
```
