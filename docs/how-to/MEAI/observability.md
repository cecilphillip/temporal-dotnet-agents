# Observability — Temporalio.Extensions.AI

## Overview

Two instrumentation layers compose when you use `Temporalio.Extensions.AI`:

- **`DurableChatTelemetry`** — AI-semantic spans for conversation turns, token counts, and tool invocations. Uses the `ActivitySource` named `"Temporalio.Extensions.AI"`.
- **`TracingInterceptor`** — Temporal protocol spans for workflow starts, updates, and activity scheduling. Uses three `ActivitySource` instances owned by `Temporalio.Extensions.OpenTelemetry`.

These are independent `ActivitySource` instances. They propagate W3C trace context through Temporal's gRPC calls, so they compose into a single distributed trace automatically — no extra configuration is needed beyond registering all four sources with your `TracerProvider`.

---

## The Four ActivitySource Names

All four sources must be registered with `TracerProvider.AddSource(...)` to capture the complete trace.

| Constant | Source name | Spans emitted |
|---|---|---|
| `DurableChatTelemetry.ActivitySourceName` | `"Temporalio.Extensions.AI"` | `durable_chat.send`, `durable_chat.turn`, `durable_function.invoke` |
| `TracingInterceptor.ClientSource.Name` | `"Temporalio.Client"` | `UpdateWorkflow:*`, `QueryWorkflow:*`, `StartWorkflow:*` |
| `TracingInterceptor.WorkflowsSource.Name` | `"Temporalio.Workflows"` | Workflow execution spans (suppressed on replay) |
| `TracingInterceptor.ActivitiesSource.Name` | `"Temporalio.Activities"` | `RunActivity:*` |

Registering only `"Temporalio.Extensions.AI"` gives you the AI-semantic spans but the trace will appear disconnected — the `durable_chat.send` and `durable_chat.turn` spans will have no common parent. Registering all four gives the full picture: a single root span that spans from client call to LLM response, with the Temporal scheduling envelope visible in between.

---

## Full Span Hierarchy

### Standard chat turn (no tools)

```
durable_chat.send                         ← DurableChatSessionClient (client process)
│   conversation.id
│   gen_ai.request.model
│   gen_ai.response.model
│   gen_ai.usage.input_tokens
│   gen_ai.usage.output_tokens
│
└─ UpdateWorkflow:Chat                    ← TracingInterceptor (client side)
     │
     └─ RunActivity:Temporalio.Extensions.AI.GetResponse   ← TracingInterceptor (worker side)
          │
          └─ durable_chat.turn            ← DurableChatActivities (worker process)
                 conversation.id
                 gen_ai.response.model
                 gen_ai.usage.input_tokens
                 gen_ai.usage.output_tokens
```

### Chat turn with a durable tool call

When a tool registered via `AddDurableTools()` is invoked, an additional span appears as a child of `durable_chat.turn`:

```
durable_chat.send
└─ UpdateWorkflow:Chat
     └─ RunActivity:Temporalio.Extensions.AI.GetResponse
          └─ durable_chat.turn
               └─ RunActivity:Temporalio.Extensions.AI.InvokeFunction   ← TracingInterceptor
                    └─ durable_function.invoke                           ← DurableFunctionActivities
                           gen_ai.tool.name
                           gen_ai.tool.call_id
```

> **Note:** `durable_function.invoke` is emitted inside the `InvokeFunction` activity, so it is always a child of the `RunActivity:Temporalio.Extensions.AI.InvokeFunction` span. Multi-tool responses produce one `durable_function.invoke` span per tool call, each as a separate activity execution.

---

## Span Attribute Reference

| Constant | Attribute key | Appears on | Description |
|---|---|---|---|
| `DurableChatTelemetry.ConversationIdAttribute` | `conversation.id` | `durable_chat.send`, `durable_chat.turn` | The conversation/session identifier passed to `ChatAsync`. Use this to filter all spans for a single session. |
| `DurableChatTelemetry.RequestModelAttribute` | `gen_ai.request.model` | `durable_chat.send` | The model ID from `ChatOptions.ModelId`, if set. |
| `DurableChatTelemetry.ResponseModelAttribute` | `gen_ai.response.model` | `durable_chat.send`, `durable_chat.turn` | The model ID returned in the LLM response. |
| `DurableChatTelemetry.InputTokensAttribute` | `gen_ai.usage.input_tokens` | `durable_chat.send`, `durable_chat.turn` | Prompt token count from `ChatResponse.Usage.InputTokenCount`. |
| `DurableChatTelemetry.OutputTokensAttribute` | `gen_ai.usage.output_tokens` | `durable_chat.send`, `durable_chat.turn` | Completion token count from `ChatResponse.Usage.OutputTokenCount`. |
| `DurableChatTelemetry.ToolNameAttribute` | `gen_ai.tool.name` | `durable_function.invoke` | The `AIFunction` name being invoked. |
| `DurableChatTelemetry.ToolCallIdAttribute` | `gen_ai.tool.call_id` | `durable_function.invoke` | The tool call ID assigned by the LLM in the response. |

