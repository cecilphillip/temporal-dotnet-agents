using System.Text.Json.Serialization;
using Microsoft.Agents.AI;

namespace Temporalio.Extensions.Agents.State;

internal sealed class TemporalAgentStateResponse : TemporalAgentStateEntry
{
    [JsonPropertyName("usage")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TemporalAgentStateUsage? Usage { get; init; }

    /// <summary>
    /// Creates a state-history entry from an <see cref="AgentResponse"/>.
    /// </summary>
    /// <param name="correlationId">The correlation ID of the originating request.</param>
    /// <param name="response">The agent response.</param>
    /// <param name="timestamp">
    /// Caller-supplied fallback timestamp used when the response and messages have no
    /// <c>CreatedAt</c>. Workflow callers must pass <c>Workflow.UtcNow</c>; activity / external
    /// callers should pass <c>DateTimeOffset.UtcNow</c>.
    /// </param>
    public static TemporalAgentStateResponse FromResponse(string correlationId, AgentResponse response, DateTimeOffset timestamp)
    {
        ArgumentNullException.ThrowIfNull(response);

        return new TemporalAgentStateResponse
        {
            CorrelationId = correlationId,
            CreatedAt = response.CreatedAt
                ?? (response.Messages.Count > 0 ? response.Messages.Max(m => m.CreatedAt) : null)
                ?? timestamp,
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
