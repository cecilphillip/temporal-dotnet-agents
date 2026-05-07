namespace Temporalio.Extensions.Agents.Workflows;

/// <summary>
/// Result payload returned by the per-tool activity
/// (<c>Temporalio.Extensions.Agents.InvokeAgentTool</c>). Carries the tool's return value
/// alongside the originating <c>CallId</c> so the workflow can pair the result with the
/// matching pending tool call.
/// </summary>
internal sealed class InvokeAgentToolResult
{
    /// <summary>
    /// The value returned by the tool's <c>InvokeAsync</c>. Serialized as <c>object?</c> through the
    /// Temporal data converter; consumers typically project this into a
    /// <see cref="Microsoft.Extensions.AI.FunctionResultContent"/> on the workflow side.
    /// </summary>
    public object? Result { get; init; }

    /// <summary>
    /// Echo of <see cref="InvokeAgentToolInput.CallId"/> so the workflow can correlate parallel
    /// tool dispatches.
    /// </summary>
    public string? CallId { get; init; }
}
