# Structured Output

How to get typed, deserialized responses from agents using `RunAsync<T>` — including markdown fence stripping, retry-on-failure with LLM self-correction, and the `ChatResponseFormat` alternative.

---

## Table of Contents

1. [Overview](#overview)
2. [RunAsync\<T\> — Recommended Approach](#runasynct--recommended-approach)
3. [How the Retry Loop Works](#how-the-retry-loop-works)
4. [Markdown Code Fence Stripping](#markdown-code-fence-stripping)
5. [StructuredOutputOptions](#structuredoutputoptions)
6. [ChatResponseFormat — Format Hint Only](#chatresponseformat--format-hint-only)
7. [RunAsync\<T\> vs ChatResponseFormat](#runasynct-vs-chatresponseformat)
8. [Available Overloads](#available-overloads)
9. [Common Pitfalls](#common-pitfalls)
10. [ResponseFormat in Workflow State](#responseformat-in-workflow-state)

---

## Overview

LLMs naturally return free-form text. When you need structured data — a typed object, a list of items, a decision enum — you have two options in TemporalAgents:

1. **`RunAsync<T>`** (recommended) — sends a normal prompt, strips markdown fences from the response, deserializes to `T`, and retries with error context if deserialization fails
2. **`ChatResponseFormat`** — tells the LLM to output JSON matching a schema, but does not automatically deserialize or retry

`RunAsync<T>` is the higher-level API that handles the messy reality of LLM output: code fences, trailing text, formatting inconsistencies, and occasional schema violations.

---

## RunAsync\<T\> — Recommended Approach

```csharp
public record WeatherReport(string City, double TemperatureC, string Summary);

var session = await agentProxy.CreateSessionAsync();

WeatherReport report = await agentProxy.RunAsync<WeatherReport>(
    new List<ChatMessage> { new(ChatRole.User, "What's the weather in Seattle?") },
    session);

Console.WriteLine($"{report.City}: {report.TemperatureC}°C — {report.Summary}");
```

What happens under the hood:

1. The agent runs normally, producing a text response
2. `MarkdownCodeFenceHelper.StripMarkdownCodeFences` removes any `` ```json ... ``` `` wrapping
3. `JsonSerializer.Deserialize<T>` attempts to parse the cleaned text
4. If deserialization fails and retries remain, the error is appended to the conversation and the agent is called again
5. The LLM sees its previous (failed) output plus the error, and self-corrects

---

## How the Retry Loop Works

```csharp
for (int attempt = 0; attempt <= options.MaxRetries; attempt++)
{
    var response = await agent.RunAsync(workingMessages, session);
    var stripped = MarkdownCodeFenceHelper.StripMarkdownCodeFences(response.Text);

    try
    {
        return JsonSerializer.Deserialize<T>(stripped, options.JsonSerializerOptions)
            ?? throw new JsonException($"Deserialization returned null for type '{typeof(T).Name}'.");
    }
    catch (JsonException ex) when (attempt < options.MaxRetries)
    {
        if (options.IncludeErrorContext)
        {
            // Append the failed output and error so the LLM can self-correct
            workingMessages.Add(new ChatMessage(ChatRole.Assistant, response.Text));
            workingMessages.Add(new ChatMessage(ChatRole.User,
                $"Your response could not be parsed as valid JSON. Error: {ex.Message}\n" +
                $"Please respond with ONLY valid JSON matching the expected schema " +
                $"for type '{typeof(T).Name}'. Do not wrap it in markdown code fences."));
        }
    }
}
```

**Key behaviors:**

- **Default: 3 total attempts** (1 initial + 2 retries)
- **Error context injection** — the LLM sees the `JsonException` message and a reminder of the target type
- **History accumulates** — each retry adds 2 messages (failed assistant response + user correction), so the agent's context grows. This is why `MaxRetries` defaults to 2, not 10
- **Final attempt throws** — if the last attempt fails, the `JsonException` propagates to the caller
- **Empty responses throw immediately** — no retry for blank output

---

## Markdown Code Fence Stripping

Many LLMs wrap JSON in markdown code fences even when instructed not to:

```markdown
```json
{"city": "Seattle", "temperatureC": 12.5, "summary": "Cloudy"}
`` `
```

`MarkdownCodeFenceHelper` handles this transparently:

1. **Fence detection** — recognizes `` ```json ``, `` ```JSON ``, and plain `` ``` ``
2. **Fence stripping** — extracts content between opening and closing fences
3. **Balanced JSON extraction** — finds the first balanced `{}` or `[]` structure, respecting string escaping and nested braces

This means it also handles responses like:

```
Here's the weather data:

```json
{"city": "Seattle", "temperatureC": 12.5}
`` `

I hope this helps!
```

The helper extracts only `{"city": "Seattle", "temperatureC": 12.5}`.

---

## StructuredOutputOptions

```csharp
var report = await agentProxy.RunAsync<WeatherReport>(
    messages,
    session,
    new StructuredOutputOptions
    {
        MaxRetries = 3,                    // default: 2
        IncludeErrorContext = true,         // default: true
        JsonSerializerOptions = myOptions   // default: null (uses JsonSerializerOptions.Default)
    });
```

| Property | Default | Description |
|----------|---------|-------------|
| `MaxRetries` | 2 | Number of retry attempts after the initial call |
| `IncludeErrorContext` | true | Whether to append the error message and schema reminder on retry |
| `JsonSerializerOptions` | null | Custom serializer options (e.g., for `camelCase` property naming) |

**When to increase `MaxRetries`:** Complex schemas with nested objects, enums, or strict validation constraints. Each retry adds ~2 messages to the context.

**When to disable `IncludeErrorContext`:** If you're using a model that performs worse with verbose error feedback. In practice, this is rare — most models benefit from seeing their mistakes.

**When to set `JsonSerializerOptions`:** When your target type uses `[JsonPropertyName]` attributes or non-default naming policies:

```csharp
var opts = new JsonSerializerOptions
{
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true
};

var result = await agent.RunAsync<MyType>(messages, session,
    new StructuredOutputOptions { JsonSerializerOptions = opts });
```

---

## ChatResponseFormat — Format Hint Only

`ChatResponseFormat` tells the LLM to output JSON matching a schema, but does **not** strip fences, deserialize, or retry:

```csharp
var options = new TemporalAgentRunOptions
{
    ResponseFormat = ChatResponseFormat.ForJsonSchema<WeatherReport>()
};

var session = await agentProxy.CreateSessionAsync();
var response = await agentProxy.RunAsync("What's the weather?", session, options);

// Manual deserialization — no fence stripping, no retry
var report = JsonSerializer.Deserialize<WeatherReport>(response.Text!);
```

This is useful when:
- You want fine-grained control over deserialization
- You need to inspect the raw text before parsing
- The model supports native JSON mode (e.g., OpenAI's `response_format`)

---

## RunAsync\<T\> vs ChatResponseFormat

| | `RunAsync<T>` | `ChatResponseFormat` |
|---|---|---|
| **Fence stripping** | Automatic | Manual |
| **Deserialization** | Automatic | Manual |
| **Retry on failure** | Yes, with error context | No |
| **LLM self-correction** | Yes | No |
| **Per-request scope** | Yes | Yes |
| **Works with all models** | Yes | Requires model JSON mode support |
| **Token overhead** | Retry adds ~2 messages per attempt | None (single attempt) |

**Recommendation:** Use `RunAsync<T>` unless you have a specific reason to handle deserialization yourself. The retry mechanism is what makes it resilient in production — LLMs occasionally produce invalid JSON, and self-correction succeeds on the second attempt in most cases.

---

## Available Overloads

`RunAsync<T>` is available on all three agent types:

### Inside a Workflow (TemporalAIAgent)

```csharp
var agent = TemporalWorkflowExtensions.GetAgent("AnalystAgent");
var session = await agent.CreateSessionAsync();

var analysis = await agent.RunAsync<AnalysisResult>(
    [new ChatMessage(ChatRole.User, "Analyze this data...")],
    session);
```

### External Caller (AIAgent Proxy)

```csharp
var proxy = services.GetTemporalAgentProxy("AnalystAgent");
var session = await proxy.CreateSessionAsync();

var analysis = await proxy.RunAsync<AnalysisResult>(
    [new ChatMessage(ChatRole.User, "Analyze this data...")],
    session);
```

### Via ITemporalAgentClient

```csharp
ITemporalAgentClient client = // resolved from DI
var sessionId = new TemporalAgentSessionId("AnalystAgent", userId);

var analysis = await client.RunAgentAsync<AnalysisResult>(
    sessionId,
    new RunRequest([new ChatMessage(ChatRole.User, "Analyze this data...")]));
```

---

## Common Pitfalls

### Nullable root types

```csharp
// RISKY — if the LLM returns "null", deserialization succeeds but returns null
WeatherReport? report = await agent.RunAsync<WeatherReport?>(messages, session);

// BETTER — use non-nullable T so null results throw JsonException
WeatherReport report = await agent.RunAsync<WeatherReport>(messages, session);
```

`RunAsync<T>` explicitly throws when deserialization returns `null` to prevent silent null propagation.

### Union types and polymorphism

System.Text.Json does not natively deserialize polymorphic types without a discriminator. If your target type has abstract base classes or interfaces, configure a custom `JsonSerializerOptions` with a `JsonDerivedType` attribute or converter.

### Large schemas

Very complex schemas (deeply nested objects, many optional fields) increase the chance of the LLM producing invalid JSON on the first attempt. Consider:
- Increasing `MaxRetries` to 3
- Simplifying the target type (flatten nested structures)
- Adding `[JsonPropertyName]` attributes with short, clear names

### ResponseFormat is per-request, not per-session

Setting `ResponseFormat` on one `RunAsync` call does **not** carry over to subsequent calls in the same session:

```csharp
// Only this call uses JSON format — the next RunAsync returns text
var options = new TemporalAgentRunOptions
{
    ResponseFormat = ChatResponseFormat.ForJsonSchema<WeatherReport>()
};
await proxy.RunAsync("Weather?", session, options);

// This call returns normal text
await proxy.RunAsync("Tell me more", session);
```

---

## ResponseFormat in Workflow State

When `ChatResponseFormat` is used, it's serialized into the conversation history as part of `AgentSessionRequest` (the MAF-specific subclass of `DurableSessionRequest` from `Temporalio.Extensions.AI`):

- `ResponseType`: `"json"` or `"text"`
- `ResponseSchema`: the JSON schema as a `JsonElement` (for `ChatResponseFormatJson`)
- `Messages`: the request messages as `ChatMessage[]` (MEAI type, stored directly on the shared `DurableSessionEntry` base)

These two response-format fields are MAF-specific — they live on `AgentSessionRequest`, not on the shared base type, since the AI library has no analog for structured output today. On replay the same format hint and messages are sent to the LLM, preserving determinism. `ChatMessage`/`AIContent` polymorphism is preserved end-to-end via `DurableAIDataConverter`.

### Threading a correlation ID through structured-output runs

`StructuredOutputExtensions.RunAsync<T>` accepts an optional `correlationId` parameter directly (it's an extension surface owned by this library, so the correlation ID is a first-class parameter rather than going through `TemporalAgentRunOptions`):

```csharp
var report = await proxy.RunAsync<WeatherReport>(
    messages,
    session,
    correlationId: "request-abc-123");  // appears on the AgentSessionRequest entry
```

When omitted, the workflow auto-generates one via `Workflow.NewGuid()` (or `Guid.NewGuid()` outside workflow context).

---

## References

- `src/Temporalio.Extensions.Agents/StructuredOutputExtensions.cs` — `RunAsync<T>` implementation
- `src/Temporalio.Extensions.Agents/StructuredOutputOptions.cs` — configuration options
- `src/Temporalio.Extensions.Agents/MarkdownCodeFenceHelper.cs` — fence stripping logic
- `tests/Temporalio.Extensions.Agents.Tests/MarkdownCodeFenceHelperTests.cs` — 11 edge-case tests
- `tests/Temporalio.Extensions.Agents.Tests/StructuredOutputOptionsTests.cs` — option validation tests
- [Usage Guide — Structured Output](./usage.md#structured-output) — quick-start examples

---

_Last updated: 2026-04-30_
