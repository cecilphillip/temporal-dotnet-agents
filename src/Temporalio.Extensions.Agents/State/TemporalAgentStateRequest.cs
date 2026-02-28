using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;

namespace Temporalio.Extensions.Agents.State;

internal sealed class TemporalAgentStateRequest : TemporalAgentStateEntry
{
    [JsonPropertyName("orchestrationId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? OrchestrationId { get; init; }

    [JsonPropertyName("responseType")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ResponseType { get; init; }

    [JsonPropertyName("responseSchema")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? ResponseSchema { get; init; }

    public static TemporalAgentStateRequest FromRunRequest(RunRequest request)
    {
        return new TemporalAgentStateRequest
        {
            CorrelationId = request.CorrelationId,
            OrchestrationId = request.OrchestrationId,
            Messages = request.Messages.Select(TemporalAgentStateMessage.FromChatMessage).ToList(),
            CreatedAt = request.Messages.Count > 0
                ? request.Messages.Min(m => m.CreatedAt) ?? DateTimeOffset.UtcNow
                : DateTimeOffset.UtcNow,
            ResponseType = request.ResponseFormat is ChatResponseFormatJson ? "json" : "text",
            ResponseSchema = (request.ResponseFormat as ChatResponseFormatJson)?.Schema
        };
    }
}
