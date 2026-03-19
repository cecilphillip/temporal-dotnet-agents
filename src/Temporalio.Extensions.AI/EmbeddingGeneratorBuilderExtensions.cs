using Microsoft.Extensions.AI;

namespace Temporalio.Extensions.AI;

/// <summary>
/// Extension methods for <see cref="EmbeddingGeneratorBuilder{String, Embedding}"/>
/// to add durable execution middleware.
/// </summary>
public static class EmbeddingGeneratorBuilderExtensions
{
    /// <summary>
    /// Adds durable execution middleware to the embedding generator pipeline.
    /// When the pipeline is used inside a Temporal workflow, embedding calls are automatically
    /// dispatched as Temporal activities with retry, timeout, and crash recovery.
    /// </summary>
    public static EmbeddingGeneratorBuilder<string, Embedding<float>> UseDurableExecution(
        this EmbeddingGeneratorBuilder<string, Embedding<float>> builder,
        Action<DurableExecutionOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var options = new DurableExecutionOptions();
        configure?.Invoke(options);

        return builder.Use(innerGenerator => new DurableEmbeddingGenerator(innerGenerator, options));
    }
}
