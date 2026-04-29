using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;
using Temporalio.Extensions.Agents.Workflows;

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

    /// <summary>
    /// Creates a state-history entry from a <see cref="RunRequest"/>.
    /// </summary>
    /// <param name="request">The originating run request. Must have a <see cref="RunRequest.CorrelationId"/> set.</param>
    /// <param name="timestamp">
    /// Caller-supplied creation timestamp. Workflow callers must pass <c>Workflow.UtcNow</c>;
    /// activity / external callers should pass <c>DateTimeOffset.UtcNow</c>.
    /// </param>
    public static TemporalAgentStateRequest FromRunRequest(RunRequest request, DateTimeOffset timestamp)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrEmpty(request.CorrelationId))
        {
            throw new InvalidOperationException(
                "RunRequest.CorrelationId is required. Set it explicitly at the construction site: " +
                "use Workflow.NewGuid() in workflow context, Guid.NewGuid() in external context.");
        }

        return new TemporalAgentStateRequest
        {
            CorrelationId = request.CorrelationId,
            OrchestrationId = request.OrchestrationId,
            Messages = request.Messages.Select(TemporalAgentStateMessage.FromChatMessage).ToList(),
            CreatedAt = request.Messages.Count > 0
                ? request.Messages.Min(m => m.CreatedAt) ?? timestamp
                : timestamp,
            ResponseType = request.ResponseFormat is ChatResponseFormatJson ? "json" : "text",
            ResponseSchema = (request.ResponseFormat as ChatResponseFormatJson)?.Schema
        };
    }
}
