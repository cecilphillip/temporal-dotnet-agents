// DurableEmbeddings sample — demonstrates IEmbeddingGenerator<string, Embedding<float>>
// wrapped with UseDurableExecution() inside a Temporal workflow.
//
// What this shows
// ───────────────
// When GenerateAsync is called inside a Temporal workflow, the DurableEmbeddingGenerator
// middleware detects Workflow.InWorkflow == true and dispatches each call as a separate
// Temporal activity via DurableEmbeddingActivities. This means:
//
//   • Each embedding is independently retried on transient failures (e.g., rate limits,
//     network timeouts) without re-running chunks that already completed.
//
//   • If the worker crashes mid-batch, Temporal replays workflow history on restart —
//     completed embedding activities are replayed from history, not re-sent to the API.
//
//   • Outside a workflow (Workflow.InWorkflow == false), GenerateAsync passes through
//     to the inner IEmbeddingGenerator unchanged — no Temporal overhead.
//
// Scenario: DocumentIndexingWorkflow — a realistic RAG indexing job
// ─────────────────────────────────────────────────────────────────
// The workflow receives a list of text chunks (e.g., paragraphs from a document) and
// generates an embedding for each one as a separate activity. In production you would
// persist the returned vectors to a vector database (Qdrant, pgvector, Azure AI Search).
// Here we print the embedding dimensions and the dot-product similarity between the
// first two chunks to confirm the vectors capture different semantic content.
//
// How Workflow.InWorkflow dispatching works
// ─────────────────────────────────────────
// DocumentIndexingWorkflow creates a DurableEmbeddingGenerator directly in workflow
// code with a NullEmbeddingGenerator as the inner generator. The null inner is never
// called because Workflow.InWorkflow == true routes every GenerateAsync call to
// DurableEmbeddingActivities instead. On the worker side, DurableEmbeddingActivities
// resolves the real IEmbeddingGenerator<string, Embedding<float>> from DI and calls
// the actual OpenAI embeddings endpoint.
//
// Prerequisites
// ─────────────
// • A local Temporal server:  temporal server start-dev
//   (The dev server starts on localhost:7233 with the "default" namespace.)
// • OPENAI_API_KEY: dotnet user-secrets set "OPENAI_API_KEY" "sk-..." --project samples/MEAI/DurableEmbeddings
//
// Run:  dotnet run --project samples/MEAI/DurableEmbeddings/DurableEmbeddings.csproj

using System.ClientModel;
using System.Diagnostics;
using DurableEmbeddings;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenAI;
using Temporalio.Client;
using Temporalio.Extensions.AI;
using Temporalio.Extensions.Hosting;
using Temporalio.Workflows;

// ── Setup: Build the application host ────────────────────────────────────────
var builder = Host.CreateApplicationBuilder(args);
builder.Logging.SetMinimumLevel(LogLevel.Warning);

var apiKey = builder.Configuration.GetValue<string>("OPENAI_API_KEY");
var apiBaseUrl = builder.Configuration.GetValue<string>("OPENAI_API_BASE_URL") ?? "https://api.openai.com/v1";
var embeddingModel = builder.Configuration.GetValue<string>("OPENAI_EMBEDDING_MODEL") ?? "text-embedding-3-small";
var temporalAddress = builder.Configuration.GetValue<string>("TEMPORAL_ADDRESS") ?? "localhost:7233";

if (string.IsNullOrEmpty(apiKey))
    throw new InvalidOperationException("OPENAI_API_KEY is not configured. Set it with: dotnet user-secrets set \"OPENAI_API_KEY\" \"sk-...\" --project samples/MEAI/DurableEmbeddings");

const string taskQueue = "durable-embeddings";

