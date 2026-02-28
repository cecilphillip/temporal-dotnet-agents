# Workflow Orchestration Sample

This sample demonstrates how to use the new fluent `AddTemporalAgents()` API to register an AI agent and access it from within a Temporal workflow.

## Key Concepts

- **Fluent Builder API**: Uses the new `.AddTemporalAgents()` extension method on `ITemporalWorkerServiceOptionsBuilder` instead of the one-shot `ConfigureTemporalAgents()` call
- **Workflow Orchestration**: A Temporal workflow that internally orchestrates an AI agent as a sub-agent
- **Determinism**: Agent calls are executed via `Workflow.ExecuteActivityAsync()` to preserve workflow determinism
- **Conversation History**: The workflow maintains conversation history as durable state, replayed from event history

## Architecture

```
User Code (e.g., API Server)
    ↓
Workflow (WeatherOrchestrationWorkflow)
    ↓
TemporalAIAgent (obtained via Workflow.GetAgent("WeatherAssistant"))
    ↓
Workflow.ExecuteActivityAsync(AgentActivities.ExecuteAgentAsync)
    ↓
Real AIAgent (ChatClientAgent calling OpenAI)
```

## Setup

1. **Prerequisites**:
   - Local Temporal server: `temporal server start-dev`
   - OpenAI API key set in `appsettings.json`

2. **Run the sample**:
   ```bash
   dotnet run --project samples/WorkflowOrchestration/WorkflowOrchestration.csproj
   ```

3. **What happens**:
   - The worker registers the "WeatherAssistant" agent using the fluent API
   - A `WeatherOrchestrationWorkflow` is submitted to Temporal
   - Inside the workflow, `Workflow.GetAgent()` creates a `TemporalAIAgent`
   - The workflow calls the agent with a user question
   - The agent activity executes (out-of-process, non-deterministic)
   - The result is returned to the workflow as durable state

## Key Differences from BasicAgent Sample

| Aspect | BasicAgent | WorkflowOrchestration |
|--------|-----------|----------------------|
| **Setup API** | `ConfigureTemporalAgents()` | `.AddTemporalAgents()` fluent builder |
| **Access Pattern** | External proxy via `GetTemporalAgentProxy()` | Internal via `Workflow.GetAgent()` |
| **Caller** | HTTP handler, CLI, external code | Workflow (orchestration) |
| **History Management** | Workflow stores conversation history | Workflow stores conversation history |

## Code Structure

- **Program.cs**:
  - Registers the agent using the fluent API
  - Defines the `WeatherOrchestrationWorkflow`
  - Submits the workflow to Temporal

- **WeatherOrchestrationWorkflow.RunAsync()**:
  - Gets the agent via `Workflow.GetAgent()`
  - Creates a session
  - Calls the agent synchronously
  - Returns the result
