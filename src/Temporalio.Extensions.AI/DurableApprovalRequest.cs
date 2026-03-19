namespace Temporalio.Extensions.AI;

/// <summary>
/// Serializable approval request stored in workflow state.
/// Represents a pending tool approval that blocks the workflow until a human responds.
/// </summary>
public sealed class DurableApprovalRequest
{
    /// <summary>
    /// The unique identifier correlating this request with its response.
    /// Maps to <c>ToolApprovalRequestContent.RequestId</c>.
    /// </summary>
    public required string RequestId { get; init; }

    /// <summary>
    /// The name of the function that requires approval.
    /// </summary>
    public string? FunctionName { get; init; }

    /// <summary>
    /// The tool call ID from the LLM's function call request.
    /// </summary>
    public string? CallId { get; init; }

    /// <summary>
    /// Human-readable description of what the tool call will do.
    /// </summary>
    public string? Description { get; init; }
}
