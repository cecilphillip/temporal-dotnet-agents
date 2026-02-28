namespace Temporalio.Extensions.Agents;

/// <summary>
/// Describes a human-review request raised by an agent tool via
/// <see cref="TemporalAgentContext.RequestApprovalAsync"/>.
/// </summary>
public sealed record ApprovalRequest
{
    /// <summary>Gets the unique identifier for this request (auto-generated).</summary>
    public string RequestId { get; init; } = Guid.NewGuid().ToString("N");

    /// <summary>Gets a short description of the action requiring approval.</summary>
    public string Action { get; init; } = string.Empty;

    /// <summary>Gets optional additional context to show the human reviewer.</summary>
    public string? Details { get; init; }
}
