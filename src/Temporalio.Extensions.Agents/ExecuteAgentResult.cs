using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Agents.AI;

namespace Temporalio.Extensions.Agents;

/// <summary>
/// Return value of <see cref="AgentActivities.ExecuteAgentAsync"/>.
/// Wraps <see cref="AgentResponse"/> together with the serialized <see cref="AgentSessionStateBag"/>
/// so the <see cref="AgentWorkflow"/> can persist provider state across turns.
/// </summary>
internal sealed class ExecuteAgentResult
{
    [JsonConstructor]
    public ExecuteAgentResult(AgentResponse response, JsonElement? serializedStateBag = null)
    {
        Response = response;
        SerializedStateBag = serializedStateBag;
    }

    /// <summary>Gets the agent response for this turn.</summary>
    public AgentResponse Response { get; }

    /// <summary>
    /// Gets the serialized <see cref="AgentSessionStateBag"/> extracted from the session
    /// after the turn completed, or <see langword="null"/> if the bag was empty / not set.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? SerializedStateBag { get; }
}
