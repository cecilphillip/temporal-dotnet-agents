using System.Text.Json;
using System.Text.Json.Serialization;
using Temporalio.Extensions.AI;
using Temporalio.Extensions.Agents.Session;

namespace Temporalio.Extensions.Agents.Workflows;

/// <summary>
/// Input for the <see cref="AgentActivities.ExecuteAgentAsync"/> activity.
/// </summary>
internal sealed class ExecuteAgentInput
{
    public ExecuteAgentInput(
        string agentName,
        RunRequest request,
        IReadOnlyList<DurableSessionEntry> conversationHistory,
        JsonElement? serializedStateBag = null,
        TemporalAgentSessionId? sessionId = null)
    {
        AgentName = agentName;
        Request = request;
        ConversationHistory = conversationHistory;
        SerializedStateBag = serializedStateBag;
        SessionId = sessionId;
    }

    /// <summary>Gets the name of the agent to invoke.</summary>
    public string AgentName { get; }

    /// <summary>
    /// Gets the explicit session ID for this agent call. When provided, the activity uses this
    /// instead of parsing the workflow ID from the activity context. This is required when the
    /// activity is dispatched from an orchestrating workflow whose ID does not follow the
    /// <c>ta-{agentName}-{key}</c> convention.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TemporalAgentSessionId? SessionId { get; }

    /// <summary>Gets the run request (contains the new messages + options).</summary>
    public RunRequest Request { get; }

    /// <summary>
    /// Gets the full conversation history at the time of the activity call,
    /// including the new request entry for this turn (an
    /// <see cref="State.AgentSessionRequest"/>).
    /// </summary>
    public IReadOnlyList<DurableSessionEntry> ConversationHistory { get; }

    /// <summary>
    /// Gets the serialized <see cref="AgentSessionStateBag"/> from the previous turn,
    /// used to restore provider state (e.g. Mem0 thread IDs) without re-initializing.
    /// <see langword="null"/> on the first turn of a session.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? SerializedStateBag { get; }
}
