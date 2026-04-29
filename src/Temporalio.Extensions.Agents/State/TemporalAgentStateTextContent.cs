using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;

namespace Temporalio.Extensions.Agents.State;

public sealed class TemporalAgentStateTextContent : TemporalAgentStateContent
{
    [JsonPropertyName("text")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public required string? Text { get; init; }

    public static TemporalAgentStateTextContent FromTextContent(TextContent content) =>
        new() { Text = content.Text };

    public override AIContent ToAIContent() => new TextContent(this.Text);
}
