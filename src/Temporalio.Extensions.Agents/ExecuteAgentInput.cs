using System.Text.Json;
using System.Text.Json.Serialization;
using Temporalio.Extensions.Agents.State;

namespace Temporalio.Extensions.Agents;

/// <summary>
/// Input for the <see cref="AgentActivities.ExecuteAgentAsync"/> activity.
/// </summary>
internal sealed class ExecuteAgentInput
{
    public ExecuteAgentInput(
        string agentName,
        RunRequest request,
        IReadOnlyList<TemporalAgentStateEntry> conversationHistory,
        JsonElement? serializedStateBag = null)
    {
        AgentName = agentName;
        Request = request;
        ConversationHistory = conversationHistory;
        SerializedStateBag = serializedStateBag;
    }

    /// <summary>Gets the name of the agent to invoke.</summary>
    public string AgentName { get; }

    /// <summary>Gets the run request (contains the new messages + options).</summary>
    public RunRequest Request { get; }

    /// <summary>
    /// Gets the full conversation history at the time of the activity call,
    /// including the new <see cref="TemporalAgentStateRequest"/> entry for this turn.
    /// </summary>
    public IReadOnlyList<TemporalAgentStateEntry> ConversationHistory { get; }

    /// <summary>
    /// Gets the serialized <see cref="AgentSessionStateBag"/> from the previous turn,
    /// used to restore provider state (e.g. Mem0 thread IDs) without re-initializing.
    /// <see langword="null"/> on the first turn of a session.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? SerializedStateBag { get; }
}
