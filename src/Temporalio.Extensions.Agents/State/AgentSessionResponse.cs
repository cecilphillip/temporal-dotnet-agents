using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Temporalio.Extensions.AI;

namespace Temporalio.Extensions.Agents.State;

/// <summary>
/// MAF-specific subclass of <see cref="DurableSessionResponse"/>. Empty today —
/// reserved for future MAF-only response metadata. The base class already carries
/// <see cref="DurableSessionResponse.Usage"/> via <see cref="UsageDetails"/>.
/// </summary>
/// <remarks>
/// <para>
/// Polymorphism wiring lives in <c>TemporalAgentJsonUtilities</c>, which registers
/// this type under the <c>"agent_response"</c> discriminator on
/// <see cref="DurableSessionEntry"/> at runtime via a
/// <see cref="System.Text.Json.Serialization.Metadata.IJsonTypeInfoResolver"/> modifier.
/// </para>
/// </remarks>
public sealed class AgentSessionResponse : DurableSessionResponse
{
    /// <summary>
    /// Creates an <see cref="AgentSessionResponse"/> from an <see cref="AgentResponse"/>.
    /// </summary>
    /// <param name="correlationId">
    /// The correlation ID of the originating request. Must be non-null and non-empty so that
    /// the request and response entries can be paired up during history queries.
    /// </param>
    /// <param name="response">The agent response captured for this turn.</param>
    /// <param name="timestamp">
    /// Caller-supplied fallback timestamp used when neither the response nor any of its
    /// messages carry a <c>CreatedAt</c>. Workflow callers must pass <c>Workflow.UtcNow</c>;
    /// activity / external callers should pass <c>DateTimeOffset.UtcNow</c>.
    /// </param>
    public static AgentSessionResponse FromAgentResponse(
        string correlationId,
        AgentResponse response,
        DateTimeOffset timestamp)
    {
        ArgumentNullException.ThrowIfNull(response);

        if (string.IsNullOrEmpty(correlationId))
        {
            throw new ArgumentException(
                "correlationId is required and must be non-empty.",
                nameof(correlationId));
        }

        DateTimeOffset createdAt = response.CreatedAt
            ?? (response.Messages.Count > 0 ? response.Messages.Max(m => m.CreatedAt) : null)
            ?? timestamp;

        return new AgentSessionResponse
        {
            CorrelationId = correlationId,
            CreatedAt = createdAt,
            Messages = response.Messages.ToList(),
            Usage = response.Usage,
        };
    }

    /// <summary>
    /// Reconstructs an <see cref="AgentResponse"/> from this entry. Useful for callers that
    /// need to feed a queried history entry back into agent-shaped APIs.
    /// </summary>
    public AgentResponse ToResponse() =>
        new()
        {
            CreatedAt = CreatedAt,
            Messages = Messages.ToList(),
            Usage = Usage,
        };
}
