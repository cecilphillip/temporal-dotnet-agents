// DocumentIndexingWorkflow — sequential embedding of text chunks via DurableEmbeddingGenerator,
// where each GenerateAsync call becomes an independently retried Temporal activity.

using Microsoft.Extensions.AI;
using Temporalio.Extensions.AI;
using Temporalio.Workflows;

namespace DurableEmbeddings;

/// <summary>
/// Input for <see cref="DocumentIndexingWorkflow"/>.
/// </summary>
public sealed class DocumentIndexingInput
{
    /// <summary>
    /// The text chunks to embed. Each chunk becomes a separate Temporal activity.
    /// </summary>
    public required IReadOnlyList<string> Chunks { get; init; }

    /// <summary>
    /// Activity start-to-close timeout forwarded to DurableEmbeddingGenerator.
    /// </summary>
    public TimeSpan ActivityTimeout { get; init; } = TimeSpan.FromMinutes(2);
}

/// <summary>
/// Result of embedding a batch of text chunks.
/// </summary>
public sealed class DocumentIndexingResult
{
    /// <summary>
    /// The original input chunks, in order.
    /// </summary>
    public required IReadOnlyList<string> Chunks { get; init; }

    /// <summary>
    /// The number of dimensions in each embedding vector.
    /// </summary>
    public required int Dimensions { get; init; }

    /// <summary>
    /// Dot-product similarity between the first two embeddings (if at least two chunks were given).
    /// A higher value means the texts are more semantically similar.
    /// </summary>
    public double? FirstPairSimilarity { get; init; }
}

/// <summary>
/// A Temporal workflow that indexes a batch of text chunks by generating an embedding
/// for each one as a separate, independently retried Temporal activity.
/// </summary>
/// <remarks>
/// <para>
/// This is a realistic RAG (Retrieval Augmented Generation) indexing scenario.
/// In production you would persist the returned vectors to a vector database
/// (e.g. Qdrant, pgvector, Azure AI Search) after the workflow completes.
/// </para>
/// <para>
/// The workflow creates a <see cref="DurableEmbeddingGenerator"/> with
/// <see cref="DurableExecutionOptions"/> derived from the workflow input.
/// The inner generator passed to the constructor is a stub — it is never called
/// because <c>Workflow.InWorkflow == true</c> causes every <c>GenerateAsync</c>
/// call to be dispatched as a Temporal activity instead.
/// </para>
/// </remarks>
[Workflow]
public sealed class DocumentIndexingWorkflow
{
    [WorkflowRun]
    public async Task<DocumentIndexingResult> RunAsync(DocumentIndexingInput input)
    {
        // Build a DurableEmbeddingGenerator configured with the activity timeout
        // from the workflow input.  The stub inner generator is never called inside
        // a workflow — Workflow.InWorkflow == true causes GenerateAsync to dispatch
        // to DurableEmbeddingActivities instead.
        var options = new DurableExecutionOptions
        {
            ActivityTimeout = input.ActivityTimeout,
            // HeartbeatTimeout defaults to 2 minutes — sufficient for embedding calls.
        };

        // NullEmbeddingGenerator is a pass-through stub used only so the
        // DurableEmbeddingGenerator constructor is satisfied. It is never reached
        // during workflow execution.
        var generator = new DurableEmbeddingGenerator(
            new NullEmbeddingGenerator(),
            options);

        // Generate one embedding per chunk as a separate Temporal activity.
        //
        // Why one call per chunk instead of batching them all together?
        //
        //   • Independent retry: if chunk #3 fails, only chunk #3 is retried.
        //     Chunks #1 and #2 replay from workflow history — no extra API calls.
        //
        //   • Deterministic replay: Temporal replays workflow code on worker restart.
        //     Each completed activity result is read from history, so already-embedded
        //     chunks are never sent to the model again.
        //
        //   • Progress visibility: each activity appears individually in the Temporal
        //     UI, making it easy to see how far along a large indexing job is.
        var embeddings = new List<Embedding<float>>(input.Chunks.Count);

        foreach (var chunk in input.Chunks)
        {
            // This call dispatches to DurableEmbeddingActivities.GenerateAsync
            // because Workflow.InWorkflow == true.  On the worker side, the
            // activity resolves IEmbeddingGenerator<string, Embedding<float>>
            // from DI and calls the real OpenAI embeddings endpoint.
            var result = await generator.GenerateAsync([chunk]);
            embeddings.Add(result[0]);
        }

        // Compute a dot-product similarity between the first two embeddings to
        // show they capture different semantic content.
        double? similarity = null;
        if (embeddings.Count >= 2)
        {
            similarity = DotProduct(embeddings[0].Vector.Span, embeddings[1].Vector.Span);
        }

        return new DocumentIndexingResult
        {
            Chunks = input.Chunks,
            Dimensions = embeddings.Count > 0 ? embeddings[0].Vector.Length : 0,
            FirstPairSimilarity = similarity,
        };
    }

    // Dot product of two float vectors — a simple cosine-similarity proxy when
    // both vectors are unit-normalised (which OpenAI embeddings are).
    private static double DotProduct(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        double sum = 0;
        for (int i = 0; i < a.Length; i++)
        {
            sum += a[i] * b[i];
        }
        return sum;
    }
}

/// <summary>
/// A no-op <see cref="IEmbeddingGenerator{String, Embedding}"/> used as the inner
/// generator for <see cref="DurableEmbeddingGenerator"/> inside a Temporal workflow.
/// It is never called: <c>Workflow.InWorkflow == true</c> causes the durable wrapper
/// to dispatch to <c>DurableEmbeddingActivities</c> before reaching this generator.
/// </summary>
internal sealed class NullEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
{
    public EmbeddingGeneratorMetadata Metadata { get; } =
        new EmbeddingGeneratorMetadata("null", null, null, null);

    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
        => throw new InvalidOperationException(
            "NullEmbeddingGenerator should never be called inside a Temporal workflow. " +
            "DurableEmbeddingGenerator dispatches to DurableEmbeddingActivities when " +
            "Workflow.InWorkflow == true.");

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }
}
