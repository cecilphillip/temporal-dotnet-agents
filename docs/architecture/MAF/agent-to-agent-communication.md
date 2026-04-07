# Agent-to-Agent Communication

How agents communicate with each other in TemporalAgents — from simple workflow sub-agent calls to cross-workflow signaling. This document covers the three primary communication patterns, their durability guarantees, and when to use each.

---

## Table of Contents

1. [Overview](#overview)
2. [Pattern 1: Workflow → Sub-Agent (GetAgent)](#pattern-1-workflow--sub-agent-getagent)
3. [Pattern 2: Parallel Fan-Out (ExecuteAgentsInParallelAsync)](#pattern-2-parallel-fan-out-executeagentsinparallelasync)
4. [Pattern 3: Cross-Workflow Communication from Agent Tools](#pattern-3-cross-workflow-communication-from-agent-tools)
5. [Pattern Comparison](#pattern-comparison)
6. [Choosing the Right Pattern](#choosing-the-right-pattern)

---

## Overview

TemporalAgents supports three distinct patterns for agent-to-agent communication, each operating at a different level of the architecture:

| Pattern | Context | Mechanism | Use Case |
|---------|---------|-----------|----------|
| **GetAgent** | Inside a `[Workflow]` | Activity-based execution | Sequential sub-agent orchestration |
| **ExecuteAgentsInParallelAsync** | Inside a `[Workflow]` | `Workflow.WhenAllAsync` | Concurrent fan-out to multiple agents |
| **TemporalAgentContext** | Inside an agent tool (activity) | Temporal client operations | Cross-workflow signaling and workflow creation |

All three patterns produce **durable** communication — results are recorded in Temporal's event history and replayed deterministically on worker restart.

---

## Pattern 1: Workflow → Sub-Agent (GetAgent)

The most common pattern: an orchestrating workflow calls one or more agents sequentially.

### How It Works

`TemporalWorkflowExtensions.GetAgent()` returns a `TemporalAIAgent` — a workflow-safe `AIAgent` that executes inference via `Workflow.ExecuteActivityAsync`. The agent's conversation history is stored as workflow state and replayed from event history:

```csharp
public static TemporalAIAgent GetAgent(
    string agentName,
    ActivityOptions? activityOptions = null)
{
    return new TemporalAIAgent(agentName, activityOptions);
}
```

### Basic Usage

```csharp
[Workflow]
public class ResearchWorkflow
{
    [WorkflowRun]
    public async Task<string> RunAsync(string topic)
    {
        var researcher = TemporalWorkflowExtensions.GetAgent("ResearcherAgent");
        var session = await researcher.CreateSessionAsync();

        var outline = await researcher.RunAsync(
            $"Create an outline about: {topic}", session);

        var writer = TemporalWorkflowExtensions.GetAgent("WriterAgent");
        var writerSession = await writer.CreateSessionAsync();

        var draft = await writer.RunAsync(
            $"Write based on this outline:\n{outline.Text}",
            writerSession);

        return draft.Text ?? string.Empty;
    }
}
```

### History Accumulation

Each `TemporalAIAgent` instance maintains its own conversation history (`List<TemporalAgentStateEntry>`). When `RunAsync` is called, the full history is sent to `AgentActivities.ExecuteAgentAsync` as part of the input, rebuilt into `ChatMessage` objects, and passed to the real `AIAgent`. This means the agent sees the complete conversation context on every turn.

### Deterministic Session IDs

Inside a workflow, session IDs must be deterministic for replay safety. `CreateSessionAsync` uses `Workflow.NewGuid()` internally:

```csharp
protected override ValueTask<AgentSession> CreateSessionCoreAsync(
    CancellationToken cancellationToken = default)
{
    var sessionId = TemporalAgentSessionId.WithDeterministicKey(
        _agentName, Workflow.NewGuid());
    return new ValueTask<AgentSession>(new TemporalAgentSession(sessionId));
}
```

You can also create session IDs explicitly:

```csharp
var sessionId = TemporalWorkflowExtensions.NewAgentSessionId("MyAgent");
```

### Important: One Instance Per Conversation

Two sessions on the same `TemporalAIAgent` instance share history because history is stored on the instance. If you need independent conversations, create separate `GetAgent` calls:

```csharp
// CORRECT: two independent agents with independent histories
var agent1 = TemporalWorkflowExtensions.GetAgent("Analyst");
var agent2 = TemporalWorkflowExtensions.GetAgent("Analyst");

// WRONG: session2 will see session1's history
var agent = TemporalWorkflowExtensions.GetAgent("Analyst");
var session1 = await agent.CreateSessionAsync();
await agent.RunAsync("Question 1", session1);
var session2 = await agent.CreateSessionAsync();
await agent.RunAsync("Question 2", session2); // sees "Question 1" in history!
```

### Iterative Agent Communication

The Evaluator-Optimizer pattern shows agents communicating iteratively — a generator produces drafts and an evaluator provides feedback:

```csharp
[Workflow("EvaluatorOptimizer.EvaluatorOptimizerWorkflow")]
public class EvaluatorOptimizerWorkflow
{
    [WorkflowRun]
    public async Task<string> RunAsync(string task, int maxIterations = 3)
    {
        var generator = GetAgent("Generator");
        var evaluator = GetAgent("Evaluator");

        var genSession = await generator.CreateSessionAsync();
        var evalSession = await evaluator.CreateSessionAsync();

        string draft = string.Empty;
        string feedback = string.Empty;

        for (int i = 0; i < maxIterations; i++)
        {
            var genPrompt = i == 0
                ? task
                : $"Revise based on feedback:\n{feedback}\n\nDraft:\n{draft}";

            var genResponse = await generator.RunAsync(
                [new ChatMessage(ChatRole.User, genPrompt)], genSession);
            draft = genResponse.Text ?? string.Empty;

            var evalResponse = await evaluator.RunAsync(
                [new ChatMessage(ChatRole.User,
                    $"Evaluate this draft. Reply 'APPROVED' if ready, " +
                    $"or give feedback.\n\n{draft}")],
                evalSession);

            feedback = evalResponse.Text ?? string.Empty;

            if (feedback.Contains("APPROVED", StringComparison.OrdinalIgnoreCase))
                break;
        }

        return draft;
    }
}
```

Each iteration is a pair of durable activities. If the worker crashes between the generator and evaluator turns, the generator's result is replayed from history — no re-execution.

---

## Pattern 2: Parallel Fan-Out (ExecuteAgentsInParallelAsync)

When multiple agents can work independently on the same (or different) inputs, fan them out concurrently.

### How It Works

`ExecuteAgentsInParallelAsync` maps each `(Agent, Messages, Session)` tuple to a `RunAsync` task and awaits them all via `Workflow.WhenAllAsync` — the workflow-safe equivalent of `Task.WhenAll`:

```csharp
public static async Task<IReadOnlyList<AgentResponse>> ExecuteAgentsInParallelAsync(
    IEnumerable<(TemporalAIAgent Agent, IList<ChatMessage> Messages, AgentSession Session)> requests,
    CancellationToken cancellationToken = default)
{
    ArgumentNullException.ThrowIfNull(requests);

    // Avoid double-enumeration: cast to IList if already materialized, otherwise ToList().
    var requestList = requests as IList<(TemporalAIAgent Agent, IList<ChatMessage> Messages, AgentSession Session)>
        ?? requests.ToList();
    var tasks = new List<Task<AgentResponse>>(requestList.Count);
    foreach (var r in requestList)
        tasks.Add(r.Agent.RunAsync(r.Messages, r.Session, null, cancellationToken));

    return await Workflow.WhenAllAsync(tasks);
}
```

### Example: Multi-Agent Fan-Out

From the `MultiAgentRouting` sample:

```csharp
[Workflow("MultiAgentRouting.RoutingWorkflow")]
public class RoutingWorkflow
{
    [WorkflowRun]
    public async Task<string[]> RunAsync(string userQuery)
    {
        var weather = GetAgent("WeatherAgent");
        var billing = GetAgent("BillingAgent");
        var techSupport = GetAgent("TechSupportAgent");

        var wSession = await weather.CreateSessionAsync();
        var bSession = await billing.CreateSessionAsync();
        var tSession = await techSupport.CreateSessionAsync();

        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, userQuery)
        };

        var results = await ExecuteAgentsInParallelAsync(
        [
            (weather,     (IList<ChatMessage>)messages, wSession),
            (billing,     messages, bSession),
            (techSupport, messages, tSession)
        ]);

        return results.Select(r => r.Text ?? string.Empty).ToArray();
    }
}
```

### Key Properties

- **Independent sessions** — each agent gets its own session, so histories don't mix
- **Results in input order** — `results[0]` corresponds to the first tuple, regardless of completion order
- **Durable** — each activity is independently recorded; if one fails, only that one is retried
- **Workflow-safe** — uses `Workflow.WhenAllAsync`, not `Task.WhenAll`

---

## Pattern 3: Cross-Workflow Communication from Agent Tools

When an agent tool (running inside `AgentActivities`) needs to interact with other Temporal workflows, it uses `TemporalAgentContext.Current`.

### Available Operations

`TemporalAgentContext` exposes these capabilities to agent tools:

```csharp
// Start a new workflow
string workflowId = await TemporalAgentContext.Current.StartWorkflowAsync(
    (ProcessingWorkflow wf) => wf.RunAsync(payload),
    new WorkflowOptions("job-" + Guid.NewGuid(), taskQueue: "jobs"));

// Query an existing workflow's state
WorkflowExecutionDescription? desc =
    await TemporalAgentContext.Current.GetWorkflowDescriptionAsync(workflowId);

// Send a fire-and-forget signal to another workflow
await TemporalAgentContext.Current.SignalWorkflowAsync<AlertWorkflow>(
    alertWorkflowId,
    wf => wf.IngestAnomalyAsync(alert));
```

### Why Activity-Mediated Signaling?

The Temporal .NET SDK doesn't expose direct workflow-to-workflow signaling. The established pattern routes through an activity with an injected `ITemporalClient`. This has several benefits:

- **Determinism** — the signal call runs inside an activity, so its completion is recorded in event history
- **Retry** — if the signal fails (e.g., target workflow not found), Temporal's activity retry handles it
- **Decoupling** — the calling workflow doesn't need to know the target's workflow type at compile time

### Example: AmbientAgent — Monitor → Alert Pipeline

The `AmbientAgent` sample demonstrates cross-workflow communication between a monitoring workflow and an alert workflow:

**MonitorWorkflow** — analyzes health-check data via an LLM agent and signals an alert workflow when anomalies are detected:

```csharp
[Workflow("AmbientAgent.MonitorWorkflow")]
public class MonitorWorkflow
{
    [WorkflowRun]
    public async Task RunAsync(MonitorWorkflowInput input)
    {
        // ... wait for enough health-check signals ...

        // Analyze via LLM agent
        var analysisAgent = GetAgent("AnalysisAgent");
        var session = await analysisAgent.CreateSessionAsync();
        var response = await analysisAgent.RunAsync(
            [new ChatMessage(ChatRole.User, summary)], session);

        // Signal the alert workflow via activity (cross-workflow communication)
        if (response.Text!.Contains("ANOMALY", StringComparison.OrdinalIgnoreCase))
        {
            var alert = new AnomalyAlert(
                DetectedAt: Workflow.UtcNow,
                Summary: response.Text,
                RecentReadings: _buffer.TakeLast(input.AnalysisInterval).ToList());

            await Workflow.ExecuteActivityAsync(
                (AlertActivities a) =>
                    a.SignalAlertWorkflowAsync(input.AlertWorkflowId, alert),
                new ActivityOptions { StartToCloseTimeout = TimeSpan.FromSeconds(30) });
        }
    }
}
```

**AlertActivities** — bridges the two workflows via `ITemporalClient`:

```csharp
public class AlertActivities(ITemporalClient client)
{
    [Activity]
    public async Task SignalAlertWorkflowAsync(string alertWorkflowId, AnomalyAlert alert)
    {
        var handle = client.GetWorkflowHandle<AlertWorkflow>(alertWorkflowId);
        await handle.SignalAsync(wf => wf.IngestAnomalyAsync(alert));
    }
}
```

**AlertWorkflow** — receives signals, uses an LLM agent to compose notifications:

```csharp
using static Temporalio.Extensions.Agents.TemporalWorkflowExtensions;

[Workflow("AmbientAgent.AlertWorkflow")]
public class AlertWorkflow
{
    [WorkflowSignal("IngestAnomaly")]
    public Task IngestAnomalyAsync(AnomalyAlert alert)
    {
        _pendingAlerts.Add(alert);
        return Task.CompletedTask;
    }

    [WorkflowRun]
    public async Task RunAsync()
    {
        while (!_shutdownRequested)
        {
            await Workflow.WaitConditionAsync(
                () => _shutdownRequested || _pendingAlerts.Count > 0,
                timeout: TimeSpan.FromHours(1));

            // Process pending alerts with an LLM agent
            foreach (var alert in _pendingAlerts.ToList())
            {
                var alertAgent = GetAgent("AlertAgent");
                var session = await alertAgent.CreateSessionAsync();
                var response = await alertAgent.RunAsync(
                    [new ChatMessage(ChatRole.User, /* prompt */)], session);
                // ...
            }
        }
    }
}
```

This demonstrates a three-stage pipeline: **Signal ingestion → LLM analysis → Cross-workflow alert → LLM notification**, all fully durable.

---

## Pattern Comparison

| | GetAgent | ExecuteAgentsInParallelAsync | TemporalAgentContext |
|---|---|---|---|
| **Context** | Inside `[Workflow]` | Inside `[Workflow]` | Inside agent tool (activity) |
| **Mechanism** | `ExecuteActivityAsync` | `Workflow.WhenAllAsync` | Direct `ITemporalClient` calls |
| **Concurrency** | Sequential | Concurrent | N/A (single operation) |
| **History** | Stored on `TemporalAIAgent` instance | Independent per agent | Recorded as activity result |
| **Durability** | Activity result cached in history | Each activity independently cached | Activity result cached |
| **OTel spans** | `agent.turn` per call | `agent.turn` per agent | Depends on operation |
| **Continue-as-new** | History carried via workflow state | N/A (typically single-shot) | N/A |

---

## Choosing the Right Pattern

**Do your agents need to run sequentially with shared context?**
- **Yes** → Use **Pattern 1** (GetAgent). Each agent can build on the previous agent's output.

**Do your agents work independently on the same input?**
- **Yes** → Use **Pattern 2** (ExecuteAgentsInParallelAsync). Get results faster by running agents concurrently.

**Does an agent need to start or signal a different workflow?**
- **Yes** → Use **Pattern 3** (TemporalAgentContext). This is the only way to communicate across workflow boundaries from within an agent tool.

**Can you combine patterns?**

Absolutely. A common composition:

1. **Pattern 1** to classify a user's intent via a classifier agent
2. **Pattern 2** to fan out to multiple specialists based on the classification
3. **Pattern 3** inside a specialist's tool to start a background processing workflow

```csharp
[WorkflowRun]
public async Task<string> RunAsync(string question)
{
    // Pattern 1: classify
    var classifier = GetAgent("Classifier");
    var session = await classifier.CreateSessionAsync();
    var intent = await classifier.RunAsync(question, session);

    // Pattern 2: fan-out to specialists
    var agents = GetSpecialistsForIntent(intent.Text!);
    var results = await ExecuteAgentsInParallelAsync(agents);

    // Pattern 3 happens implicitly — specialist tools use
    // TemporalAgentContext.Current to start background jobs
    return CombineResults(results);
}
```

---

## References

- `src/Temporalio.Extensions.Agents/TemporalWorkflowExtensions.cs` — `GetAgent`, `ExecuteAgentsInParallelAsync`
- `src/Temporalio.Extensions.Agents/TemporalAIAgent.cs` — workflow-safe agent with activity-based execution
- `src/Temporalio.Extensions.Agents/TemporalAgentContext.cs` — async-local Temporal capabilities for tools
- `samples/WorkflowOrchestration/` — Pattern 1 example
- `samples/MultiAgentRouting/RoutingWorkflow.cs` — Pattern 2 example
- `samples/EvaluatorOptimizer/EvaluatorOptimizerWorkflow.cs` — iterative agent communication
- `samples/AmbientAgent/` — Pattern 3 example (cross-workflow signaling)
- [Durability & Determinism](./durability-and-determinism.md) — why activity results are cached on replay
- [Routing Patterns](../how-to/MAF/routing.md) — LLM-powered and workflow-based routing

---

_Last updated: 2026-03-13_
