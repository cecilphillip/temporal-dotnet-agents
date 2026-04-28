using Microsoft.Extensions.AI;

namespace Temporalio.Extensions.AI;

/// <summary>
/// Extension methods for wrapping <see cref="AIFunction"/> instances with durable execution.
/// </summary>
public static class AIFunctionExtensions
{
    /// <summary>
    /// Wraps an <see cref="AIFunction"/> with Temporal durable execution.
    /// When invoked inside a workflow, the function call is dispatched as a Temporal activity.
    /// </summary>
    /// <remarks>
    /// Outside a workflow, the wrapper passes through to the inner function unchanged.
    /// Inside a workflow, the call is dispatched as a Temporal activity, so the activity worker
    /// handling that task queue must have called <c>AddDurableAI()</c> and <c>AddDurableTools(function)</c> 
    /// If the function is not registered, the activity throws <see cref="InvalidOperationException"/>
    /// </remarks>
    /// <param name="function">The function to wrap.</param>
    /// <param name="options">Optional durable execution configuration.</param>
    /// <returns>A <see cref="DurableAIFunction"/> wrapping the original function.</returns>
    public static DurableAIFunction AsDurable(
        this AIFunction function,
        DurableExecutionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(function);
        return new DurableAIFunction(function, options);
    }
}
