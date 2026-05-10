using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;
using Temporalio.Extensions.Agents.Session;

namespace Temporalio.Extensions.Agents.Workflows;

/// <summary>
/// Input for the <c>RunDurableAgentStepAsync</c> activity used by durable-agent workflows.
/// Each call produces either a final assistant message or a list of pending
/// <see cref="FunctionCallContent"/> items that the workflow then dispatches as separate
/// <c>InvokeAgentToolAsync</c> activities (one per tool call, fanned out via
/// <see cref="Temporalio.Workflows.Workflow.WhenAllAsync{TResult}(IEnumerable{Task{TResult}})"/>).
/// </summary>
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

    /// <summary>
    /// When <see langword="true"/>, this is the first step of a new turn. Durable agents with a
    /// configured <see cref="HistoryStore.IAgentHistoryStore"/> use this to load prior history
    /// from the store at the start of the turn (subsequent steps within the same turn already
    /// see the loaded history through <see cref="AccumulatedMessages"/>).
    /// </summary>
    public bool IsFirstStep { get; init; }

    /// <summary>
    /// When <see langword="true"/>, the activity resolves and returns worker-side durable-agent
    /// settings (<c>UseExternalStoreMode</c>, per-tool <see cref="ActivityOptions"/> dictionary,
    /// <see cref="AgentStepResult.ResolvedMaxToolCallsPerTurn"/>) so the workflow can patch its
    /// input on the first turn of a proxy-started session or sub-agent orchestration.
    /// Only set on the first step of the first turn when <see cref="AgentWorkflowInput.WorkerSettingsResolved"/>
    /// is <see langword="false"/>, or by <see cref="TemporalAIAgent"/> on every first iteration.
    /// Defaults to <see langword="false"/> for all existing callers.
    /// </summary>
    public bool NeedsWorkerSettingsResolution { get; init; }
}
