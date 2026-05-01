using System.Text.Json;
using System.Text.Json.Serialization;
using Temporalio.Common;
using Temporalio.Extensions.AI;

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
}
