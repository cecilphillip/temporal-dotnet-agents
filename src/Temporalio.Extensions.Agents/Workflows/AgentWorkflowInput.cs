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
    /// Gets the retry policy applied to the agent's <c>RunAgentStep</c> activity (the LLM call).
    /// When <see langword="null"/>, Temporal SDK defaults apply (unbounded retries). Per-tool
    /// retry policies are configured separately via <see cref="DurableAgentToolActivityOptions"/>.
    /// </summary>
    public RetryPolicy? RetryPolicy { get; init; }

    /// <summary>
    /// Maximum number of LLM-step iterations within a single agent turn. Each iteration may
    /// dispatch a parallel batch of tool activities. When the cap is exceeded the workflow
    /// returns a structured error response. Resolved per-agent at workflow start.
    /// </summary>
    public int MaxToolCallsPerTurn { get; init; } = 20;

    /// <summary>
    /// When <see langword="true"/>, the agent has an <see cref="HistoryStore.IAgentHistoryStore"/>
    /// configured (per-agent or worker-level). The workflow strips message payloads from history
    /// entries (PII / O(n²) protection) and the activity loads/appends conversation history via
    /// the store. Resolved at workflow start by <c>DefaultTemporalAgentClient</c>.
    /// </summary>
    public bool UseExternalStoreMode { get; init; }

    /// <summary>
    /// Pre-computed per-tool <see cref="ActivityOptions"/> indexed by tool name (case-insensitive).
    /// Populated by <c>DefaultTemporalAgentClient</c> from the agent's
    /// <see cref="DurableAgentRegistration.Tools"/> at workflow start. When a tool name is
    /// present, the workflow uses these options for the per-tool activity dispatch
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
