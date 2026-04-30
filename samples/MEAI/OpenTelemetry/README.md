# OpenTelemetry: Distributed Tracing for Durable Chat

## Overview

This sample shows how to configure distributed tracing for `Temporalio.Extensions.AI`, producing
a complete span hierarchy from the external `ChatAsync` call through the Temporal protocol layers
down to the LLM activity. It also demonstrates the `DurableAIPlugin` — the plugin-based
registration path — as an alternative to `AddDurableAI()`.

- Full span hierarchy: `durable_chat.send` → `UpdateWorkflow:Chat` → `RunActivity:GetResponse` → `durable_chat.turn`
- `conversation.id` attribute on both the send and turn spans — filter an entire session in one query
- Four `ActivitySource` names must be registered with the tracer provider
- `TracingInterceptor` propagates the W3C `traceparent` header across gRPC boundaries
- Plugin registration path: `AddWorkerPlugin(new DurableAIPlugin(...))` as an alternative to `AddDurableAI()`

## Span Hierarchy

```
durable_chat.send  (conversation.id = <id>)
  └─ UpdateWorkflow:Chat                     ← Temporal SDK protocol span
       └─ RunActivity:GetResponse            ← Temporal SDK protocol span
            └─ durable_chat.turn            ← library semantic span
                   conversation.id, gen_ai.usage.input_tokens,
                   gen_ai.usage.output_tokens
```

## Highlights

- **Four sources, not one.** `DurableChatTelemetry.ActivitySourceName` covers library semantic spans; the three `TracingInterceptor` sources cover Temporal protocol spans. Omitting any one of them produces gaps in your trace.
- **`TracingInterceptor` is required for connected traces.** Without it, Temporal's internal gRPC calls break the distributed trace and the library spans appear disconnected from the protocol spans in your backend.
- **`conversation.id` makes session filtering practical.** Both the client-side `durable_chat.send` span and the worker-side `durable_chat.turn` span carry `conversation.id`, so a single attribute filter surfaces every span for a session across all service instances.
- **`DurableAIPlugin` is the plugin entry point.** Gated by `[Experimental("TAI001")]`, it is equivalent to `AddDurableAI()` and follows the canonical Temporal AI Partner Ecosystem integration pattern. Suppress `TAI001` with `#pragma warning disable TAI001`.

## Getting Started

### Prerequisites

- [.NET 10 SDK](https://dot.net) or later
- A local Temporal server: `temporal server start-dev`
- An OpenAI-compatible API key

### Configure API credentials

```bash
dotnet user-secrets set "OPENAI_API_KEY" "sk-..." --project samples/MEAI/OpenTelemetry
dotnet user-secrets set "OPENAI_API_BASE_URL" "https://api.openai.com/v1" --project samples/MEAI/OpenTelemetry
```

### Run

```bash
dotnet run --project samples/MEAI/OpenTelemetry/OpenTelemetry.csproj
```

### Expected Output

Span data is written to the console by `AddConsoleExporter()`. Look for entries like:

```
Activity.DisplayName: durable_chat.send
    conversation.id: otel-demo-<guid>
Activity.DisplayName: durable_chat.turn
    conversation.id: otel-demo-<guid>
    gen_ai.usage.input_tokens: 42
    gen_ai.usage.output_tokens: 18
```

Filter by `conversation.id = otel-demo-<guid>` to see all spans for the session.