// ── Setup: Connect Temporal client with DurableAIDataConverter ────────────────
// DurableAIDataConverter.Instance wraps Temporal's payload converter with
// AIJsonUtilities.DefaultOptions, which correctly handles MEAI's $type discriminator
// for polymorphic AIContent subclasses. This is required whenever MEAI types
// (ChatMessage, AIContent, etc.) pass through Temporal workflow history.
var temporalClient = await TemporalClient.ConnectAsync(new TemporalClientConnectOptions(temporalAddress)
{
    DataConverter = DurableAIDataConverter.Instance,
    Namespace = "default",
});
builder.Services.AddSingleton<ITemporalClient>(temporalClient);

// ── Setup: Create the OpenAI client ──────────────────────────────────────────
var openAiClient = new OpenAIClient(
    new ApiKeyCredential(apiKey),
    new OpenAIClientOptions { Endpoint = new Uri(apiBaseUrl) });

// ── Setup: Register IEmbeddingGenerator with UseDurableExecution ─────────────
// AddEmbeddingGenerator is the idiomatic MEAI DI pattern — it returns an
// EmbeddingGeneratorBuilder for chaining middleware, then Build() registers the
// final IEmbeddingGenerator<string, Embedding<float>> singleton.
//
// UseDurableExecution() wraps the pipeline with DurableEmbeddingGenerator middleware.
// When GenerateAsync is called inside a workflow, the middleware dispatches to
// DurableEmbeddingActivities instead of calling the inner generator directly.
// When called outside a workflow, it passes through to the inner generator unchanged.
//
// On the worker side, DurableEmbeddingActivities resolves this same
// IEmbeddingGenerator<string, Embedding<float>> from DI and calls GenerateAsync —
// that is what actually reaches the OpenAI API.
builder.Services
    .AddEmbeddingGenerator(
        openAiClient.GetEmbeddingClient(embeddingModel).AsIEmbeddingGenerator())
    .UseDurableExecution(opts =>
    {
        // How long each individual embedding activity may run before Temporal
        // considers it timed out and schedules a retry.
        opts.ActivityTimeout = TimeSpan.FromMinutes(2);
    })
    .Build();

// ── Setup: Register IChatClient ───────────────────────────────────────────────
// AddDurableAI registers DurableChatActivities, which constructor-injects IChatClient.
// Even though this sample is about embeddings and does not use the chat workflow,
// we must provide an IChatClient so the activities can be resolved without error.
IChatClient openAiChatClient = (IChatClient)openAiClient.GetChatClient("gpt-4o-mini");
builder.Services.AddChatClient(openAiChatClient).Build();

// ── Setup: Register worker + durable AI ──────────────────────────────────────
// AddDurableAI registers:
//   • DurableChatWorkflow      — durable chat session workflow (not used in this sample)
//   • DurableChatActivities    — activity wrapping IChatClient.GetResponseAsync
//   • DurableFunctionActivities — activity wrapping durable tool calls
//   • DurableEmbeddingActivities — activity wrapping IEmbeddingGenerator.GenerateAsync
//   • DurableChatSessionClient — external entry point for chat sessions
//
// DurableEmbeddingActivities is already included — no extra registration required.
// AddWorkflow<DocumentIndexingWorkflow>() registers our custom workflow on the worker.
builder.Services
    .AddHostedTemporalWorker(temporalAddress, "default", taskQueue)
    .AddDurableAI(opts =>
    {
        opts.ActivityTimeout = TimeSpan.FromMinutes(2);
        opts.SessionTimeToLive = TimeSpan.FromHours(1);
    })
    .AddWorkflow<DocumentIndexingWorkflow>()
    .AddWorkflow<ParallelDocumentIndexingWorkflow>();

// ── Start ─────────────────────────────────────────────────────────────────────
var host = builder.Build();
await host.StartAsync();

Console.WriteLine("Worker started.\n");

// ── Run demos ─────────────────────────────────────────────────────────────────
await RunDocumentIndexingDemoAsync(temporalClient, taskQueue);
await RunParallelIndexingDemoAsync(temporalClient, taskQueue);

// ── Shutdown ──────────────────────────────────────────────────────────────────
try { await host.StopAsync(); } catch (OperationCanceledException) { }
Console.WriteLine("Done.");

