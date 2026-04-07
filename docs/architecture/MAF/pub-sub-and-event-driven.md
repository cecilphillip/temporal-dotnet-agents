# Pub/Sub and Event-Driven Communication in TemporalAgents

## Why There Is No Pub/Sub

Temporal is not a message broker. It has no built-in pub/sub substrate — no topics, no
fan-out delivery, and no durable queues shared across unrelated workflows.

**Attempting to build a pub/sub layer on top of Temporal is therefore out of scope.**
It would require an external broker (Kafka, NATS, Service Bus, etc.) and would duplicate
Temporal's own durability guarantees in an awkward way.

---

## The Temporal-Idiomatic Equivalent

In Dapr Agents the pub/sub pattern is used to decouple agents that react to events
independently. The Temporal equivalent is **parallel orchestration**: an orchestrating
workflow fans out to multiple agents simultaneously using `Workflow.WhenAllAsync`, collects
their results, and optionally aggregates them.

This gives you:

| Pub/Sub capability | Temporal equivalent |
|---|---|
| Fire-and-forget to N subscribers | `RunAgentFireAndForgetAsync` × N (fire-and-forget signal) |
| Wait for all subscribers to respond | `ExecuteAgentsInParallelAsync` (WhenAll) |
| Wait for the first subscriber to respond | `Workflow.WhenAnyAsync(tasks)` |
| Fan-out with aggregation | Parallel agents + result aggregation in workflow code |
| At-least-once delivery to subscribers | Default Temporal retry policy on activities |

---

## Code Example — Fan-Out / Fan-In with `ExecuteAgentsInParallelAsync`

The helper `ExecuteAgentsInParallelAsync` (from `TemporalWorkflowExtensions`) dispatches
multiple agents in parallel and returns all responses in input order.

```csharp
using static Temporalio.Extensions.Agents.TemporalWorkflowExtensions;

[Workflow("EventDrivenFanOut")]
public class EventDrivenFanOutWorkflow
{
    [WorkflowRun]
    public async Task<string> RunAsync(string topic, string payload)
    {
        // Simulate a "NewArticlePublished" event fan-out to three independent agents.
        var summarySession  = await GetAgent("Summarizer").CreateSessionAsync();
        var taggingSession  = await GetAgent("Tagger").CreateSessionAsync();
        var moderationSession = await GetAgent("Moderator").CreateSessionAsync();

        var prompt = new List<ChatMessage> { new(ChatRole.User, payload) };

        // All three agents run concurrently — equivalent to three pub/sub subscribers
        // each independently processing the same event.
        var results = await ExecuteAgentsInParallelAsync(new[]
        {
            (GetAgent("Summarizer"),  prompt, summarySession),
            (GetAgent("Tagger"),      prompt, taggingSession),
            (GetAgent("Moderator"),   prompt, moderationSession),
        });

        // Aggregate results (fan-in)
        return $"""
            Summary:    {results[0].Text}
            Tags:       {results[1].Text}
            Moderation: {results[2].Text}
            """;
    }
}
```

### Registration

```csharp
builder.Services
    .AddHostedTemporalWorker("localhost:7233", "default", "agents")
    .AddWorkflow<EventDrivenFanOutWorkflow>()
    .AddTemporalAgents(opts =>
    {
        opts.AddAIAgent(summarizerAgent);
        opts.AddAIAgent(taggerAgent);
        opts.AddAIAgent(moderatorAgent);
    });
```

---

## Code Example — Fire-and-Forget Fan-Out (No Response Needed)

When the publisher does not need to wait for each subscriber, use
`ITemporalAgentClient.RunAgentFireAndForgetAsync` in a loop. This is the correct
**external** path (e.g., from an API endpoint or console app):

```csharp
// External caller — outside any workflow
var subscriberNames = new[] { "AuditAgent", "NotificationAgent", "AnalyticsAgent" };

foreach (var name in subscriberNames)
{
    var sessionId = TemporalAgentSessionId.WithRandomKey(name);
    // Equivalent to publishing to a topic: fire-and-forget, no reply needed.
    // StartWorkflowAsync + SignalAsync under the hood — returns as soon as the
    // signal is accepted; the agent session workflow processes the event asynchronously.
    await agentClient.RunAgentFireAndForgetAsync(sessionId, new RunRequest(eventPayload));
}
```

> **Workflow-internal path:** If you are inside an orchestrating `[Workflow]` and need to
> kick off sub-agents without waiting for their results, use `TemporalAIAgent.RunAsync`
> inside `Workflow.ExecuteActivityAsync` (via `GetAgent`) — or use
> `ExecuteAgentsInParallelAsync` if you do want to collect results later.
> `AgentActivities` and `ExecuteAgentInput` are `internal` and cannot be referenced
> from consumer code.

---

## Key Differences vs. a Message Broker

| Aspect | Pub/Sub broker (e.g. Kafka) | Temporal parallel orchestration |
|--------|---|---|
| Subscriber discovery | Dynamic (any consumer group) | Static (known agent names at design time) |
| Ordering guarantees | Per-partition ordering | No cross-agent ordering (each runs independently) |
| Back-pressure | Consumer lag / offset management | Temporal task queue depth |
| Replay | Offset reset | Temporal workflow history |
| Dead-letter handling | DLQ topic | Temporal activity retry policy |

**When to use a message broker instead:** if your subscribers are dynamically discovered
at runtime, if fan-out exceeds dozens of agents, or if cross-service decoupling is a
hard architectural requirement. In those cases, integrate a broker (e.g. Azure Service Bus
or NATS) externally and use `TemporalAgentContext.StartWorkflowAsync` from within an
activity to trigger agent workflows in response to broker events.

---

_Last updated: 2026-02-27_
