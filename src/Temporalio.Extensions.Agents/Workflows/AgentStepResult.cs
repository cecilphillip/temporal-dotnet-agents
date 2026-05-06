using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;

namespace Temporalio.Extensions.Agents.Workflows;

/// <summary>
/// Result of a single <c>RunAgentStepAsync</c> activity execution.
/// </summary>
/// <remarks>
/// <para>
/// When <see cref="IsFinal"/> is <see langword="true"/>, the step produced an assistant message
/// with no tool calls and the workflow loop terminates. When <see langword="false"/>,
/// <see cref="ToolCalls"/> carries the LLM's pending <see cref="FunctionCallContent"/> items,
/// which the workflow dispatches as separate <c>InvokeFunctionAsync</c> activities.
/// </para>
/// </remarks>
internal sealed class AgentStepResult
{
    /// <summary>
    /// <see langword="true"/> when the assistant produced a final answer (no tool calls).
    /// </summary>
    public required bool IsFinal { get; init; }

    /// <summary>
    /// The assistant message produced by this step. In tool-call iterations, this contains
    /// one or more <see cref="FunctionCallContent"/> items; in the final iteration, it
    /// contains the final text answer.
    /// </summary>
    public required ChatMessage AssistantMessage { get; init; }

    /// <summary>
    /// When <see cref="IsFinal"/> is <see langword="false"/>, this contains the
    /// <see cref="FunctionCallContent"/> items the workflow must dispatch as activities.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<FunctionCallContent>? ToolCalls { get; init; }

    /// <summary>
    /// Updated <see cref="State.AgentSessionStateBag"/> serialization from this step.
    /// Threaded back into the next iteration via <see cref="AgentStepInput.SerializedStateBag"/>.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? UpdatedStateBag { get; init; }

    /// <summary>Optional usage metadata for the LLM call performed during this step.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public UsageDetails? Usage { get; init; }
}
