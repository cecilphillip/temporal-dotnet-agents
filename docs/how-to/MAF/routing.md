# Agent Routing Patterns

How to route messages to the right agent in TemporalAgents. Because routing belongs inside your workflow — where it is durable, observable, and fully under your control — this document covers two workflow-based patterns and the determinism rules that govern them.

---

## Table of Contents

1. [Overview](#overview)
2. [Two Routing Patterns](#two-routing-patterns)
3. [Pattern 1: Static Routing](#pattern-1-static-routing)
4. [Pattern 2: Dynamic Routing via Activity](#pattern-2-dynamic-routing-via-activity)
5. [Do's and Don'ts](#dos-and-donts)
6. [Agent Registry: Safe vs. Unsafe Contexts](#agent-registry-safe-vs-unsafe-contexts)
7. [Choosing the Right Pattern](#choosing-the-right-pattern)
8. [References](#references)

---

## Overview

Routing in TemporalAgents is implemented as workflow logic. This means every routing decision is:

- **Durable** — recorded in Temporal's event history; a crash after the routing decision never re-evaluates it
- **Observable** — visible in the Temporal Web UI alongside every agent call
- **Deterministic** — replayed from history on crash recovery, not re-executed

The `samples/MAF/WorkflowRouting/` sample demonstrates both patterns described here.

---

## Two Routing Patterns

| Pattern | Where routing happens | Agent names | Use case |
|---|---|---|---|
| **Static routing** | Inside `[Workflow]` with a classifier agent | Hardcoded in workflow code | Fixed agent set, conditional logic, fallback chains |
| **Dynamic routing via activity** | Inside `[Workflow]` via activity that queries descriptors | Discovered at runtime | Agent set changes across deployments, feature flags, A/B testing |

---

## Pattern 1: Static Routing

### How It Works

A classifier agent runs as the first step inside a Temporal workflow. The workflow inspects the classifier's output and uses `GetAgent("name")` with hardcoded agent names to dispatch to the right specialist.

### Registration

No special routing configuration needed — just register the agents:

```csharp
services.AddChatClient(chatClient);

services.AddHostedTemporalWorker("localhost:7233", "default", "agents")
    .AddTemporalAgents(opts =>
    {
        opts.AddDurableAgent("Classifier", agent =>
        {
            agent.Instructions = "Classify the user's question into one of: ORDERS, TECH_SUPPORT, GENERAL.";
            agent.ChatClient   = sp => sp.GetRequiredService<IChatClient>();
        });
        opts.AddDurableAgent("OrdersAgent", agent =>
        {
            agent.Description  = "Handles order tracking, returns, and shipping.";
            agent.Instructions = "You are an orders specialist...";
            agent.ChatClient   = sp => sp.GetRequiredService<IChatClient>();
        });
        opts.AddDurableAgent("TechSupportAgent", agent =>
        {
            agent.Description  = "Handles technical issues and troubleshooting.";
            agent.Instructions = "You are a technical support specialist...";
            agent.ChatClient   = sp => sp.GetRequiredService<IChatClient>();
        });
        opts.AddDurableAgent("GeneralAgent", agent =>
        {
            agent.Description  = "Handles questions that don't fit other specialists.";
            agent.Instructions = "You are a general assistant.";
            agent.ChatClient   = sp => sp.GetRequiredService<IChatClient>();
        });
    })
    .AddWorkflow<CustomerServiceWorkflow>();
```

### Workflow Implementation

```csharp
[Workflow("CustomerServiceWorkflow")]
public class CustomerServiceWorkflow
{
    [WorkflowRun]
    public async Task<string> RunAsync(string userQuestion)
    {
        // Step 1: Classify the intent
        var classifier = GetAgent("Classifier");
        var classifierSession = await classifier.CreateSessionAsync();
        var classification = (await classifier.RunAsync(
            [new ChatMessage(ChatRole.User, userQuestion)],
            classifierSession)).Text?.Trim().ToUpperInvariant();

        // Step 2: Route based on classification
        var specialistName = classification switch
        {
            "ORDERS"       => "OrdersAgent",
            "TECH_SUPPORT" => "TechSupportAgent",
            _              => "GeneralAgent",  // always provide a fallback
        };

        Workflow.Logger.LogInformation(
            "Classified as '{Classification}' → routing to {Agent}",
            classification, specialistName);

        // Step 3: Call the specialist
        var specialist = GetAgent(specialistName);
        var specialistSession = await specialist.CreateSessionAsync();
        var response = await specialist.RunAsync(
            [new ChatMessage(ChatRole.User, userQuestion)],
            specialistSession);

        return response.Text ?? string.Empty;
    }
}
```

The classifier result is recorded in Temporal's event history before the specialist is invoked. A crash after classification replays the cached result — the LLM is never called again.

### Pros and Cons

| Pros | Cons |
|------|------|
| Simple — just workflow code and a switch | Agent names hardcoded in workflow code |
| Full control: if/else, fallback chains, multi-step logic | Adding new agents requires code changes and redeployment |
| Every routing decision recorded in history | |
| No extra infrastructure | |

### Reference

See `samples/MAF/WorkflowRouting/` (`CustomerServiceWorkflow.cs`) for a complete working example.

---

## Pattern 2: Dynamic Routing via Activity

This pattern is for when the set of available agents changes across deployments — feature flags, A/B tests, gradual rollouts — without recompiling the workflow.

### The Determinism Problem

The natural instinct is to call `TemporalAgentsOptions.GetRegisteredAgentNames()` or `TemporalAgentsOptions.IsAgentRegistered()` directly in workflow code. **This is unsafe.** Here is why:

- Workflow code must be **deterministic during replay**
- If agent registration changes between the original execution and replay, the routing decision would differ
- A different decision means a different code path, which means Temporal raises a **non-determinism error** and the workflow fails
- This is the same fundamental reason `DateTime.UtcNow` and `Guid.NewGuid()` are forbidden in workflows

### The Safe Pattern: Registry Lookup + Activity

The workflow discovers available agents by calling an activity that queries `options.GetRegisteredAgentNames()` and combines the names with a local description map declared in the activity. The activity result is cached in workflow history, so the registry is never re-queried on replay.

#### Step 1: Register agents with descriptions

```csharp
services.AddChatClient(chatClient);

services.AddHostedTemporalWorker("localhost:7233", "default", "agents")
    .AddTemporalAgents(opts =>
    {
        // Not a routable specialist — no Description set, so it is excluded from GetAgentDescriptors().
        opts.AddDurableAgent("Classifier", agent =>
        {
            agent.Instructions = "Classify the user's question.";
            agent.ChatClient   = sp => sp.GetRequiredService<IChatClient>();
        });
        opts.AddDurableAgent("OrdersAgent", agent =>
        {
            agent.Description  = "Handles order tracking, returns, and shipping.";
            agent.Instructions = "You are an orders specialist...";
            agent.ChatClient   = sp => sp.GetRequiredService<IChatClient>();
        });
        opts.AddDurableAgent("TechSupportAgent", agent =>
        {
            agent.Description  = "Handles technical issues, app crashes, and troubleshooting.";
            agent.Instructions = "You are a technical support specialist...";
            agent.ChatClient   = sp => sp.GetRequiredService<IChatClient>();
        });
        opts.AddDurableAgent("GeneralAgent", agent =>
        {
            agent.Description  = "Handles general questions and fallback routing.";
            agent.Instructions = "You are a general assistant.";
            agent.ChatClient   = sp => sp.GetRequiredService<IChatClient>();
        });
    })
    .AddWorkflow<DynamicRoutingWorkflow>()
    .AddSingletonActivities<RoutingActivities>();
```

> The `Description` you set on the builder is stored in the agent registry. `AddAgentProxy` accepts an optional `description` parameter for proxy-only declarations. Agents registered without a description (e.g. a classifier) are excluded from `GetAgentDescriptors()` automatically. The `AgentDescriptor` record in `Temporalio.Extensions.Agents.State` is the `(Name, Description)` shape the method returns.

#### Step 2: Define routing activities

Activities are not replayed — their results are cached in workflow history. This makes registry lookups safe.

```csharp
public class RoutingActivities(TemporalAgentsOptions options)
{
    // Returns all registered agents that have a description. Agents registered
    // without one (e.g. the Classifier) are excluded automatically.
    // Safe inside an activity: result is cached in history; registry not re-queried on replay.
    [Activity("GetAvailableAgents")]
    public AgentInfo[] GetAvailableAgents()
    {
        return options.GetAgentDescriptors()
            .Select(d => new AgentInfo(d.Name, d.Description))
            .ToArray();
    }

    // Validates an LLM-chosen name against the registry.
    // LLMs can hallucinate names — this activity is the safety net.
    [Activity("ValidateAgent")]
    public string ValidateAgent(string agentName, string fallback)
    {
        return options.IsAgentRegistered(agentName) ? agentName : fallback;
    }
}

public record AgentInfo(string Name, string Description);
```

#### Step 3: Use in a workflow — no hardcoded agent names

```csharp
[Workflow("DynamicRoutingWorkflow")]
public class DynamicRoutingWorkflow
{
    private const string FallbackAgent = "GeneralAgent";

    [WorkflowRun]
    public async Task<string> RunAsync(string userQuestion)
    {
        // Step 1: Discover available agents via activity (cached on replay)
        var agents = await Workflow.ExecuteActivityAsync(
            (RoutingActivities a) => a.GetAvailableAgents(),
            new ActivityOptions { StartToCloseTimeout = TimeSpan.FromSeconds(10) });

        if (agents.Length == 0)
            return await CallAgent(FallbackAgent, userQuestion);

        // Step 2: Build a routing prompt from the discovered descriptors
        var agentList = string.Join("\n", agents.Select(a => $"  {a.Name} — {a.Description}"));
        var routingPrompt =
            $"Given the user question, respond with ONLY the name of the best-matching agent.\n\n" +
            $"Available agents:\n{agentList}\n\n" +
            $"User question: {userQuestion}\n\n" +
            $"Respond with the agent name only. No explanation, no punctuation.";

        var classifier = GetAgent("Classifier");
        var classifierSession = await classifier.CreateSessionAsync();
        var chosenAgent = (await classifier.RunAsync(
            [new ChatMessage(ChatRole.User, routingPrompt)], classifierSession))
            .Text?.Trim() ?? string.Empty;

        // Step 3: Validate the LLM's choice via activity (cached on replay)
        var agentName = await Workflow.ExecuteActivityAsync(
            (RoutingActivities a) => a.ValidateAgent(chosenAgent, FallbackAgent),
            new ActivityOptions { StartToCloseTimeout = TimeSpan.FromSeconds(10) });

        // Step 4: Call the resolved specialist
        return await CallAgent(agentName, userQuestion);
    }

    private static async Task<string> CallAgent(string agentName, string userQuestion)
    {
        var specialist = GetAgent(agentName);
        var specialistSession = await specialist.CreateSessionAsync();
        var response = await specialist.RunAsync(
            [new ChatMessage(ChatRole.User, userQuestion)], specialistSession);
        return response.Text ?? string.Empty;
    }
}
```

### Why This Is Safe

Both activity results are recorded in Temporal's event history. On replay, cached results are returned — the registry is never re-queried:

```
Original execution:
  Activity("GetAvailableAgents")  → reads registry → ["OrdersAgent", "TechSupportAgent", ...] → cached
  Activity("ValidateAgent")       → confirms "OrdersAgent" exists → cached

Replay after crash / new deployment:
  Activity("GetAvailableAgents")  → returns cached list (registry NOT queried)
  Activity("ValidateAgent")       → returns cached "OrdersAgent" (registry NOT queried)
```

### Pros and Cons

| Pros | Cons |
|------|------|
| No hardcoded agent names in the routing workflow | More complex setup than static routing |
| Add/remove agents by changing registration, not workflow code | Requires custom activities and workflow |
| Naturally handles feature flags and A/B testing | Two extra activity calls per request |
| Descriptors provide rich context for LLM classification | Descriptors must be kept in sync with agent registrations |

### Reference

See `samples/MAF/WorkflowRouting/` (`DynamicRoutingWorkflow.cs` and `RoutingActivities.cs`) for a complete working example.

---

## Do's and Don'ts

### DO

- Route inside a workflow — the decision is durable, visible in history, and replayed from cache
- Use `GetAgent("name")` with string constants or activity results inside workflows
- Query `TemporalAgentsOptions` inside **activities** — activity results are cached; the registry is never re-queried on replay
- Provide a default/fallback agent for unrecognized or empty classifications
- Validate LLM-chosen agent names via an activity before dispatching (LLMs can hallucinate names)
- Test routing with edge cases: empty LLM response, unexpected classification, no registered agents

### DON'T

- Don't call `TemporalAgentsOptions.GetRegisteredAgentNames()` inside a `[Workflow]` class — non-deterministic on replay
- Don't call `TemporalAgentsOptions.IsAgentRegistered()` inside a `[Workflow]` class — same reason
- Don't forget the `_` default case in switch expressions — classifiers produce unexpected output

---

## Agent Registry: Safe vs. Unsafe Contexts

| Context | Can query registry? | Why |
|---|---|---|
| `Program.cs` / startup | Yes | Runs once at startup, not replayed |
| Health-check endpoint | Yes | External HTTP handler, not workflow code |
| Activity code | Yes | Results are cached in history on replay |
| `[Workflow]` class method | **NO** | Replayed deterministically — registry may have changed |
| `[WorkflowUpdate]` handler | **NO** | Part of workflow code, subject to replay |
| `[WorkflowQuery]` handler | Read-only is safe but pointless | Queries don't affect workflow state |

The rule is simple: **if the code runs inside a workflow execution context, do not query the registry.** Wrap the query in an activity instead.

---

## Choosing the Right Pattern

**Is the agent set fixed at compile time?**

- **Yes** — Use **Pattern 1** (static routing). Plain switch expressions and `GetAgent("name")` give you full control with minimal ceremony.
- **No** (agents change across deployments: feature flags, A/B tests, gradual rollouts) — Use **Pattern 2** (dynamic routing via activity).

**Do you need fallback chains or multi-step classification?**

- **Yes** — Both patterns support this. With static routing, compose multiple classifier calls and switch statements directly in the workflow.
- **No** — Static routing is the lower-friction choice.

---

## References

- `samples/MAF/WorkflowRouting/` — both patterns (`CustomerServiceWorkflow.cs`, `DynamicRoutingWorkflow.cs`, `RoutingActivities.cs`)
- [`durability-and-determinism.md`](../architecture/MAF/durability-and-determinism.md) — why workflow code must be deterministic
- [`agent-sessions-and-workflow-loop.md`](../architecture/MAF/agent-sessions-and-workflow-loop.md) — how agent calls become durable activities
- [`session-statebag-and-context-providers.md`](../architecture/MAF/session-statebag-and-context-providers.md) — StateBag and AIContextProvider integration
- `src/Temporalio.Extensions.Agents/TemporalAgentsOptions.cs` — agent registry API (`GetRegisteredAgentNames`, `IsAgentRegistered`)

---

_Last updated: 2026-04-30_
