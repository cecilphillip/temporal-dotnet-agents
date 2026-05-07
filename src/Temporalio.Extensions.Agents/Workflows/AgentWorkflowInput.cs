using System.Text.Json;
using System.Text.Json.Serialization;
using Temporalio.Common;
using Temporalio.Extensions.AI;
using Temporalio.Workflows;

namespace Temporalio.Extensions.Agents.Workflows;

/// <summary>
/// Input passed to <see cref="AgentWorkflow"/> when starting a new run.
/// Inherits shared session-loop fields (<see cref="DurableChatWorkflowInput.MaxEntryCount"/>,
/// <see cref="DurableChatWorkflowInput.HistoryReducer"/>, <see cref="DurableChatWorkflowInput.OriginalCreatedAt"/>,
/// <see cref="DurableChatWorkflowInput.EnableSearchAttributes"/>, <see cref="DurableChatWorkflowInput.CarriedHistory"/>)
/// from <see cref="DurableChatWorkflowInput"/> per Layer 3 Decision #1.
/// MAF-specific fields (<see cref="AgentName"/>, <see cref="TaskQueue"/>,
/// <see cref="CarriedStateBag"/>, etc.) live on this subclass.
/// </summary>
internal sealed class AgentWorkflowInput : DurableChatWorkflowInput
{
    /// <summary>Gets the name of the agent that this workflow manages.</summary>
    public required string AgentName { get; init; }

    /// <summary>Gets the task queue on which <see cref="AgentActivities"/> are registered.</summary>
    public required string TaskQueue { get; init; }

    /// <summary>
    /// Gets the serialized <see cref="AgentSessionStateBag"/> carried forward from a
    /// previous run (for continue-as-new scenarios). Allows AIContextProvider state
    /// (e.g. Mem0 thread IDs) to survive workflow continuation.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? CarriedStateBag { get; init; }

    /// <summary>
    /// Gets the retry policy applied to every agent activity invocation.
    /// When <see langword="null"/>, Temporal SDK defaults apply (unbounded retries).
    /// </summary>
    public RetryPolicy? RetryPolicy { get; init; }

    /// <summary>
    /// When <see langword="true"/>, the workflow does not include the conversation history in
    /// <see cref="ExecuteAgentInput.ConversationHistory"/> (it stays <see langword="null"/>),
    /// and the activity loads/appends history through a registered
    /// <see cref="HistoryStore.IAgentHistoryStore"/>. Propagated from
    /// <see cref="TemporalAgentsOptions.UseExternalHistory"/> when the workflow is started.
    /// </summary>
    /// <remarks>
    /// Migration: this flag travels with the workflow input. Sessions started before the
    /// upgrade carry <c>UseExternalStore = false</c> and continue using in-memory history
    /// until they complete or hit continue-as-new — switching the option after a session
    /// starts does not retroactively migrate that session.
    /// </remarks>
    public bool UseExternalStore { get; init; }

    /// <summary>
    /// When <see langword="true"/>, the workflow runs the per-turn loop in step mode:
    /// each LLM call is a separate <c>RunAgentStepAsync</c> activity, and each tool call
    /// is dispatched as a separate <c>InvokeFunctionAsync</c> activity. Propagated from
    /// <see cref="TemporalAgentsOptions.EnablePerToolActivities"/> when the workflow is started.
    /// </summary>
    /// <remarks>
    /// Migration: this flag travels with the workflow input. Sessions started before the
    /// upgrade carry <c>EnablePerToolActivities = false</c> and continue using the
    /// single-activity <c>ExecuteAgentAsync</c> path until they complete or hit
    /// continue-as-new.
    /// </remarks>
    public bool EnablePerToolActivities { get; init; }

    /// <summary>
    /// Optional per-tool <see cref="ActivityOptions"/> indexed by tool name. When non-null
    /// and the tool name is present, used in place of the workflow's default activity
    /// options for the tool-invocation activity. Travels with the workflow so write-tool
    /// retry constraints (e.g. <c>MaximumAttempts = 1</c>) are honored across
    /// continue-as-new.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, ActivityOptions>? PerToolActivityOptions { get; init; }

    /// <summary>
    /// Maximum number of step-mode iterations per single agent turn. Mirrors
    /// <see cref="TemporalAgentsOptions.MaxToolCallsPerTurn"/> and travels with the workflow
    /// input so the cap is honored across continue-as-new transitions.
    /// </summary>
    public int MaxToolCallsPerTurn { get; init; } = 20;

    /// <summary>
    /// When <see langword="true"/>, the workflow runs the v0.3 durable-agent loop:
    /// each LLM call is a separate <c>Temporalio.Extensions.Agents.RunDurableAgentStep</c> activity,
    /// and each tool call is a separate <c>Temporalio.Extensions.Agents.InvokeAgentTool</c> activity
    /// dispatched by the workflow. Set by <c>DefaultTemporalAgentClient</c> when the agent name
    /// resolves to a <see cref="DurableAgentRegistration"/> in
    /// <see cref="TemporalAgentsOptions.DurableAgentRegistrations"/>.
    /// </summary>
    /// <remarks>
    /// Travels with the workflow input so the dispatch path is preserved across continue-as-new
    /// transitions. Mutually exclusive with <see cref="EnablePerToolActivities"/> in practice —
    /// the durable-agent path supersedes the legacy step-mode path. Phase 5 removes both
    /// <see cref="EnablePerToolActivities"/> and the single-activity <c>ExecuteAgent</c> path.
    /// </remarks>
    public bool IsDurable { get; init; }

    /// <summary>
    /// Pre-computed per-tool <see cref="ActivityOptions"/> indexed by tool name (case-insensitive).
    /// Populated by <c>DefaultTemporalAgentClient</c> from the agent's
    /// <see cref="DurableAgentRegistration.Tools"/> at workflow start. When non-null and the tool
    /// name is present, the workflow uses these options for the per-tool activity dispatch
    /// (<c>Temporalio.Extensions.Agents.InvokeAgentTool</c>); otherwise it falls back to a default
    /// built from <see cref="DurableChatWorkflowInput.ActivityTimeout"/> and <see cref="RetryPolicy"/>.
    /// </summary>
    /// <remarks>
    /// The dictionary is built at workflow start (not at first activity dispatch) so retry
    /// constraints — especially <c>MaximumAttempts = 1</c> on write tools — are pinned at the
    /// time the workflow began running. Continue-as-new carries the same dictionary forward so
    /// retry semantics survive across CAN transitions.
    /// </remarks>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, ActivityOptions>? DurableAgentToolActivityOptions { get; init; }
}
