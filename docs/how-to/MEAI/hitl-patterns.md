# Human-in-the-Loop Patterns — Temporalio.Extensions.AI

## Overview

Human-in-the-Loop (HITL) in `Temporalio.Extensions.AI` lets a tool call pause the entire conversation workflow and wait for a human decision before proceeding. The workflow blocks at a `WaitConditionAsync` — Temporal persists all state durably — and only resumes once a decision arrives via `SubmitApprovalAsync`. There is no polling loop, no database, and no lost state if the worker crashes while waiting.

---

## The Approval Flow

```
User message → ChatAsync → DurableChatWorkflow → DurableChatActivities
                                                        │
                                    UseFunctionInvocation runs the tool-call loop
                                                        │
                                              your tool runs
                                                        │
                                   tool calls RequestApproval [WorkflowUpdate]
                                                        │
                                   DurableChatWorkflow.RequestApprovalAsync
                                       stores DurableApprovalRequest
                                       blocks on WaitConditionAsync ←──── Human submits
                                                        │                decision via
                                   external system polls GetPendingApprovalAsync  SubmitApprovalAsync
                                                        │
                                        WaitConditionAsync unblocks
                                        RequestApprovalAsync returns DurableApprovalDecision
                                                        │
                                             tool receives decision
                                          acts on it (proceed or cancel)
                                                        │
                                    UseFunctionInvocation sends result to LLM
                                          LLM generates final response
                                                        │
                                              ChatAsync returns
```

---

## Setup

HITL requires no special configuration beyond the standard `AddDurableAI` registration. Set `ApprovalTimeout` and `ActivityTimeout` long enough to accommodate human review time.

```csharp
builder.Services
    .AddHostedTemporalWorker(temporalAddress, "default", taskQueue)
    .AddDurableAI(opts =>
    {
        // Both must outlast the maximum expected review window.
        opts.ActivityTimeout  = TimeSpan.FromHours(24);
        opts.HeartbeatTimeout = TimeSpan.FromMinutes(5);
        opts.ApprovalTimeout  = TimeSpan.FromHours(24);
        opts.SessionTimeToLive = TimeSpan.FromHours(2);
    });
```

> **Why must `ActivityTimeout` be long?** The tool executes inside a `DurableChatActivities` activity. While the workflow is blocked waiting for approval, the activity remains alive (heartbeating). If the activity times out before the human responds, Temporal retries it — which re-sends the approval request. Set `ActivityTimeout` to at least as long as `ApprovalTimeout` to avoid spurious retries.

---

## Writing an Approval-Gated Tool

A tool requests approval by sending a `RequestApproval` workflow update directly to the running `DurableChatWorkflow`. The workflow's update handler blocks until a human submits a decision.

```csharp
var deleteTool = AIFunctionFactory.Create(
    async (
        [Description("Age threshold in days; records older than this are deleted")]
        int olderThanDays) =>
    {
        // 1. Build the approval request
        var request = new DurableApprovalRequest
        {
            RequestId    = Guid.NewGuid().ToString("N"),
            FunctionName = "delete_records",
            Description  = $"Permanently delete records older than {olderThanDays} days. Cannot be undone.",
        };

        // 2. Send the RequestApproval update — this call blocks until the human responds.
        //    The workflow ID is "{WorkflowIdPrefix}{conversationId}" (default prefix: "chat-").
        var workflowId = $"chat-{conversationId}";
        var handle = temporalClient.GetWorkflowHandle(workflowId);

        var decision = await handle.ExecuteUpdateAsync<DurableApprovalDecision>(
            "RequestApproval",
            new object[] { request });

        // 3. Act on the decision
        if (!decision.Approved)
            return $"Deletion rejected ({decision.Reason ?? "no reason given"}). No records deleted.";

        // perform the operation
        return $"Deleted all records older than {olderThanDays} days.";
    },
    name: "delete_records",
    description: "Deletes records older than the given number of days. Requires human approval.");
```

Then pass the tool to `ChatAsync` via `ChatOptions`:

```csharp
var response = await sessionClient.ChatAsync(
    conversationId,
    [systemMessage, new ChatMessage(ChatRole.User, userRequest)],
    options: new ChatOptions { Tools = [deleteTool] });
```

> **Note on workflow ID construction:** The workflow ID follows the pattern `"{WorkflowIdPrefix}{conversationId}"`. The default prefix is `"chat-"` (from `DurableExecutionOptions.WorkflowIdPrefix`). If you change the prefix in `AddDurableAI`, update your tool code to match. `DurableChatWorkflow` is `internal`, so the tool must use the untyped `GetWorkflowHandle(workflowId)` overload and reference the update by its registered name `"RequestApproval"`.

