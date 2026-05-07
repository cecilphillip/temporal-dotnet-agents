# Human-in-the-Loop: Approval Gates for Agent Actions

## Overview

Demonstrates how to pause an agent mid-turn and wait for a human decision before proceeding. The `send_email` tool suspends itself inside a running activity by issuing a `[WorkflowUpdate]`, and the workflow blocks on `WaitConditionAsync` until an external caller submits an approval or rejection.

This sample demonstrates:
- `TemporalAgentContext.Current.RequestApprovalAsync()` suspending a tool inside an activity
- `ITemporalAgentClient.GetPendingApprovalAsync()` polling for pending approvals from outside the workflow
- `ITemporalAgentClient.SubmitApprovalAsync()` unblocking the workflow with a decision
- `ActivityTimeout` set to 24 hours to accommodate human review time

## Architecture

```
User input
    │
    ▼
proxy.RunAsync(messages, session)            ← [WorkflowUpdate] to AgentWorkflow
    │
    ├─ AgentActivities.RunDurableAgentStepAsync()   ← LLM call (returns FunctionCallContent)
    │
    └─ AgentActivities.InvokeAgentToolAsync()       ← activity per tool (24h timeout)
           │
           └─ send_email tool invoked
                  │
                  └─ RequestApprovalAsync()   ← [WorkflowUpdate]: sends DurableApprovalRequest
                         │                       workflow blocks on WaitConditionAsync
                         │
                  ┌──────┴──────────────────────────────────────┐
                  │  Human review (console in this sample)       │
                  │  client.GetPendingApprovalAsync(sessionId)   │  ← [WorkflowQuery]
                  │  client.SubmitApprovalAsync(sessionId, ...)  │  ← [WorkflowUpdate]
                  └──────┬──────────────────────────────────────┘
                         │
                  WaitConditionAsync satisfied → tool resumes
                         │
                  email sent (or rejected) → result returns to workflow,
                                             next RunDurableAgentStepAsync iteration runs
```

## Highlights

- **Suspension without polling.** The workflow blocks on `WaitConditionAsync` — no spin-wait, no timer. The worker thread is released and other workflows continue normally while waiting.
- **`GetPendingApprovalAsync` is a `[WorkflowQuery]`.** Queries never block the workflow and are safe to call as frequently as needed. This sample polls every second from outside the workflow while the agent task is in-flight.
- **`SubmitApprovalAsync` is a `[WorkflowUpdate]`.** Strongly consistent: it validates the `RequestId` matches the pending request before unblocking, preventing stale or duplicate decisions.
- **Activity timeout must cover human review time.** `ActivityTimeout = TimeSpan.FromHours(24)` gives reviewers a full day. A heartbeat timeout of 5 minutes ensures the worker is still alive. Both are set in `AddTemporalAgents()`.

## Getting Started

### Prerequisites

- [.NET 10 SDK](https://dot.net) or later
- A local Temporal server: `temporal server start-dev`
- An OpenAI-compatible API key
- This sample waits for you to type `approve` or `reject` at the console — do not run it with piped stdin

### Configure API credentials

```bash
dotnet user-secrets set "OPENAI_API_KEY" "sk-..." --project samples/MAF/HumanInTheLoop
dotnet user-secrets set "OPENAI_API_BASE_URL" "https://api.openai.com/v1" --project samples/MAF/HumanInTheLoop
```

### Run

```bash
dotnet run --project samples/MAF/HumanInTheLoop/HumanInTheLoop.csproj
```

### Expected Output

```
Email Assistant — HITL Approval Sample
  Ask the assistant to send an email.
  When it tries, you will be prompted to approve or reject before it is delivered.
  Type 'quit' to exit.

You: Send an email to alice@example.com saying the meeting is at 3pm
Assistant: (thinking...)

  ╔══════════════════════════════════════════════╗
  ║            APPROVAL REQUIRED                 ║
  ╠══════════════════════════════════════════════╣
  ║  Send email to alice@example.com             ║
  ║  Subject: Meeting at 3pm                     ║
  ╚══════════════════════════════════════════════╝
  Decision [approve/reject]: approve

  Approved — agent is resuming...

  [EMAIL SENT] To: alice@example.com
               Subject: Meeting at 3pm
Assistant: The email has been sent to alice@example.com.
```