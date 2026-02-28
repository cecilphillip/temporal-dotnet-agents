using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;

namespace Temporalio.Extensions.Agents.State;

internal sealed class TemporalAgentStateUnknownContent : TemporalAgentStateContent
{
    [JsonPropertyName("content")]
    public required JsonElement Content { get; init; }

    public static TemporalAgentStateUnknownContent FromUnknownContent(AIContent content) =>
        new()
        {
            Content = JsonSerializer.SerializeToElement(
                value: content,
                jsonTypeInfo: AIJsonUtilities.DefaultOptions.GetTypeInfo(typeof(AIContent)))
        };

    public override AIContent ToAIContent()
    {
        AIContent? content = this.Content.Deserialize(
            jsonTypeInfo: AIJsonUtilities.DefaultOptions.GetTypeInfo(typeof(AIContent))) as AIContent;

        return content ?? throw new InvalidOperationException($"The content '{this.Content}' is not valid AI content.");
    }
}
