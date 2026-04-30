# DurableTools: Per-Tool Activity Dispatch

## Overview

This sample demonstrates `AsDurable()`, which wraps an `AIFunction` so that each tool invocation
dispatches as its own independent Temporal activity rather than running inline inside the LLM
activity. Each tool call gets its own retry policy, timeout, and event history entry — visible
individually in the Temporal Web UI.

- `AsDurable()` — wraps any `AIFunction` so workflow context triggers `DurableFunctionActivities`
- `AddDurableTools()` — registers functions in `DurableFunctionRegistry` on the worker
- `WeatherReportWorkflow` — custom workflow that calls a durable tool directly (not via `DurableChatSessionClient`)
- Per-tool retry isolation: a failing tool is retried without re-running the LLM call
- Contrast with `UseFunctionInvocation()`, where the entire LLM + tool loop is one activity

## Architecture

```
Program.cs
    │
    ├─ AddDurableTools(weatherTool)  →  DurableFunctionRegistry["get_current_weather"]
    │
    └─ DurableToolDemo.RunAsync()
           │
           └─ WeatherReportWorkflow.RunAsync()
                  │
                  └─ durableWeather.InvokeAsync()  →  DurableFunctionActivities
                                                       └─ registry["get_current_weather"]
                                                              └─ GetCurrentWeather(city)
```

## Highlights

- **Two invocation models.** `UseFunctionInvocation()` runs the full LLM + tool loop as one activity (simpler). `AsDurable()` gives each tool its own activity (finer retry granularity). Choose based on whether individual tool retries matter.
- **Stub inner function.** The lambda passed to `AIFunctionFactory.Create` inside `WeatherReportWorkflow` is never reached — `Workflow.InWorkflow == true` intercepts the call before the stub executes. The real implementation lives in `Program.cs` and is resolved from the `DurableFunctionRegistry` by name.
- **Registry lookup by name.** `DurableFunctionActivities` resolves functions from `DurableFunctionRegistry` using the function's `Name` property as the key. The name must match between the workflow-side stub and the `AddDurableTools` registration.
- **No `DurableChatSessionClient` required.** `AsDurable()` works in any `[Workflow]` class — you are not limited to the stock `DurableChatWorkflow` session model.

## Getting Started

### Prerequisites

- [.NET 10 SDK](https://dot.net) or later
- A local Temporal server: `temporal server start-dev`
- An OpenAI-compatible API key

### Configure API credentials

```bash
dotnet user-secrets set "OPENAI_API_KEY" "sk-..." --project samples/MEAI/DurableTools
dotnet user-secrets set "OPENAI_API_BASE_URL" "https://api.openai.com/v1" --project samples/MEAI/DurableTools
```

### Run

```bash
dotnet run --project samples/MEAI/DurableTools/DurableTools.csproj
```

### Expected Output

```
Worker started.

════════════════════════════════════════════════════════
 AsDurable() — Per-Tool Activity Dispatch
════════════════════════════════════════════════════════
 Each tool call is a separate Temporal activity with its
 own retry policy, timeout, and event history entry.

 Workflow ID: weather-report-<guid>
 City      : Tokyo

 Result: It's sunny and 22 °C in Tokyo.
════════════════════════════════════════════════════════
```
