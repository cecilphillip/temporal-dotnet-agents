# DurableEmbeddings: Fault-Tolerant RAG Indexing

## Overview

This sample demonstrates `IEmbeddingGenerator` wrapped with `UseDurableExecution()`, where each
`GenerateAsync` call dispatches as a separate Temporal activity. Two workflow variants show
sequential and parallel fan-out strategies for indexing a document corpus. If the worker crashes
mid-batch, completed embeddings replay from workflow history — no API calls are repeated.

- `UseDurableExecution()` on `EmbeddingGeneratorBuilder` — middleware that detects workflow context
- `DocumentIndexingWorkflow` — sequential per-chunk embedding; one activity per chunk
- `ParallelDocumentIndexingWorkflow` — concurrent fan-out via `Workflow.WhenAllAsync`
- Crash recovery: completed activities replay from history; only remaining chunks are re-run
- `DurableEmbeddingActivities` is included in `AddDurableAI()` — no extra registration required

## Architecture

```
Sequential                           Parallel
──────────                           ────────
DocumentIndexingWorkflow             ParallelDocumentIndexingWorkflow
  foreach chunk                        tasks = chunks.Select(GenerateAsync).ToList()
    await generator.GenerateAsync()    await Workflow.WhenAllAsync(tasks)
      └─ DurableEmbeddingActivities      └─ N concurrent DurableEmbeddingActivities
           └─ IEmbeddingGenerator             └─ IEmbeddingGenerator (per activity)
                └─ OpenAI API                      └─ OpenAI API
```

## Highlights

- **One activity per chunk, not one per batch.** This gives independent retry granularity: if chunk 3 fails on a rate-limit error, only chunk 3 is retried. Chunks 1 and 2 are replayed from history — no wasted API calls.
- **`Workflow.WhenAllAsync`, not `Task.WhenAll`.** Inside a `[Workflow]` class, `Task.WhenAll` bypasses Temporal's custom `TaskScheduler` and breaks determinism during history replay. `Workflow.WhenAllAsync` is the correct replacement.
- **`NullEmbeddingGenerator` as a workflow-side stub.** `DurableEmbeddingGenerator` requires an inner generator in its constructor, but `Workflow.InWorkflow == true` prevents it from ever being called. A lightweight `NullEmbeddingGenerator` satisfies the constructor without pulling in API credentials on the workflow thread.
- **Parallel wall-clock time approaches `max(per-activity)` not `sum`.** The parallel demo schedules all N activities in one Temporal scheduling round, so total elapsed time scales with the slowest chunk rather than all chunks combined.

## Getting Started

### Prerequisites

- [.NET 10 SDK](https://dot.net) or later
- A local Temporal server: `temporal server start-dev`
- An OpenAI-compatible API key

### Configure API credentials

```bash
dotnet user-secrets set "OPENAI_API_KEY" "sk-..." --project samples/MEAI/DurableEmbeddings
dotnet user-secrets set "OPENAI_API_BASE_URL" "https://api.openai.com/v1" --project samples/MEAI/DurableEmbeddings
dotnet user-secrets set "OPENAI_EMBEDDING_MODEL" "text-embedding-3-small" --project samples/MEAI/DurableEmbeddings
```

### Run

```bash
dotnet run --project samples/MEAI/DurableEmbeddings/DurableEmbeddings.csproj
```

### Expected Output

```
 Demo: Durable Document Indexing (RAG embedding pipeline)
   Chunks to index: 3
   Elapsed         : 1842 ms (sequential)
   Chunks indexed  : 3
   Vector dimension: 1536
   Similarity (chunk 1 vs 2): 0.3241

 Demo: Parallel Document Indexing (fan-out embedding)
   Chunks to index: 5
   Elapsed          : 743 ms (parallel)
   Chunks processed : 5
   Vector dimension : 1536
```