// ═════════════════════════════════════════════════════════════════════════════
// Demo: DocumentIndexingWorkflow — durable embedding generation per chunk
//
// Each text chunk is embedded as a separate Temporal activity. The workflow
// returns the vector dimension and the dot-product similarity between the
// first two chunks, proving they have distinct semantic representations.
// ═════════════════════════════════════════════════════════════════════════════
static async Task RunDocumentIndexingDemoAsync(ITemporalClient client, string taskQueue)
{
    Console.WriteLine("════════════════════════════════════════════════════════");
    Console.WriteLine(" Demo: Durable Document Indexing (RAG embedding pipeline)");
    Console.WriteLine("════════════════════════════════════════════════════════");

    // Sample text chunks representing paragraphs from a document.
    // In a real RAG pipeline these would come from a PDF, web page, or database row.
    var chunks = new[]
    {
        "Temporal is a durable execution platform that automatically retries failed " +
            "activities and replays workflow history on worker restart.",

        "The Eiffel Tower is a wrought-iron lattice tower on the Champ de Mars in Paris, " +
            "France, built between 1887 and 1889 as the centerpiece of the 1889 World's Fair.",

        "Microsoft Extensions AI (MEAI) provides a unified abstraction layer for " +
            "large language models, embedding generators, and AI middleware in .NET.",
    };

    Console.WriteLine($" Chunks to index: {chunks.Length}");
    for (int i = 0; i < chunks.Length; i++)
    {
        Console.WriteLine($"   [{i + 1}] {chunks[i][..Math.Min(70, chunks[i].Length)]}...");
    }
    Console.WriteLine();

    var workflowId = $"doc-index-{Guid.NewGuid():N}";
    Console.WriteLine($" Workflow ID: {workflowId}");
    Console.WriteLine(" Starting DocumentIndexingWorkflow...\n");

    var sw = Stopwatch.StartNew();

    // Execute the workflow. Each chunk becomes one DurableEmbeddingActivities invocation.
    // If this process crashes mid-run, Temporal will replay completed embeddings from
    // history and only re-run the remaining chunks — no wasted API calls.
    var result = await client.ExecuteWorkflowAsync(
        (DocumentIndexingWorkflow wf) => wf.RunAsync(new DocumentIndexingInput
        {
            Chunks = chunks,
            ActivityTimeout = TimeSpan.FromMinutes(2),
        }),
        new WorkflowOptions
        {
            Id = workflowId,
            TaskQueue = taskQueue,
        });

    sw.Stop();

    Console.WriteLine(" Results:");
    Console.WriteLine($"   Elapsed         : {sw.ElapsedMilliseconds} ms (sequential)");
    Console.WriteLine($"   Chunks indexed  : {result.Chunks.Count}");
    Console.WriteLine($"   Vector dimension: {result.Dimensions}");

    if (result.FirstPairSimilarity.HasValue)
    {
        // Dot-product similarity between chunk 1 (Temporal) and chunk 2 (Eiffel Tower).
        // A higher value means more similar semantics. These topics are unrelated, so
        // the similarity should be noticeably lower than comparing two on-topic chunks.
        Console.WriteLine($"   Similarity (chunk 1 vs 2): {result.FirstPairSimilarity.Value:F4}");
        Console.WriteLine("   (dot-product of unit-normalised OpenAI embeddings;");
        Console.WriteLine("    lower value = more distinct semantic content)");
    }

    Console.WriteLine();
    Console.WriteLine(" Each embedding was generated as a separate Temporal activity:");
    Console.WriteLine("   • Independently retried on transient failures (rate limits, timeouts)");
    Console.WriteLine("   • Completed embeddings replay from history on worker restart");
    Console.WriteLine("   • Visible individually in the Temporal UI for progress tracking");
    Console.WriteLine("════════════════════════════════════════════════════════\n");
}

