# DurableChat: Multi-Turn Durable Conversations

## Overview

This sample demonstrates how to make a multi-turn chat session durable using `DurableChatSessionClient`.
Each call to `ChatAsync` issues a `[WorkflowUpdate]` against a long-lived Temporal workflow, so conversation
history survives worker restarts without any extra persistence code. Three demos run in sequence:
multi-turn context carry-over, tool calls, and history retrieval.

- `DurableChatSessionClient.ChatAsync` — each message is a workflow update, not a bare HTTP call
- Conversation ID maps 1:1 to a Temporal workflow ID — the same ID routes all turns to the same instance
- `GetHistoryAsync` retrieves the full message log, including tool call and tool result entries
- `DurableAIDataConverter.Instance` preserves MEAI's `$type` discriminator across workflow history round-trips
- `UseFunctionInvocation()` handles the LLM tool-call loop inside the activity — tool results are sent back to the model before the activity returns

## Highlights

- **Context carry-over without resending history.** The second question ("What is the population of that city?") is answerable because the workflow holds the prior exchange. The caller sends only the new message — the workflow supplies the full history to the LLM automatically.
- **`DurableAIDataConverter` is required.** Without it, `FunctionCallContent`, `FunctionResultContent`, and other `AIContent` subtypes lose their `$type` discriminator when serialized into workflow history, causing deserialization errors on replay.
- **Tool calls stay inside the activity.** `UseFunctionInvocation()` completes the full request-call-result loop as one Temporal activity. This is the right model when you want simplicity — see the DurableTools sample if you need per-tool retry policies.
- **Sessions have a TTL.** `opts.SessionTimeToLive` controls how long the workflow waits for a new turn before shutting down. Set it to match your expected idle window.

## Getting Started

### Prerequisites

- [.NET 10 SDK](https://dot.net) or later
- A local Temporal server: `temporal server start-dev`
- An OpenAI-compatible API key

### Configure API credentials

```bash
dotnet user-secrets set "OPENAI_API_KEY" "sk-..." --project samples/MEAI/DurableChat
dotnet user-secrets set "OPENAI_API_BASE_URL" "https://api.openai.com/v1" --project samples/MEAI/DurableChat
```

### Run

```bash
dotnet run --project samples/MEAI/DurableChat/DurableChat.csproj
```

### Expected Output

```
Worker started.

════════════════════════════════════════════════════════
 Demo 1: Multi-Turn Conversation
════════════════════════════════════════════════════════
 Conversation ID: multi-turn-<guid>

 User : What is the capital of France?
 Agent: The capital of France is Paris.

 User : What is the population of that city?
 Agent: Paris has a population of approximately 2.1 million ...
════════════════════════════════════════════════════════

════════════════════════════════════════════════════════
 Demo 3: History Query
════════════════════════════════════════════════════════
 Persisted history:
   [User ] Name three planets in our solar system.
   [Agent] Mercury, Venus, and Earth are three planets ...
   ...
 Total messages stored: 4
════════════════════════════════════════════════════════
```
