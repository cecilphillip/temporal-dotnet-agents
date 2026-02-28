using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;

namespace Temporalio.Extensions.Agents.State;

internal sealed class TemporalAgentStateHostedFileContent : TemporalAgentStateContent
{
    [JsonPropertyName("fileId")]
    public required string FileId { get; init; }

    public static TemporalAgentStateHostedFileContent FromHostedFileContent(HostedFileContent content) =>
        new() { FileId = content.FileId };

    public override AIContent ToAIContent() => new HostedFileContent(this.FileId);
}
