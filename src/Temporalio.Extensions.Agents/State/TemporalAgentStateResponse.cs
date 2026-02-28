using System.Text.Json.Serialization;
using Microsoft.Agents.AI;

namespace Temporalio.Extensions.Agents.State;

internal sealed class TemporalAgentStateResponse : TemporalAgentStateEntry
{
    [JsonPropertyName("usage")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TemporalAgentStateUsage? Usage { get; init; }

    public static TemporalAgentStateResponse FromResponse(string correlationId, AgentResponse response)
    {
        return new TemporalAgentStateResponse
        {
            CorrelationId = correlationId,
            CreatedAt = response.CreatedAt
                ?? (response.Messages.Count > 0 ? response.Messages.Max(m => m.CreatedAt) : null)
                ?? DateTimeOffset.UtcNow,
            Messages = response.Messages.Select(TemporalAgentStateMessage.FromChatMessage).ToList(),
            Usage = TemporalAgentStateUsage.FromUsage(response.Usage)
        };
    }

    public AgentResponse ToResponse()
    {
        return new AgentResponse
        {
            CreatedAt = this.CreatedAt,
            Messages = this.Messages.Select(m => m.ToChatMessage()).ToList(),
            Usage = this.Usage?.ToUsageDetails()
        };
    }
}
