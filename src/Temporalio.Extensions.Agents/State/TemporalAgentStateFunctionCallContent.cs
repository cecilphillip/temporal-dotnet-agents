using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;

namespace Temporalio.Extensions.Agents.State;

internal sealed class TemporalAgentStateFunctionCallContent : TemporalAgentStateContent
{
    [JsonPropertyName("arguments")]
    public required IReadOnlyDictionary<string, object?> Arguments { get; init; } =
        ImmutableDictionary<string, object?>.Empty;

    [JsonPropertyName("callId")]
    public required string CallId { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    public static TemporalAgentStateFunctionCallContent FromFunctionCallContent(FunctionCallContent content) =>
        new()
        {
            Arguments = content.Arguments?.ToDictionary() ?? [],
            CallId = content.CallId,
            Name = content.Name
        };

    public override AIContent ToAIContent() =>
        new FunctionCallContent(this.CallId, this.Name, new Dictionary<string, object?>(this.Arguments));
}
