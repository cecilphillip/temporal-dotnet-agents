namespace Temporalio.Extensions.AI;

/// <summary>
/// Serializable input for the durable function invocation activity.
/// </summary>
internal sealed class DurableFunctionInput
{
    /// <summary>
    /// The name of the <see cref="Microsoft.Extensions.AI.AIFunction"/> to invoke.
    /// </summary>
    public required string FunctionName { get; init; }

    /// <summary>
    /// The arguments to pass to the function.
    /// </summary>
    public IDictionary<string, object?>? Arguments { get; init; }

    /// <summary>
    /// The tool call ID for correlation with the LLM's function call request.
    /// </summary>
    public string? CallId { get; init; }
}
