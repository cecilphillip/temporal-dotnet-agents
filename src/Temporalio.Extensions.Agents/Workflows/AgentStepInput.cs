using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;
using Temporalio.Extensions.Agents.Session;

namespace Temporalio.Extensions.Agents.Workflows;

/// <summary>
/// Input for the <c>RunAgentStepAsync</c> activity used by step-mode workflows
/// (<see cref="TemporalAgentsOptions.EnablePerToolActivities"/> = <see langword="true"/>).
/// </summary>
/// <remarks>
/// <para>
/// In step mode, the workflow drives the tool-dispatch loop. Each call to the step activity
/// produces either a final assistant message or a list of pending <see cref="FunctionCallContent"/>
/// items that the workflow then dispatches as separate <c>InvokeFunctionAsync</c> activities.
/// </para>
/// <para>
/// The step activity bypasses <c>FunctionInvokingChatClient</c> by resolving the
/// <see cref="IChatClient"/> directly from the activity's <see cref="IServiceProvider"/>
/// (registered by the user before <c>AddDurableAI</c>) and calling its
/// <c>GetStreamingResponseAsync</c> directly — the agent instance itself is not invoked. This
/// returns the LLM's tool-call response to the workflow rather than auto-executing tools
/// inside the activity. The agent's <c>Instructions</c> and registration-time
/// <c>ChatOptions</c> (Temperature, ModelId, etc.) are pulled from the registered
/// <c>ChatClientAgent</c> so per-agent configuration is preserved across the bypass.
/// </para>
/// </remarks>
internal sealed class AgentStepInput
{
    /// <summary>Gets the name of the agent registered with <see cref="TemporalAgentsOptions"/>.</summary>
    public required string AgentName { get; init; }

    /// <summary>Gets the originating run request (carries response format, correlation id, etc.).</summary>
    public required RunRequest Request { get; init; }

    /// <summary>
    /// Gets the messages accumulated across the step loop. On the first iteration this contains
    /// the original user request messages; on subsequent iterations it also contains the
    /// assistant tool-call message and the prior <see cref="FunctionResultContent"/> messages.
    /// </summary>
    public required List<ChatMessage> AccumulatedMessages { get; init; }

    /// <summary>
    /// Gets the serialized <see cref="State.AgentSessionStateBag"/> from the previous step
    /// (or turn). Threaded through every iteration of the loop.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? SerializedStateBag { get; init; }

    /// <summary>
    /// Gets the explicit session ID for this agent call. When provided, the activity uses this
    /// instead of parsing the workflow ID from the activity context.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TemporalAgentSessionId? SessionId { get; init; }
}
