// ParallelDocumentIndexingWorkflow — fans out all embedding activities concurrently
// using Workflow.WhenAllAsync rather than awaiting each one in sequence.

using Microsoft.Extensions.AI;
using Temporalio.Extensions.AI;
using Temporalio.Workflows;

namespace DurableEmbeddings;

/// <summary>
/// Result of a parallel document-indexing run.
/// </summary>
public sealed class ParallelIndexingResult
{
    /// <summary>Total number of chunks that were indexed.</summary>
    public required int ChunksProcessed { get; init; }

    /// <summary>The number of dimensions in each embedding vector.</summary>
    public required int Dimensions { get; init; }
}

/// <summary>
/// A Temporal workflow that indexes a batch of text chunks by generating all
/// embeddings concurrently — each chunk is dispatched as a separate Temporal
/// activity, and all activities are scheduled in parallel using
/// <see cref="Workflow.WhenAllAsync{TResult}(IEnumerable{Task{TResult}})"/>.
/// </summary>
/// <remarks>
/// <para>
/// Uses the same <see cref="DocumentIndexingInput"/> as the sequential variant
/// so that both workflows can be driven with identical inputs for comparison.
/// </para>
/// <para>
/// The parallel dispatch is achieved by collecting the <see cref="Task{T}"/>
/// objects returned by <see cref="DurableEmbeddingGenerator.GenerateAsync"/> before
/// awaiting them. Inside a workflow, each such Task wraps a
/// <c>Workflow.ExecuteActivityAsync</c> call, so collecting N tasks schedules
/// N activities concurrently. <c>Workflow.WhenAllAsync</c> (not <c>Task.WhenAll</c>)
/// then waits for all to complete in a replay-safe manner.
/// </para>
/// </remarks>
[Workflow]
public sealed class ParallelDocumentIndexingWorkflow
{
    [WorkflowRun]
    public async Task<ParallelIndexingResult> RunAsync(DocumentIndexingInput input)
    {
        var embeddingOptions = new DurableExecutionOptions
        {
            ActivityTimeout = input.ActivityTimeout,
        };

        // The NullEmbeddingGenerator is never invoked: Workflow.InWorkflow == true
        // causes DurableEmbeddingGenerator to dispatch to DurableEmbeddingActivities
        // on every GenerateAsync call.
        var generator = new DurableEmbeddingGenerator(new NullEmbeddingGenerator(), embeddingOptions);

        // ── Fan-out: start all embedding activities at the same time ────────────
        //
        // Each generator.GenerateAsync call immediately schedules a
        // Workflow.ExecuteActivityAsync and returns a running Task.
        // We materialize every Task into a list (.ToList()) before any await,
        // so Temporal sees all N activity commands at once and schedules them
        // in parallel rather than sequentially.
        var tasks = input.Chunks
            .Select(chunk => generator.GenerateAsync([chunk]))
            .ToList();

        // ── Fan-in: wait for all activities using the workflow-safe combinator ──
        //
        // Workflow.WhenAllAsync is the correct replacement for Task.WhenAll inside
        // a [Workflow] class. Task.WhenAll bypasses Temporal's custom TaskScheduler
        // and breaks determinism during history replay; Workflow.WhenAllAsync works
        // correctly with the SDK's replay mechanism.
        //
        // Results are returned in the same order as input.Chunks.
        var generatedEmbeddings = await Workflow.WhenAllAsync(tasks);

        var dimensions = generatedEmbeddings.Length > 0
            ? generatedEmbeddings[0][0].Vector.Length
            : 0;

        return new ParallelIndexingResult
        {
            ChunksProcessed = generatedEmbeddings.Length,
            Dimensions = dimensions,
        };
    }
}
