using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;

namespace Temporalio.Extensions.Agents.State;

internal sealed class TemporalAgentStateFunctionResultContent : TemporalAgentStateContent
{
    [JsonPropertyName("callId")]
    public required string CallId { get; init; }

    [JsonPropertyName("result")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? Result { get; init; }

    public static TemporalAgentStateFunctionResultContent FromFunctionResultContent(FunctionResultContent content) =>
        new() { CallId = content.CallId, Result = content.Result };

    public override AIContent ToAIContent() => new FunctionResultContent(this.CallId, this.Result);
}
