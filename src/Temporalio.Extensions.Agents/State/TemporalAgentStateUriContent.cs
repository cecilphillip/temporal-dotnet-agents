using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;

namespace Temporalio.Extensions.Agents.State;

internal sealed class TemporalAgentStateUriContent : TemporalAgentStateContent
{
    [JsonPropertyName("uri")]
    public required Uri Uri { get; init; }

    [JsonPropertyName("mediaType")]
    public required string MediaType { get; init; }

    public static TemporalAgentStateUriContent FromUriContent(UriContent uriContent) =>
        new() { MediaType = uriContent.MediaType, Uri = uriContent.Uri };

    public override AIContent ToAIContent() => new UriContent(this.Uri, this.MediaType);
}
