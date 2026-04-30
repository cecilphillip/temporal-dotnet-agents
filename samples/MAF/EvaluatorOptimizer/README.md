# Evaluator-Optimizer: Iterative Draft Refinement

## Overview

Demonstrates the evaluator-optimizer agentic pattern: a Generator agent produces drafts and an Evaluator agent reviews them, providing feedback until the draft is approved or a maximum iteration count is reached. Both agents run as durable Temporal activities inside a single orchestrating workflow.

This sample demonstrates:
- Two-agent collaboration inside `EvaluatorOptimizerWorkflow`
- A feedback loop that runs up to `maxIterations` (default: 3) cycles
- Durable iteration: completed generation and evaluation turns are replayed from history on crash/restart
- Loop termination when the Evaluator responds with `"APPROVED"`

## Architecture

```
EvaluatorOptimizerWorkflow.RunAsync(task, maxIterations)
    │
    ├─ loop (up to maxIterations)
    │     │
    │     ├─ GetAgent("Generator").RunAsync(prompt)    ← activity (cached in history)
    │     │     └─ produces: draft text
    │     │
    │     ├─ GetAgent("Evaluator").RunAsync(draft)     ← activity (cached in history)
    │     │     └─ produces: "APPROVED" or feedback
    │     │
    │     └─ if "APPROVED" → break; else → revise prompt with feedback
    │
    └─ return final draft
```

## Highlights

- **Each LLM call is independently retried.** Generator and Evaluator each run as separate `ExecuteActivityAsync` calls. A transient failure in one is retried without re-running the other.
- **Durable loop.** If the worker crashes mid-iteration, Temporal replays all completed activity results from event history. The loop resumes at the correct iteration with no duplicate LLM calls.
- **Full revision history preserved.** Every draft and every piece of feedback is recorded in the workflow event history — visible in the Temporal Web UI for debugging and auditing.
- **Independent sessions.** Generator and Evaluator each get their own `CreateSessionAsync()` call, keeping their conversation histories separate.

## Getting Started

### Prerequisites

- [.NET 10 SDK](https://dot.net) or later
- A local Temporal server: `temporal server start-dev`
- An OpenAI-compatible API key

### Configure API credentials

```bash
dotnet user-secrets set "OPENAI_API_KEY" "sk-..." --project samples/MAF/EvaluatorOptimizer
dotnet user-secrets set "OPENAI_API_BASE_URL" "https://api.openai.com/v1" --project samples/MAF/EvaluatorOptimizer
```

### Run

```bash
dotnet run --project samples/MAF/EvaluatorOptimizer/EvaluatorOptimizer.csproj
```

### Expected Output

```
Worker started. Submitting EvaluatorOptimizer workflow...

Workflow started: eval-opt-<guid>

Task: Write a concise (100-word) explanation of how Temporal workflows achieve fault tolerance.

── Final Draft ─────────────────────────────────────────────
Temporal workflows achieve fault tolerance through durable execution...
────────────────────────────────────────────────────────────

Done.
```
