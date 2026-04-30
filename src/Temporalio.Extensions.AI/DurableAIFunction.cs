using Microsoft.Extensions.AI;
using Temporalio.Workflows;

namespace Temporalio.Extensions.AI;

/// <summary>
/// A <see cref="DelegatingAIFunction"/> that wraps tool calls as Temporal activities
/// when running inside a workflow, providing per-tool durability, retry, and timeout.
/// </summary>
public sealed class DurableAIFunction : DelegatingAIFunction
{
    private readonly DurableExecutionOptions _options;

    /// <summary>
    /// Initializes a new instance of the <see cref="DurableAIFunction"/> class.
    /// </summary>
    /// <param name="innerFunction">The inner function to wrap.</param>
    /// <param name="options">Durable execution configuration.</param>
    public DurableAIFunction(AIFunction innerFunction, DurableExecutionOptions? options = null)
        : base(innerFunction)
    {
        _options = options ?? new DurableExecutionOptions();
    }

    /// <inheritdoc/>
    protected override async ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments,
        CancellationToken cancellationToken)
    {
        if (!Workflow.InWorkflow)
        {
            // Outside a workflow — pass through to inner function.
            return await base.InvokeCoreAsync(arguments, cancellationToken)
                .ConfigureAwait(false);
        }

        // Inside a workflow — dispatch as a Temporal activity.
        var input = new DurableFunctionInput
        {
            FunctionName = Name,
            Arguments = ConvertArguments(arguments),
        };

        var activityOptions = new ActivityOptions
        {
            StartToCloseTimeout = _options.ActivityTimeout,
            Summary = BuildActivitySummary(Name),
        };

        if (_options.RetryPolicy is not null)
        {
            activityOptions.RetryPolicy = _options.RetryPolicy;
        }

        // Do NOT use .ConfigureAwait(false) here: this runs inside a Temporal workflow.
        // ConfigureAwait(false) bypasses the Temporal workflow scheduler's SynchronizationContext,
        // so the continuation would run on the ThreadPool instead of the workflow thread.
        // The workflow would then be unable to register its CompleteWorkflowExecution command,
        // causing it to hang indefinitely at WorkflowTaskCompleted without ever completing.
        var output = await Workflow.ExecuteActivityAsync(
            (DurableFunctionActivities a) => a.InvokeFunctionAsync(input),
            activityOptions);

        return output.Result;
    }

    /// <summary>
    /// Converts <see cref="AIFunctionArguments"/> to a serializable dictionary.
    /// </summary>
    private static Dictionary<string, object?>? ConvertArguments(AIFunctionArguments arguments)
    {
        return arguments.Count == 0 ? null : new Dictionary<string, object?>(arguments);
    }

    /// <summary>
    /// Builds the activity summary value (visible in the Temporal Web UI activity list).
    /// Uses the function name; returns null when the name is missing so the SDK omits the field.
    /// </summary>
    internal static string? BuildActivitySummary(string? functionName) =>
        string.IsNullOrWhiteSpace(functionName) ? null : functionName;
}
