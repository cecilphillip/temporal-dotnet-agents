using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;

namespace Temporalio.Extensions.AI;

/// <summary>
/// Concrete <see cref="DurableSessionEntry"/> representing a request turn — the inbound
/// messages that triggered a model / agent invocation. Library-specific request fields
/// (e.g., MAF's <c>OrchestrationId</c> / <c>ResponseSchema</c>) live on subclasses.
/// </summary>
public class DurableSessionRequest : DurableSessionEntry
{
    /// <summary>
    /// Creates a request entry from a flat list of <see cref="ChatMessage"/>s.
    /// </summary>
    /// <param name="messages">The user-supplied messages for this turn.</param>
    /// <param name="correlationId">
    /// Per-turn correlation ID. Must be non-null and non-empty. Workflow callers should
    /// pass <c>Workflow.NewGuid().ToString("N")</c>; non-workflow callers should pass
    /// <c>Guid.NewGuid().ToString("N")</c>.
    /// </param>
    /// <param name="timestamp">
    /// Fallback creation timestamp used when none of the supplied <paramref name="messages"/>
    /// have a <see cref="ChatMessage.CreatedAt"/>. Workflow callers must pass
    /// <c>Workflow.UtcNow</c>; activity / external callers should pass
    /// <see cref="DateTimeOffset.UtcNow"/>.
    /// </param>
    public static DurableSessionRequest FromMessages(
        IReadOnlyList<ChatMessage> messages,
        string correlationId,
        DateTimeOffset timestamp)
    {
        ArgumentNullException.ThrowIfNull(messages);

        if (string.IsNullOrEmpty(correlationId))
        {
            throw new ArgumentException(
                "correlationId is required and must be non-empty. Use Workflow.NewGuid() in workflow context, Guid.NewGuid() in external context.",
                nameof(correlationId));
        }

        DateTimeOffset createdAt = messages.Count > 0
            ? messages.Min(m => m.CreatedAt) ?? timestamp
            : timestamp;

        return new DurableSessionRequest
        {
            CorrelationId = correlationId,
            CreatedAt = createdAt,
            Messages = messages.ToList(),
        };
    }
}