// ═════════════════════════════════════════════════════════════════════════════
// Demo: ParallelDocumentIndexingWorkflow — concurrent fan-out embedding
//
// All embedding activities are scheduled at the same time via Workflow.WhenAllAsync.
// Temporal orchestrates concurrent execution: the worker dispatches every chunk
// in a single scheduling round rather than waiting for each to complete before
// starting the next. Contrast with the sequential demo above.
// ═════════════════════════════════════════════════════════════════════════════
static async Task RunParallelIndexingDemoAsync(ITemporalClient client, string taskQueue)
{
    Console.WriteLine("════════════════════════════════════════════════════════");
    Console.WriteLine(" Demo: Parallel Document Indexing (fan-out embedding)");
    Console.WriteLine("════════════════════════════════════════════════════════");

    // Five thematically connected paragraphs from the same domain — used to
    // show that all five embeddings are generated in a single parallel round,
    // each as its own independently retried Temporal activity.
    var chunks = new[]
    {
        "Temporal is a durable execution platform that automatically retries failed " +
            "activities and replays workflow history on worker restart, providing " +
            "fault-tolerant orchestration without manual retry logic.",

        "A Temporal workflow is written as ordinary async C# code. The SDK serialises " +
            "every completed step into an event history so the workflow can be resumed " +
            "on any worker after a crash without losing progress.",

        "Activities are the units of work in Temporal: they run outside the workflow " +
            "sandbox, can call external APIs and databases, and are individually retried " +
            "according to a configurable retry policy.",

        "The Temporal server persists the complete event history for every workflow " +
            "execution. Workers replay this history to reconstruct in-memory state, " +
            "guaranteeing exactly-once semantics for completed activity results.",

        "Workflow versioning lets you deploy new code alongside in-flight executions. " +
            "The patching API inserts conditional branches so existing histories " +
            "continue down the old path while new executions take the new path.",
    };

    Console.WriteLine($" Chunks to index: {chunks.Length}");
    for (int i = 0; i < chunks.Length; i++)
    {
        Console.WriteLine($"   [{i + 1}] {chunks[i][..Math.Min(70, chunks[i].Length)]}...");
    }
    Console.WriteLine();

    var workflowId = $"doc-index-parallel-{Guid.NewGuid():N}";
    Console.WriteLine($" Workflow ID: {workflowId}");
    Console.WriteLine(" Starting ParallelDocumentIndexingWorkflow...\n");

    var sw = Stopwatch.StartNew();

    // All N embedding activities are dispatched concurrently.
    // Workflow.WhenAllAsync (the workflow-safe replacement for Task.WhenAll)
    // waits for all of them before the workflow returns — preserving correct
    // history replay behaviour on worker restart.
    var result = await client.ExecuteWorkflowAsync(
        (ParallelDocumentIndexingWorkflow wf) => wf.RunAsync(new DocumentIndexingInput
        {
            Chunks = chunks,
            ActivityTimeout = TimeSpan.FromMinutes(2),
        }),
        new WorkflowOptions
        {
            Id = workflowId,
            TaskQueue = taskQueue,
        });

    sw.Stop();

    Console.WriteLine(" Results:");
    Console.WriteLine($"   Elapsed          : {sw.ElapsedMilliseconds} ms (parallel)");
    Console.WriteLine($"   Chunks processed : {result.ChunksProcessed}");
    Console.WriteLine($"   Vector dimension : {result.Dimensions}");
    Console.WriteLine();
    Console.WriteLine(" All embeddings were dispatched concurrently as Temporal activities:");
    Console.WriteLine("   • Workflow.WhenAllAsync scheduled all N activities in one round");
    Console.WriteLine("   • Each activity is still independently retried on failure");
    Console.WriteLine("   • Completed activities replay from history on worker restart");
    Console.WriteLine("   • Compare elapsed time with the sequential demo above —");
    Console.WriteLine("     wall-clock time approaches max(per-activity) rather than sum");
    Console.WriteLine("════════════════════════════════════════════════════════\n");
}
