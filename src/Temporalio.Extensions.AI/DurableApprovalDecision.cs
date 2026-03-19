namespace Temporalio.Extensions.AI;

/// <summary>
/// A human's decision on a pending tool approval request.
/// </summary>
public sealed class DurableApprovalDecision
{
    /// <summary>
    /// The request ID this decision applies to.
    /// Must match <see cref="DurableApprovalRequest.RequestId"/>.
    /// </summary>
    public required string RequestId { get; init; }

    /// <summary>
    /// Whether the tool call is approved.
    /// </summary>
    public bool Approved { get; init; }

    /// <summary>
    /// Optional reason for approval or rejection.
    /// </summary>
    public string? Reason { get; init; }
}