Token count attributes on `durable_chat.send` are set after the update returns, so they reflect the completed turn. Token count attributes on `durable_chat.turn` are set directly from the activity's `ChatResponse`, making them the authoritative source for cost attribution.

---

## Setup — Registering OpenTelemetry

### NuGet packages

```xml
<!-- Temporal OTel extension — provides TracingInterceptor -->
<PackageReference Include="Temporalio.Extensions.OpenTelemetry" Version="1.11.1" />

<!-- OTel hosting integration -->
<PackageReference Include="OpenTelemetry.Extensions.Hosting" Version="1.11.0" />

<!-- Choose an exporter -->
<PackageReference Include="OpenTelemetry.Exporter.Console" Version="1.11.0" />   <!-- dev -->
<PackageReference Include="OpenTelemetry.Exporter.OpenTelemetryProtocol" Version="1.11.0" />  <!-- prod -->
```

`TracingInterceptor` lives in `Temporalio.Extensions.OpenTelemetry`. `DurableChatTelemetry` lives in `Temporalio.Extensions.AI` (this library).

### Register the TracerProvider

```csharp
// Both: set up in any process you want traced — worker and external caller alike
builder.Services
    .AddOpenTelemetry()
    .WithTracing(tracing => tracing
        .AddSource(DurableChatTelemetry.ActivitySourceName)      // "Temporalio.Extensions.AI"
        .AddSource(TracingInterceptor.ClientSource.Name)         // "Temporalio.Client"
        .AddSource(TracingInterceptor.WorkflowsSource.Name)      // "Temporalio.Workflows"
        .AddSource(TracingInterceptor.ActivitiesSource.Name)     // "Temporalio.Activities"
        .AddConsoleExporter());  // or .AddOtlpExporter()
```

### Connect the Temporal client

```csharp
// Both
var client = await TemporalClient.ConnectAsync(new TemporalClientConnectOptions("localhost:7233")
{
    DataConverter = DurableAIDataConverter.Instance,
    Interceptors = [new TracingInterceptor()],
    Namespace = "default",
});
builder.Services.AddSingleton<ITemporalClient>(client);
```

> **Note:** Both `DurableAIDataConverter.Instance` and `new TracingInterceptor()` are required, and they serve completely different purposes. `DurableAIDataConverter.Instance` configures Temporal's payload converter to use `AIJsonUtilities.DefaultOptions`, which preserves the `$type` discriminator that MEAI uses for polymorphic `AIContent` subclasses (`TextContent`, `FunctionCallContent`, etc.). Without it, type information is silently lost when messages round-trip through workflow history, causing deserialization errors on replay. `TracingInterceptor` propagates W3C trace context through Temporal's internal gRPC calls so that the spans produced by this library and the Temporal SDK form a single connected trace tree. Neither substitutes for the other.

### Register the worker

```csharp
// Worker
builder.Services
    .AddHostedTemporalWorker(temporalAddress, "default", "my-task-queue")
    .AddDurableAI(opts =>
    {
        opts.ActivityTimeout = TimeSpan.FromMinutes(5);
        opts.SessionTimeToLive = TimeSpan.FromHours(24);
    });
```

---

## Exporter Examples

### Console exporter (development and debugging)

The console exporter prints each completed span to stdout. This is useful for verifying the span hierarchy locally without running a collector.

```csharp
// Both
builder.Services
    .AddOpenTelemetry()
    .WithTracing(tracing => tracing
        .AddSource(DurableChatTelemetry.ActivitySourceName)
        .AddSource(TracingInterceptor.ClientSource.Name)
        .AddSource(TracingInterceptor.WorkflowsSource.Name)
        .AddSource(TracingInterceptor.ActivitiesSource.Name)
        .AddConsoleExporter());
```

### OTLP exporter (production — Jaeger, Grafana Tempo, Honeycomb, etc.)

```csharp
// Both
builder.Services
    .AddOpenTelemetry()
    .WithTracing(tracing => tracing
        .AddSource(DurableChatTelemetry.ActivitySourceName)
        .AddSource(TracingInterceptor.ClientSource.Name)
        .AddSource(TracingInterceptor.WorkflowsSource.Name)
        .AddSource(TracingInterceptor.ActivitiesSource.Name)
        .AddOtlpExporter(otlp =>
        {
            otlp.Endpoint = new Uri("http://localhost:4317");  // gRPC OTLP endpoint
        }));
```

