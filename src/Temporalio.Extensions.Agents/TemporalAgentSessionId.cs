using System.Text.Json;
using System.Text.Json.Serialization;

namespace Temporalio.Extensions.Agents;

/// <summary>
/// Identifies a long-running Temporal agent session. The session corresponds to a Temporal workflow
/// whose ID is <c>ta-{agentName}-{key}</c>.
/// </summary>
[JsonConverter(typeof(TemporalAgentSessionIdJsonConverter))]
public readonly struct TemporalAgentSessionId : IEquatable<TemporalAgentSessionId>
{
    private const string WorkflowIdPrefix = "ta-";

    /// <summary>
    /// Initializes a new instance of the <see cref="TemporalAgentSessionId"/> struct.
    /// </summary>
    public TemporalAgentSessionId(string agentName, string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentName);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        this.AgentName = agentName;
        this.Key = key;
    }

    /// <summary>Gets the agent name (case-preserved; stored lowercase in <see cref="WorkflowId"/>).</summary>
    public string AgentName { get; }

    /// <summary>Gets the unique session key.</summary>
    public string Key { get; }

    /// <summary>Gets the Temporal workflow ID: <c>ta-{agentName.ToLowerInvariant()}-{key}</c>.</summary>
    public string WorkflowId => $"{WorkflowIdPrefix}{AgentName.ToLowerInvariant()}-{Key}";

    /// <summary>Creates a session ID with a random GUID key.</summary>
    public static TemporalAgentSessionId WithRandomKey(string agentName) =>
        new(agentName, Guid.NewGuid().ToString("N"));

    /// <summary>Creates a session ID with a deterministic key derived from a <see cref="Guid"/>.</summary>
    public static TemporalAgentSessionId WithDeterministicKey(string agentName, Guid key) =>
        new(agentName, key.ToString("N"));

    /// <summary>
    /// Parses a workflow ID of the form <c>ta-{agentName}-{key}</c> into a <see cref="TemporalAgentSessionId"/>.
    /// </summary>
    public static TemporalAgentSessionId Parse(string workflowId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workflowId);

        if (!workflowId.StartsWith(WorkflowIdPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new FormatException($"'{workflowId}' is not a valid agent session workflow ID. Expected prefix '{WorkflowIdPrefix}'.");
        }

        // Format: ta-{agentName}-{key}
        // The key is the last segment; agent name is everything between prefix and last '-'
        ReadOnlySpan<char> rest = workflowId.AsSpan(WorkflowIdPrefix.Length);
        int lastDash = rest.LastIndexOf('-');
        if (lastDash <= 0)
        {
            throw new FormatException($"'{workflowId}' is not a valid agent session workflow ID. Expected format 'ta-{{agentName}}-{{key}}'.");
        }

        string agentName = rest[..lastDash].ToString();
        string key = rest[(lastDash + 1)..].ToString();

        return new TemporalAgentSessionId(agentName, key);
    }

    /// <summary>Implicit conversion to workflow ID string.</summary>
    public static implicit operator string(TemporalAgentSessionId sessionId) => sessionId.WorkflowId;

    /// <summary>Implicit conversion from workflow ID string (via <see cref="Parse"/>).</summary>
    public static implicit operator TemporalAgentSessionId(string workflowId) => Parse(workflowId);

    public static bool operator ==(TemporalAgentSessionId left, TemporalAgentSessionId right) =>
        left.AgentName.Equals(right.AgentName, StringComparison.OrdinalIgnoreCase) && left.Key == right.Key;

    public static bool operator !=(TemporalAgentSessionId left, TemporalAgentSessionId right) => !(left == right);

    public bool Equals(TemporalAgentSessionId other) => this == other;

    public override bool Equals(object? obj) => obj is TemporalAgentSessionId other && this == other;

    public override int GetHashCode() => HashCode.Combine(AgentName.ToLowerInvariant(), Key);

    public override string ToString() => WorkflowId;

    /// <summary>Custom JSON converter that serializes/deserializes as the workflow ID string.</summary>
    public sealed class TemporalAgentSessionIdJsonConverter : JsonConverter<TemporalAgentSessionId>
    {
        public override TemporalAgentSessionId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.String)
            {
                throw new JsonException("Expected string value for TemporalAgentSessionId.");
            }

            string value = reader.GetString() ?? string.Empty;
            return Parse(value);
        }

        public override void Write(Utf8JsonWriter writer, TemporalAgentSessionId value, JsonSerializerOptions options)
        {
            writer.WriteStringValue(value.WorkflowId);
        }
    }
}
