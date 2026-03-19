using System.Diagnostics;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Temporalio.Activities;

namespace Temporalio.Extensions.AI;

/// <summary>
/// Temporal activities that execute <see cref="AIFunction"/> invocations durably.
/// Functions are resolved from a DI-registered registry by name.
/// </summary>
internal sealed class DurableFunctionActivities(
    IReadOnlyDictionary<string, AIFunction> functionRegistry,
    ILoggerFactory? loggerFactory = null)
{
    private readonly ILogger _logger = (loggerFactory ?? NullLoggerFactory.Instance)
        .CreateLogger<DurableFunctionActivities>();

    /// <summary>
    /// Invokes a named <see cref="AIFunction"/> with the given arguments.
    /// </summary>
    [Activity("Temporalio.Extensions.AI.InvokeFunction")]
    public async Task<DurableFunctionOutput> InvokeFunctionAsync(DurableFunctionInput input)
    {
        var ctx = ActivityExecutionContext.Current;
        var ct = ctx.CancellationToken;

        if (!functionRegistry.TryGetValue(input.FunctionName, out var function))
        {
            throw new InvalidOperationException(
                $"Function '{input.FunctionName}' is not registered in the durable function registry.");
        }

        using var span = DurableChatTelemetry.ActivitySource.StartActivity(
            DurableChatTelemetry.FunctionInvokeSpanName,
            ActivityKind.Internal);

        span?.SetTag(DurableChatTelemetry.ToolNameAttribute, input.FunctionName);
        span?.SetTag(DurableChatTelemetry.ToolCallIdAttribute, input.CallId);

        _logger.LogDebug("Invoking durable function {FunctionName}, callId={CallId}",
            input.FunctionName, input.CallId);

        try
        {
            // Build AIFunctionArguments from the deserialized dictionary.
            var arguments = input.Arguments is not null
                ? new AIFunctionArguments(input.Arguments)
                : new AIFunctionArguments();

            var result = await function.InvokeAsync(arguments, ct).ConfigureAwait(false);

            _logger.LogDebug("Durable function {FunctionName} completed", input.FunctionName);
            return new DurableFunctionOutput { Result = result };
        }
        catch (Exception ex)
        {
            span?.SetStatus(ActivityStatusCode.Error, ex.Message);
            _logger.LogError(ex, "Durable function {FunctionName} failed", input.FunctionName);
            throw;
        }
    }
}
