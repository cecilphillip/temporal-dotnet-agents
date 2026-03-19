using Microsoft.Extensions.AI;

namespace Temporalio.Extensions.AI;

/// <summary>
/// Serializable output from the durable embedding generation activity.
/// </summary>
internal sealed class DurableEmbeddingOutput
{
    /// <summary>
    /// The generated embeddings.
    /// </summary>
    public required GeneratedEmbeddings<Embedding<float>> Embeddings { get; init; }
}
