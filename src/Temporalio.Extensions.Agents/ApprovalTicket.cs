namespace Temporalio.Extensions.Agents;

/// <summary>
/// The resolved outcome of an approval request, returned by
/// <see cref="TemporalAgentContext.RequestApprovalAsync"/> and
/// <see cref="ITemporalAgentClient.SubmitApprovalAsync"/>.
/// </summary>
public sealed record ApprovalTicket
{
    /// <summary>Gets the <see cref="ApprovalRequest.RequestId"/> this ticket resolves.</summary>
    public string RequestId { get; init; } = string.Empty;

    /// <summary>Gets whether the action was approved.</summary>
    public bool Approved { get; init; }

    /// <summary>Gets the optional reviewer comment.</summary>
    public string? Comment { get; init; }
}
