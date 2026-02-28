using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;

namespace Temporalio.Extensions.Agents.State;

internal sealed class TemporalAgentStateTextReasoningContent : TemporalAgentStateContent
{
    [JsonPropertyName("text")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Text { get; init; }

    public static TemporalAgentStateTextReasoningContent FromTextReasoningContent(TextReasoningContent content) =>
        new() { Text = content.Text };

    public override AIContent ToAIContent() => new TextReasoningContent(this.Text);
}
