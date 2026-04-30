# Workflow Orchestration: Agent as Sub-Agent

## Overview

Shows how to call an AI agent from inside a custom Temporal workflow using `TemporalWorkflowExtensions.GetAgent()`. The workflow itself is the orchestrator — it starts the agent, passes a question, and returns the result as its final output.

This sample demonstrates:
- A custom `[Workflow]` (`WeatherOrchestrationWorkflow`) driving an agent call
- `GetAgent("WeatherAssistant")` resolving a registered agent by name inside workflow code
- Deterministic agent execution via `Workflow.ExecuteActivityAsync()` under the hood
- A weather tool with `UseFunctionInvocation()` middleware inside the sub-agent

## Highlights

- **Workflow as orchestrator, not just session container.** Unlike the external-caller pattern (where a client sends updates to `AgentWorkflow`), here a custom workflow controls the agent lifecycle and composes its output into a larger result.
- **Replay safety.** `GetAgent().RunAsync()` dispatches through `Workflow.ExecuteActivityAsync()`. After the activity completes, its result is cached in event history — a worker restart replays the cached value and never re-calls the LLM.
- **Composability.** Additional agents, activities, signals, or timers can be added to `WeatherOrchestrationWorkflow.RunAsync` alongside the agent call — standard Temporal workflow composition applies.
- **`.AddWorkflow<T>()` on the same builder.** The orchestrating workflow is registered on the same hosted worker as the agents, keeping the setup to a single `AddHostedTemporalWorker` call.

## Getting Started

### Prerequisites

- [.NET 10 SDK](https://dot.net) or later
- A local Temporal server: `temporal server start-dev`
- An OpenAI-compatible API key

### Configure API credentials

```bash
dotnet user-secrets set "OPENAI_API_KEY" "sk-..." --project samples/MAF/WorkflowOrchestration
dotnet user-secrets set "OPENAI_API_BASE_URL" "https://api.openai.com/v1" --project samples/MAF/WorkflowOrchestration
```

### Run

```bash
dotnet run --project samples/MAF/WorkflowOrchestration/WorkflowOrchestration.csproj
```

### Expected Output

```
Worker started. Submitting orchestration workflow...

Orchestration workflow started: weather-orchestration-<guid>

Orchestration workflow result: The weather is currently sunny.

Done.
```
