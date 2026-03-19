namespace Temporalio.Extensions.AI;

/// <summary>
/// Serializable output from the durable function invocation activity.
/// </summary>
internal sealed class DurableFunctionOutput
{
    /// <summary>
    /// The result returned by the <see cref="Microsoft.Extensions.AI.AIFunction"/>.
    /// </summary>
    public object? Result { get; init; }
}