For HTTP/Protobuf instead of gRPC:

```csharp
// Both
otlp.Endpoint = new Uri("http://localhost:4318/v1/traces");
otlp.Protocol = OtlpExportProtocol.HttpProtobuf;
```

You can register both exporters simultaneously during a migration or for local verification alongside production export:

```csharp
// Both
.AddConsoleExporter()
.AddOtlpExporter()
```

---

## What Each Span Tells You

**`durable_chat.send` duration** approximates total end-to-end latency from the caller's perspective. It begins when `ChatAsync` is called and ends after the workflow update returns. This includes network round-trips to the Temporal server, activity scheduling, queue time, LLM inference, and the return path. Use this span's duration for SLA measurement.

**`durable_chat.turn` duration** approximates actual LLM inference time (plus any synchronous tool call round-trips handled by `UseFunctionInvocation()` in the middleware pipeline). This is the time your activity spent calling `IChatClient.GetResponseAsync`. Use this span for LLM latency analysis.

**Gap between `durable_chat.send` and `durable_chat.turn`** reflects Temporal scheduling overhead — the time from when the client issued the update to when a worker picked up the activity. Under normal conditions this is low (milliseconds). A widening gap indicates worker capacity pressure or Temporal server latency.

**Token counts on `durable_chat.turn`** (`gen_ai.usage.input_tokens`, `gen_ai.usage.output_tokens`) come directly from `ChatResponse.Usage` and are the authoritative source for cost attribution per conversation turn. The same values are also copied onto `durable_chat.send` after the update returns, so you can access them from either span.

**`conversation.id`** is set on both `durable_chat.send` and `durable_chat.turn`. In a backend like Jaeger or Grafana Tempo you can filter all traces for a specific session by querying `conversation.id = "your-session-id"`. This is the primary mechanism for correlating all turns of a multi-turn conversation.

**`durable_function.invoke` per-tool span** tells you which tool was called, its call ID, and its duration. If a single LLM turn triggers multiple tool calls they appear as sibling `durable_function.invoke` spans under the same `durable_chat.turn`, making it straightforward to measure per-tool latency and identify slow tools.

---

## Activity Summaries in the Temporal Web UI

Beyond OpenTelemetry spans, the library also populates `ActivityOptions.Summary` at every activity dispatch site. The summary value is rendered in the Temporal Web UI activity list — clicking into a workflow shows each activity row with its summary alongside the activity type, making it easy to scan which model handled a turn or which tool a turn invoked without opening individual events.

This is fully automatic. There is no public API knob to override the summary; the values are derived from the call site:

| Activity | Summary value |
|---|---|
| Chat (`DurableChatActivities.GetResponseAsync`, streaming and non-streaming) | `chatOptions.ModelId` (e.g., `"gpt-4o-mini"`) |
| Tool (`DurableFunctionActivities.InvokeFunctionAsync` via `DurableAIFunction`) | function `Name` (e.g., `"GetWeather"`) |
| Embedding (`DurableEmbeddingActivities.GenerateAsync`) | `EmbeddingGenerationOptions.ModelId` |

When the underlying field is null or empty the summary is omitted (no padding) — the activity row still appears, just without the summary label. HITL approval is implemented as a `[WorkflowUpdate]`, not an activity, so it has no `Summary` site.

This follows the Temporal AI Partner Ecosystem Guide's recommendation: *"For debuggability, pass in a Summary when you call an activity, such as a summary of the user's query or the choice of LLM model. This will improve the rendering in Temporal UI."* For the full UI mechanics see [Enriching the UI — adding Summary to activities and timers](https://docs.temporal.io/develop/dotnet/platform/enriching-ui#adding-summary-to-activities-and-timers).

Activity summaries are independent of OpenTelemetry — they are written into Temporal event history directly by the SDK and require no exporter or additional configuration.

---

## Reference

`samples/MEAI/OpenTelemetry/` contains a runnable end-to-end example that demonstrates the full span hierarchy using the console exporter. Run it with:

```bash
# Prerequisites: temporal server start-dev, OPENAI_API_KEY set via dotnet user-secrets (see README for setup)
dotnet run --project samples/MEAI/OpenTelemetry/DurableOpenTelemetry.csproj
```

The console exporter output will show each span with its attributes as the multi-turn demo conversation executes. Search for `durable_chat.send` in the output to find the root span, then trace downward through `UpdateWorkflow:Chat` → `RunActivity:Temporalio.Extensions.AI.GetResponse` → `durable_chat.turn` to see the full hierarchy.
