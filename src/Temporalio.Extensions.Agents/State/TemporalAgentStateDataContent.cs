using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;

namespace Temporalio.Extensions.Agents.State;

internal sealed class TemporalAgentStateDataContent : TemporalAgentStateContent
{
    [JsonPropertyName("uri")]
    public required string Uri { get; init; }

    [JsonPropertyName("mediaType")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MediaType { get; init; }

    public static TemporalAgentStateDataContent FromDataContent(DataContent content) =>
        new() { MediaType = content.MediaType, Uri = content.Uri };

    public override AIContent ToAIContent() => new DataContent(this.Uri, this.MediaType);
}
