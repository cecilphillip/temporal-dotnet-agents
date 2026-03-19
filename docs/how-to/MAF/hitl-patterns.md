# Human-in-the-Loop Patterns

How to implement approval gates, build approval dashboards, handle timeouts, and test HITL flows in TemporalAgents.

---

## Table of Contents

1. [Overview](#overview)
2. [How Approval Works](#how-approval-works)
3. [Requesting Approval from a Tool](#requesting-approval-from-a-tool)
4. [Building an Approval Dashboard](#building-an-approval-dashboard)
5. [Timeout Configuration](#timeout-configuration)
6. [Multi-Step Approval Chains](#multi-step-approval-chains)
7. [Error Handling and Rejection](#error-handling-and-rejection)
8. [Testing HITL Flows](#testing-hitl-flows)
9. [Types Reference](#types-reference)
10. [Complete Example: Email Approval](#complete-example-email-approval)

---

## Overview

TemporalAgents supports human-in-the-loop (HITL) approval gates that allow agent tools to **pause mid-turn** and wait for a human decision before proceeding. The approval flow is fully durable — if the worker crashes while waiting for a human, the activity resumes from exactly the same point once a new worker picks it up.

Three `[WorkflowUpdate]` / `[WorkflowQuery]` handlers make this work:

| Handler | Type | Purpose |
|---------|------|---------|
| `RequestApprovalAsync` | Update | Called from inside a tool; blocks until human responds |
| `SubmitApprovalAsync` | Update | Called from external system; unblocks the tool |
| `GetPendingApproval` | Query | Called from external system; polls for pending requests |

---

## How Approval Works

```
Agent Tool                    AgentWorkflow                  External System
    │                              │                              │
    │  RequestApprovalAsync        │                              │
    │─────────────────────────────>│                              │
    │                              │  stores _pendingApproval     │
    │                              │  blocks on WaitConditionAsync│
    │  (activity suspended)        │                              │
    │                              │                              │
    │                              │  GetPendingApproval (query)  │
    │                              │<─────────────────────────────│
    │                              │──────────────────────────────>│
    │                              │  returns DurableApprovalRequest │
    │                              │                              │
    │                              │  SubmitApprovalAsync (update)│
    │                              │<─────────────────────────────│
    │                              │  sets _approvalDecision      │
    │                              │  WaitConditionAsync unblocks │
    │                              │                              │
    │  DurableApprovalDecision returned │                         │
    │<─────────────────────────────│                              │
    │                              │                              │
    │  tool continues or cancels   │                              │
```

The key insight: `RequestApprovalAsync` is a `[WorkflowUpdate]` called from inside the activity (via `TemporalAgentContext`). The workflow blocks on `WaitConditionAsync` while the activity remains suspended. The activity heartbeats during this period, so the worker won't treat it as stuck.

---

## Requesting Approval from a Tool

Call `TemporalAgentContext.Current.RequestApprovalAsync` from inside any agent tool implementation:

```csharp
var sendEmailTool = AIFunctionFactory.Create(
    async (
        [Description("Recipient email")] string to,
        [Description("Email subject")]   string subject,
        [Description("Email body")]      string body) =>
    {
        var decision = await TemporalAgentContext.Current.RequestApprovalAsync(
            new DurableApprovalRequest
            {
                RequestId   = Guid.NewGuid().ToString("N"),
                Description = $"Send email to {to} — Subject: {subject}\n\nBody:\n{body}"
            });

        if (!decision.Approved)
        {
            return $"Email rejected by reviewer: {decision.Reason ?? "no reason given"}";
        }

        // Proceed with the actual action
        await SendEmailAsync(to, subject, body);
        return $"Email sent to {to}.";
    },
    name: "send_email",
    description: "Sends an email. Requires human approval.");
```

**Important:** The tool function is `async` and awaits the approval decision. The entire `proxy.RunAsync` call that triggered this tool remains suspended until the human responds (or the timeout elapses).

---

## Building an Approval Dashboard

The external system (UI, CLI, monitoring service) uses two methods on `ITemporalAgentClient`:

### Polling for Pending Approvals

`GetPendingApprovalAsync` is a `[WorkflowQuery]` — it's read-only, never blocks the workflow, and is safe to call at any frequency:

```csharp
ITemporalAgentClient client = // resolved from DI
var sessionId = new TemporalAgentSessionId("EmailAssistant", userId);

// Poll until an approval appears
DurableApprovalRequest? pending = await client.GetPendingApprovalAsync(sessionId);

if (pending is not null)
{
    Console.WriteLine($"Description: {pending.Description}");
    Console.WriteLine($"Request ID: {pending.RequestId}");
}
```

### Submitting a Decision

`SubmitApprovalAsync` is a `[WorkflowUpdate]` — it validates the `RequestId`, sets the decision, and unblocks the tool:

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

### Validation Guards

The workflow validates submissions before they enter history:

- **No pending request:** throws `InvalidOperationException` — "No approval request is pending"
- **Wrong RequestId:** throws `InvalidOperationException` — "Decision RequestId does not match pending request"

These guards prevent stale or misrouted decisions from affecting the workflow.

### Polling Pattern for a Console App

The `HumanInTheLoop` sample demonstrates a polling loop that stays responsive while the agent is suspended:

```csharp
// Start the agent call (may block inside a tool)
var agentTask = proxy.RunAsync(userMessages, session);

while (!agentTask.IsCompleted)
{
    await Task.Delay(TimeSpan.FromSeconds(1));

    DurableApprovalRequest? pending = null;
    try
    {
        pending = await client.GetPendingApprovalAsync(sessionId);
    }
    catch (RpcException ex) when (ex.Code == RpcException.StatusCode.NotFound)
    {
        continue; // Workflow not started yet
    }

    if (pending is null) continue;

    // Display the request and collect human input
    var approved = PromptForDecision(pending);

    await client.SubmitApprovalAsync(sessionId, new DurableApprovalDecision
    {
        RequestId = pending.RequestId,
        Approved  = approved,
        Reason    = approved ? null : "Rejected by reviewer."
    });
}

var response = await agentTask; // Agent resumes and returns final response
```

---

## Timeout Configuration

### ApprovalTimeout

Controls how long the workflow waits for a human response before auto-rejecting:

```csharp
builder.Services
    .AddHostedTemporalWorker("agents")
    .AddTemporalAgents(opts =>
    {
        opts.ApprovalTimeout = TimeSpan.FromHours(4); // default: 7 days
        opts.AddAIAgent(agent);
    });
```

When the timeout elapses, `RequestApprovalAsync` returns a rejected decision:

```csharp
new DurableApprovalDecision
{
    RequestId = request.RequestId,
    Approved  = false,
    Reason    = "Approval timed out after 4 hours with no human response."
}
```

### ActivityStartToCloseTimeout

The activity that hosts the tool **also** has a timeout. It must exceed the `ApprovalTimeout`, otherwise the activity times out before the human can respond:

```csharp
opts.ActivityStartToCloseTimeout = TimeSpan.FromHours(24); // must exceed ApprovalTimeout
opts.ActivityHeartbeatTimeout    = TimeSpan.FromMinutes(5);
opts.ApprovalTimeout             = TimeSpan.FromHours(4);
```

**Rule of thumb:** `ActivityStartToCloseTimeout` > `ApprovalTimeout` + expected LLM processing time.

### Continue-as-New

`ApprovalTimeout` survives continue-as-new transitions — it's carried forward in `AgentWorkflowInput.ApprovalTimeout`.

---

## Multi-Step Approval Chains

A single tool can request multiple approvals sequentially:

```csharp
var deleteTool = AIFunctionFactory.Create(async (string userId) =>
{
    // First gate: data deletion
    var decision1 = await TemporalAgentContext.Current.RequestApprovalAsync(
        new DurableApprovalRequest
        {
            RequestId   = Guid.NewGuid().ToString("N"),
            Description = $"Delete user data for {userId} — This will remove all records. Irreversible."
        });

    if (!decision1.Approved)
        return $"Data deletion rejected: {decision1.Reason}";

    // Second gate: account deactivation
    var decision2 = await TemporalAgentContext.Current.RequestApprovalAsync(
        new DurableApprovalRequest
        {
            RequestId   = Guid.NewGuid().ToString("N"),
            Description = $"Deactivate account for {userId} — User will lose access immediately."
        });

    if (!decision2.Approved)
        return $"Account deactivation rejected: {decision2.Reason}. Data was still deleted.";

    await DeleteAndDeactivateAsync(userId);
    return $"User {userId} data deleted and account deactivated.";
},
name: "delete_user",
description: "Deletes user data and deactivates account. Requires two approvals.");
```

Each `RequestApprovalAsync` call is a separate `[WorkflowUpdate]` round-trip. The external system sees each pending request individually and can approve/reject them independently.

**Caveat:** If the first approval is granted but the second is rejected, the first action may have already been performed (depending on your tool logic). Design tools so that partial completion is either safe or explicitly handled.

---

## Error Handling and Rejection

### Handling Rejection in Tools

```csharp
var decision = await TemporalAgentContext.Current.RequestApprovalAsync(request);

if (!decision.Approved)
{
    // Option 1: Return a message — agent incorporates it into its response
    return $"Action rejected: {decision.Reason ?? "no reason given"}";

    // Option 2: Throw — the tool fails and the agent reports an error
    throw new OperationCanceledException($"Rejected: {decision.Reason}");
}
```

Returning a message is generally preferred — it lets the agent explain the rejection to the user and offer alternatives.

### Handling Timeout in Tools

Timeout produces the same rejected ticket, so the tool handles it identically:

```csharp
if (!decision.Approved)
{
    // Could be a human rejection or a timeout
    var reason = decision.Reason ?? "unknown reason";
    return $"Action not approved: {reason}";
}
```

The `Reason` field distinguishes the two cases — timeouts include "timed out" in the message.

### Handling Submission Errors

`SubmitApprovalAsync` can throw `InvalidOperationException` if:
- No approval is pending (the tool hasn't called `RequestApprovalAsync` yet)
- The `RequestId` doesn't match the pending request

Handle these in the external system:

```csharp
try
{
    await client.SubmitApprovalAsync(sessionId, decision);
}
catch (InvalidOperationException ex)
{
    // Stale request — the approval may have timed out
    Console.WriteLine($"Cannot submit: {ex.Message}");
}
```

---

## Testing HITL Flows

### Integration Tests

The test suite covers both the timeout path and the happy path:

```csharp
[Fact]
public async Task RequestApproval_TimesOut_ReturnsRejectedDecision()
{
    // Build a custom host with a short approval timeout
    var host = BuildHostWithApprovalTimeout(TimeSpan.FromSeconds(2));
    await host.StartAsync();

    // Start workflow and send the approval request
    var handle = env.Client.GetWorkflowHandle<AgentWorkflow>(workflowId);
    var decision = await handle.ExecuteUpdateAsync<AgentWorkflow, DurableApprovalDecision>(
        wf => wf.RequestApprovalAsync(new DurableApprovalRequest
        {
            RequestId   = Guid.NewGuid().ToString("N"),
            Description = "Test action — Test details"
        }));

    Assert.False(decision.Approved);
    Assert.Contains("timed out", decision.Reason);
}

[Fact]
public async Task SubmitApproval_BeforeTimeout_ReturnsApprovedDecision()
{
    // Use a longer timeout so we can submit before it elapses
    var host = BuildHostWithApprovalTimeout(TimeSpan.FromMinutes(5));
    await host.StartAsync();

    // Request approval in background
    var approvalTask = handle.ExecuteUpdateAsync<AgentWorkflow, DurableApprovalDecision>(
        wf => wf.RequestApprovalAsync(request));

    // Wait for the request to be pending, then submit
    await Task.Delay(500);
    await handle.ExecuteUpdateAsync<AgentWorkflow, DurableApprovalDecision>(
        wf => wf.SubmitApprovalAsync(new DurableApprovalDecision
        {
            RequestId = request.RequestId,
            Approved  = true
        }));

    var decision = await approvalTask;
    Assert.True(decision.Approved);
}
```

See [Testing Agents](./testing-agents.md) for the full integration test fixture pattern.

---

## Types Reference

### DurableApprovalRequest

```csharp
// Namespace: Temporalio.Extensions.AI
public sealed record DurableApprovalRequest
{
    public required string RequestId { get; init; }            // must be set explicitly, e.g. Guid.NewGuid().ToString("N")
    public string? FunctionName { get; init; }                 // optional: name of the tool requesting approval
    public string? CallId { get; init; }                       // optional: tool call correlation ID
    public string? Description { get; init; }                  // human-readable description for the reviewer
}
```

### DurableApprovalDecision

```csharp
// Namespace: Temporalio.Extensions.AI
// Used for both the submitted decision and the returned outcome
public sealed record DurableApprovalDecision
{
    public string RequestId { get; init; } = string.Empty;     // must match pending request
    public bool Approved { get; init; }
    public string? Reason { get; init; }                       // reviewer note or timeout message
}
```

---

## Complete Example: Email Approval

The `samples/HumanInTheLoop/` sample implements a full email assistant with HITL approval. Key components:

**Tool definition** — `send_email` pauses for approval before sending:

```csharp
var sendEmailTool = AIFunctionFactory.Create(
    async (string to, string subject, string body) =>
    {
        var decision = await TemporalAgentContext.Current.RequestApprovalAsync(
            new DurableApprovalRequest
            {
                RequestId   = Guid.NewGuid().ToString("N"),
                Description = $"Send email to {to} — Subject: {subject}\n\nBody:\n{body}"
            });

        if (!decision.Approved)
            return $"Email to {to} was rejected ({decision.Reason ?? "no reason"}).";

        // Send the email
        return $"Email sent to {to}.";
    },
    name: "send_email",
    description: "Sends an email. Requires human approval.");
```

**Worker configuration** — 24-hour activity timeout for human review:

```csharp
builder.Services
    .AddHostedTemporalWorker("hitl-sample")
    .AddTemporalAgents(opts =>
    {
        opts.ActivityStartToCloseTimeout = TimeSpan.FromHours(24);
        opts.ActivityHeartbeatTimeout    = TimeSpan.FromMinutes(5);
        opts.AddAIAgent(emailAgent, timeToLive: TimeSpan.FromHours(2));
    });
```

**Console loop** — polls for approvals and collects human decisions:

```csharp
var agentTask = proxy.RunAsync(userMessages, session);

while (!agentTask.IsCompleted)
{
    await Task.Delay(TimeSpan.FromSeconds(1));
    var pending = await client.GetPendingApprovalAsync(sessionId);
    if (pending is null) continue;

    // Display approval request and prompt for decision
    var approved = choice == "approve";
    await client.SubmitApprovalAsync(sessionId, new DurableApprovalDecision
    {
        RequestId = pending.RequestId,
        Approved  = approved,
        Reason    = approved ? null : reason
    });
}

var response = await agentTask;
```

Run it with:

```bash
dotnet run --project samples/HumanInTheLoop
```

---

## References

- `src/Temporalio.Extensions.AI/DurableApprovalRequest.cs` — request type
- `src/Temporalio.Extensions.AI/DurableApprovalDecision.cs` — decision and outcome type
- `src/Temporalio.Extensions.Agents/AgentWorkflow.cs` — HITL update/query handlers
- `src/Temporalio.Extensions.Agents/TemporalAgentContext.cs` — `RequestApprovalAsync` for tools
- `samples/HumanInTheLoop/` — complete working example
- [Usage Guide — HITL](./usage.md#human-in-the-loop-hitl-approval-gates) — quick-start examples
- [Testing Agents](./testing-agents.md) — integration test patterns

---

_Last updated: 2026-03-13_
