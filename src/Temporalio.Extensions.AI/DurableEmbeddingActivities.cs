using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Temporalio.Activities;

namespace Temporalio.Extensions.AI;

/// <summary>
/// Temporal activities that perform embedding generation.
/// The <see cref="IEmbeddingGenerator{String, Embedding}"/> is resolved from DI on the worker side.
/// </summary>
internal sealed class DurableEmbeddingActivities(
    IEmbeddingGenerator<string, Embedding<float>> generator,
    ILoggerFactory? loggerFactory = null)
{
    private readonly ILogger _logger = (loggerFactory ?? NullLoggerFactory.Instance)
        .CreateLogger<DurableEmbeddingActivities>();

    /// <summary>
    /// Generates embeddings by calling the inner generator.
    /// </summary>
    [Activity("Temporalio.Extensions.AI.GenerateEmbedding")]
    public async Task<DurableEmbeddingOutput> GenerateAsync(DurableEmbeddingInput input)
    {
        var ctx = ActivityExecutionContext.Current;
        var ct = ctx.CancellationToken;

        _logger.LogDebug("Executing durable embedding activity for {Count} inputs", input.Values.Count);

        ctx.Heartbeat($"embedding-{input.Values.Count}");

        var embeddings = await generator.GenerateAsync(
            input.Values,
            input.Options,
            ct).ConfigureAwait(false);

        _logger.LogDebug("Durable embedding activity completed");

        return new DurableEmbeddingOutput { Embeddings = embeddings };
    }
}
