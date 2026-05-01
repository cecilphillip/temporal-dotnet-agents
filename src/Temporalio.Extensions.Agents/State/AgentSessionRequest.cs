using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;
using Temporalio.Extensions.AI;
using Temporalio.Extensions.Agents.Workflows;

namespace Temporalio.Extensions.Agents.State;

/// <summary>
/// MAF-specific subclass of <see cref="DurableSessionRequest"/> that adds
/// agent-orchestration fields (<see cref="OrchestrationId"/>, <see cref="ResponseType"/>,
/// <see cref="ResponseSchema"/>) to the shared session-history wire shape.
/// </summary>
/// <remarks>
/// <para>
/// Polymorphism wiring lives in <c>TemporalAgentJsonUtilities</c>, which registers
/// this type under the <c>"agent_request"</c> discriminator on
/// <see cref="DurableSessionEntry"/> at runtime via a
/// <see cref="System.Text.Json.Serialization.Metadata.IJsonTypeInfoResolver"/> modifier.
/// </para>
/// </remarks>
public sealed class AgentSessionRequest : DurableSessionRequest
{
    /// <summary>
    /// Gets the ID of the orchestration or workflow that initiated this request, when applicable.
    /// </summary>
    public string? OrchestrationId { get; init; }

    /// <summary>
    /// Gets the response-format kind requested for this turn — either <c>"text"</c> or <c>"json"</c>.
    /// </summary>
    public string? ResponseType { get; init; }

    /// <summary>
    /// Gets the JSON schema for the structured response, when <see cref="ResponseType"/> is
    /// <c>"json"</c> and the caller supplied a <see cref="ChatResponseFormatJson"/>.
    /// </summary>
    public JsonElement? ResponseSchema { get; init; }

    /// <summary>
    /// Creates an <see cref="AgentSessionRequest"/> from a <see cref="RunRequest"/>.
    /// </summary>
    /// <param name="request">
    /// The originating run request. Must have a non-empty <see cref="RunRequest.CorrelationId"/>.
    /// </param>
    /// <param name="timestamp">
    /// Caller-supplied creation timestamp. Workflow callers must pass <c>Workflow.UtcNow</c>;
    /// activity / external callers should pass <c>DateTimeOffset.UtcNow</c>. Used as the
    /// fallback when none of the messages on the request carry a <c>CreatedAt</c>.
    /// </param>
    public static AgentSessionRequest FromRunRequest(RunRequest request, DateTimeOffset timestamp)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrEmpty(request.CorrelationId))
        {
            throw new InvalidOperationException(
                "RunRequest.CorrelationId is required. Set it explicitly at the construction site: " +
                "use Workflow.NewGuid() in workflow context, Guid.NewGuid() in external context.");
        }

        DateTimeOffset createdAt = request.Messages.Count > 0
            ? request.Messages.Min(m => m.CreatedAt) ?? timestamp
            : timestamp;

        return new AgentSessionRequest
        {
            CorrelationId = request.CorrelationId,
            CreatedAt = createdAt,
            Messages = request.Messages.ToList(),
            OrchestrationId = request.OrchestrationId,
            ResponseType = request.ResponseFormat is ChatResponseFormatJson ? "json" : "text",
            ResponseSchema = (request.ResponseFormat as ChatResponseFormatJson)?.Schema,
        };
    }
}
