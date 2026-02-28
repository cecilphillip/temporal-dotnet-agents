using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;

namespace Temporalio.Extensions.Agents.State;

internal sealed class TemporalAgentStateHostedVectorStoreContent : TemporalAgentStateContent
{
    [JsonPropertyName("vectorStoreId")]
    public required string VectorStoreId { get; init; }

    public static TemporalAgentStateHostedVectorStoreContent FromHostedVectorStoreContent(HostedVectorStoreContent content) =>
        new() { VectorStoreId = content.VectorStoreId };

    public override AIContent ToAIContent() => new HostedVectorStoreContent(this.VectorStoreId);
}