---

## Polling for Pending Approvals

From your external system (API server, background job, etc.) poll `GetPendingApprovalAsync` — a `[WorkflowQuery]` that returns instantly without blocking the workflow:

```csharp
DurableApprovalRequest? pending = null;

while (pending is null)
{
    await Task.Delay(TimeSpan.FromSeconds(2));

    try
    {
        pending = await sessionClient.GetPendingApprovalAsync(conversationId);
    }
    catch (Temporalio.Exceptions.RpcException ex)
        when (ex.Code == Temporalio.Exceptions.RpcException.StatusCode.NotFound)
    {
        // Workflow has not started yet — retry on the next tick.
        continue;
    }
}

// pending is now non-null: show it to the reviewer
Console.WriteLine($"Approval needed: {pending.FunctionName} — {pending.Description}");
```

`GetPendingApprovalAsync` returns `null` when no approval is pending (the conversation is idle or has already been resolved). `RpcException.NotFound` means the workflow has not written its first event yet — safe to retry.

---

## Submitting the Decision

Once the reviewer has made a decision, call `SubmitApprovalAsync`. The `RequestId` must match the one in `DurableApprovalRequest`:

```csharp
var decision = new DurableApprovalDecision
{
    RequestId = pending.RequestId,
    Approved  = true,
    Reason    = "Verified by compliance team.",
};

await sessionClient.SubmitApprovalAsync(conversationId, decision);
```

After `SubmitApprovalAsync` returns, the workflow's `WaitConditionAsync` is satisfied. `RequestApprovalAsync` returns the decision to the tool, the tool completes, `UseFunctionInvocation` sends the result to the LLM, and `ChatAsync` eventually returns the final response.

---

## Timeout Behavior

`ApprovalTimeout` (default: 7 days) is the maximum time the workflow waits for a human response. If no decision arrives within that window, the workflow auto-rejects with a descriptive reason:

```
Approval request timed out after 7 days — auto-rejected.
```

The tool receives this rejection as a `DurableApprovalDecision` with `Approved = false`, and can return an appropriate message to the LLM.

---

## Integration with External Systems

Replace the polling loop with your preferred delivery mechanism:

**REST webhook**
```
1. External system receives RequestApproval event (e.g., via Temporal visibility query)
2. System POSTs DurableApprovalRequest to a review service
3. Reviewer clicks Approve/Reject in the review UI
4. Review service calls sessionClient.SubmitApprovalAsync(conversationId, decision)
```

**Slack**
```
1. Slack bot receives the pending request (polled or event-driven)
2. Bot posts a message with Approve/Reject action buttons
3. User clicks a button → Slack sends action payload to your handler
4. Handler calls sessionClient.SubmitApprovalAsync(conversationId, decision)
```

**Email**
```
1. Your system sends an email with a signed approval URL
2. Reviewer clicks the URL → hits your approval endpoint
3. Endpoint calls sessionClient.SubmitApprovalAsync(conversationId, decision)
```

The workflow waits durably regardless of which mechanism you use. It does not care how the decision arrives — only that `SubmitApprovalAsync` is eventually called with the correct `RequestId`.

---

## HITL Types Reference

### `DurableApprovalRequest`

Stored in workflow state when a tool calls the `RequestApproval` update.

| Property | Type | Description |
|---|---|---|
| `RequestId` | `string` | Unique ID — must be echoed back in the `DurableApprovalDecision` |
| `FunctionName` | `string?` | The tool function name that needs approval |
| `CallId` | `string?` | Tool call ID from the LLM's function call request |
| `Description` | `string?` | Human-readable description of what the tool will do |

### `DurableApprovalDecision`

Submitted by the external reviewer via `SubmitApprovalAsync`.

| Property | Type | Description |
|---|---|---|
| `RequestId` | `string` | Must match `DurableApprovalRequest.RequestId` |
| `Approved` | `bool` | Whether the tool call is approved |
| `Reason` | `string?` | Optional human-readable explanation |

---

## Runnable Example

`samples/MEAI/HumanInTheLoop/` contains a complete end-to-end demo. The sample uses a data management assistant that can delete records but requires explicit human approval. The main loop auto-approves after detecting the pending request, demonstrating the full flow in a single process.

```bash
# Prerequisites: temporal server start-dev, OPENAI_API_KEY in appsettings.local.json
dotnet run --project samples/MEAI/HumanInTheLoop/HumanInTheLoop.csproj
```
