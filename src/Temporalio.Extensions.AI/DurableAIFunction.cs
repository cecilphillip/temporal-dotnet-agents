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
        };

        if (_options.RetryPolicy is not null)
        {
            activityOptions.RetryPolicy = _options.RetryPolicy;
        }

        var output = await Workflow.ExecuteActivityAsync(
            (DurableFunctionActivities a) => a.InvokeFunctionAsync(input),
            activityOptions).ConfigureAwait(false);

        return output.Result;
    }

    /// <summary>
    /// Converts <see cref="AIFunctionArguments"/> to a serializable dictionary.
    /// </summary>
    private static Dictionary<string, object?>? ConvertArguments(AIFunctionArguments arguments)
    {
        if (arguments.Count == 0)
        {
            return null;
        }

        var dict = new Dictionary<string, object?>(arguments.Count);
        foreach (var kvp in arguments)
        {
            dict[kvp.Key] = kvp.Value;
        }

        return dict;
    }
}
