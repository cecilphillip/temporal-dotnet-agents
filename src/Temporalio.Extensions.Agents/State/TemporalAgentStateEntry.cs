using System.Text.Json;
using System.Text.Json.Serialization;

namespace Temporalio.Extensions.Agents.State;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(TemporalAgentStateRequest), "request")]
[JsonDerivedType(typeof(TemporalAgentStateResponse), "response")]
internal abstract class TemporalAgentStateEntry
{
    [JsonPropertyName("correlationId")]
    public required string CorrelationId { get; init; }

    [JsonPropertyName("createdAt")]
    public required DateTimeOffset CreatedAt { get; init; }

    [JsonPropertyName("messages")]
    public IReadOnlyList<TemporalAgentStateMessage> Messages { get; init; } = [];

    [JsonExtensionData]
    public IDictionary<string, JsonElement>? ExtensionData { get; set; }
}
