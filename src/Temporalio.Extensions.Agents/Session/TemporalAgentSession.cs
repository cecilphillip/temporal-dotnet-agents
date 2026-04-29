using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Agents.AI;

namespace Temporalio.Extensions.Agents.Session;

/// <summary>
/// An <see cref="AgentSession"/> implementation for Temporal agents.
/// </summary>
public sealed class TemporalAgentSession : AgentSession
{
    /// <summary>
    /// Initializes a new <see cref="TemporalAgentSession"/> with the given session ID.
    /// Use this to reconnect to an existing agent session by its known workflow ID.
    /// </summary>
    public TemporalAgentSession(TemporalAgentSessionId sessionId)
    {
        this.SessionId = sessionId;
    }

    [JsonConstructor]
    internal TemporalAgentSession(TemporalAgentSessionId sessionId, AgentSessionStateBag stateBag) : base(stateBag)
    {
        this.SessionId = sessionId;
    }

    /// <summary>Gets the Temporal agent session ID.</summary>
    [JsonInclude]
    [JsonPropertyName("sessionId")]
    public TemporalAgentSessionId SessionId { get; }

    internal JsonElement Serialize(JsonSerializerOptions? jsonSerializerOptions = null)
    {
        var opts = jsonSerializerOptions ?? TemporalAgentJsonUtilities.DefaultOptions;
        return JsonSerializer.SerializeToElement(this, opts.GetTypeInfo(typeof(TemporalAgentSession)));
    }

    internal static TemporalAgentSession Deserialize(JsonElement serializedSession, JsonSerializerOptions? jsonSerializerOptions = null)
    {
        if (!serializedSession.TryGetProperty("sessionId", out JsonElement sessionIdElement) ||
            sessionIdElement.ValueKind != JsonValueKind.String)
        {
            throw new JsonException("Invalid or missing sessionId property.");
        }

        string sessionIdString = sessionIdElement.GetString() ?? throw new JsonException("sessionId property is null.");
        TemporalAgentSessionId sessionId = TemporalAgentSessionId.Parse(sessionIdString);
        AgentSessionStateBag stateBag = serializedSession.TryGetProperty("stateBag", out JsonElement stateBagElement)
            ? AgentSessionStateBag.Deserialize(stateBagElement)
            : new AgentSessionStateBag();

        return new TemporalAgentSession(sessionId, stateBag);
    }

    /// <summary>
    /// Creates a <see cref="TemporalAgentSession"/> with the given <paramref name="sessionId"/>
    /// and optionally restores a <see cref="AgentSessionStateBag"/> from a previously
    /// serialized value (see <see cref="SerializeStateBag"/>).
    /// </summary>
    internal static TemporalAgentSession FromStateBag(
        TemporalAgentSessionId sessionId,
        JsonElement? serializedStateBag)
    {
        // Note: a JsonElement default-initialized in C# has ValueKind == Undefined,
        // whereas an explicit JSON null has ValueKind == Null. After round-tripping
        // through Temporal's payload converter, an absent bag may surface as either
        // a C# null on the JsonElement? wrapper or as a JsonElement whose ValueKind
        // is Undefined or Null. The pattern below covers all cases by treating
        // anything other than a real value as "no bag" and returning a fresh session.
        if (serializedStateBag is { ValueKind: not JsonValueKind.Undefined and not JsonValueKind.Null } bagEl)
        {
            return new TemporalAgentSession(sessionId, AgentSessionStateBag.Deserialize(bagEl));
        }

        return new TemporalAgentSession(sessionId);
    }

    /// <summary>
    /// Serializes the <see cref="AgentSessionStateBag"/> portion of this session,
    /// returning <see langword="null"/> when the bag is empty.
    /// </summary>
    internal JsonElement? SerializeStateBag()
    {
        if (this.StateBag.Count == 0)
        {
            return null;
        }

        return this.StateBag.Serialize();
    }

    /// <inheritdoc/>
    public override object? GetService(Type serviceType, object? serviceKey = null)
    {
        if (serviceType == typeof(TemporalAgentSessionId))
        {
            return this.SessionId;
        }

        return base.GetService(serviceType, serviceKey);
    }

    /// <inheritdoc/>
    public override string ToString() => this.SessionId.WorkflowId;
}
