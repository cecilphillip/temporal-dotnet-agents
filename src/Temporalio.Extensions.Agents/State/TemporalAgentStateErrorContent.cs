using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;

namespace Temporalio.Extensions.Agents.State;

internal sealed class TemporalAgentStateErrorContent : TemporalAgentStateContent
{
    [JsonPropertyName("message")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Message { get; init; }

    [JsonPropertyName("errorCode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ErrorCode { get; init; }

    [JsonPropertyName("details")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Details { get; init; }

    public static TemporalAgentStateErrorContent FromErrorContent(ErrorContent content) =>
        new() { Details = content.Details, ErrorCode = content.ErrorCode, Message = content.Message };

    public override AIContent ToAIContent() =>
        new ErrorContent(this.Message) { Details = this.Details, ErrorCode = this.ErrorCode };
}
