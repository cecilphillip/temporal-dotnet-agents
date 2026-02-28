namespace Temporalio.Extensions.Agents;

/// <summary>
/// A human reviewer's decision submitted via
/// <see cref="ITemporalAgentClient.SubmitApprovalAsync"/>.
/// </summary>
public sealed record ApprovalDecision
{
    /// <summary>Gets the <see cref="ApprovalRequest.RequestId"/> this decision targets.</summary>
    public string RequestId { get; init; } = string.Empty;

    /// <summary>Gets whether the action was approved.</summary>
    public bool Approved { get; init; }

    /// <summary>Gets an optional comment from the reviewer.</summary>
    public string? Comment { get; init; }
}
