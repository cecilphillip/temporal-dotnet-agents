using Microsoft.Extensions.AI;

namespace Temporalio.Extensions.AI;

/// <summary>
/// Serializable input for the durable embedding generation activity.
/// </summary>
internal sealed class DurableEmbeddingInput
{
    /// <summary>
    /// The input values to generate embeddings for.
    /// </summary>
    public required IList<string> Values { get; init; }

    /// <summary>
    /// Optional embedding generation options.
    /// </summary>
    public EmbeddingGenerationOptions? Options { get; init; }
}
