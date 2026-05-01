using Microsoft.Extensions.AI;
using Temporalio.Workflows;

namespace Temporalio.Extensions.AI;

/// <summary>
/// A <see cref="DelegatingEmbeddingGenerator{String, Embedding}"/> middleware that wraps
/// embedding generation as a Temporal activity when running inside a workflow.
/// </summary>
/// <remarks>
/// Context-aware behavior:
/// <list type="bullet">
///   <item>Inside a Temporal workflow → dispatches via <c>Workflow.ExecuteActivityAsync</c></item>
///   <item>Otherwise → passes through to inner generator</item>
/// </list>
/// </remarks>
/// <param name="innerGenerator">The inner embedding generator to delegate to.</param>
/// <param name="durableOptions">Durable execution configuration.</param>
public sealed class DurableEmbeddingGenerator(
    IEmbeddingGenerator<string, Embedding<float>> innerGenerator,
    DurableExecutionOptions durableOptions)
    : DelegatingEmbeddingGenerator<string, Embedding<float>>(innerGenerator)
{
    // Field initializer validates durableOptions at construction time.
    private readonly DurableExecutionOptions _durableOptions =
        durableOptions ?? throw new ArgumentNullException(nameof(durableOptions));

    /// <inheritdoc/>
    public override async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (!Workflow.InWorkflow)
        {
            return await base.GenerateAsync(values, options, cancellationToken)
                .ConfigureAwait(false);
        }

        // Inside a workflow — dispatch as an activity.
        var input = new DurableEmbeddingInput
        {
            Values = values as IList<string> ?? values.ToList(),
            Options = options,
        };

        var activityOptions = new ActivityOptions
        {
            StartToCloseTimeout = _durableOptions.ActivityTimeout,
            HeartbeatTimeout = _durableOptions.HeartbeatTimeout,
            Summary = BuildActivitySummary(options),
        };

        if (_durableOptions.RetryPolicy is not null)
        {
            activityOptions.RetryPolicy = _durableOptions.RetryPolicy;
        }

        // Do NOT use .ConfigureAwait(false) here: this runs inside a Temporal workflow.
        // ConfigureAwait(false) bypasses the Temporal workflow scheduler's SynchronizationContext,
        // causing the continuation to run on the ThreadPool instead of the workflow thread.
        // The workflow would then be unable to register its CompleteWorkflowExecution command,
        // causing it to hang indefinitely at WorkflowTaskCompleted without ever completing.
        var output = await Workflow.ExecuteActivityAsync(
            (DurableEmbeddingActivities a) => a.GenerateAsync(input),
            activityOptions);

        return output.Embeddings;
    }

    /// <summary>
    /// Builds the activity summary value (visible in the Temporal Web UI activity list).
    /// Uses the model id when available; returns null otherwise so the SDK omits the field.
    /// </summary>
    internal static string? BuildActivitySummary(EmbeddingGenerationOptions? options)
    {
        var modelId = options?.ModelId;
        return string.IsNullOrWhiteSpace(modelId) ? null : modelId;
    }

    /// <inheritdoc/>
    public override object? GetService(Type serviceType, object? serviceKey = null)
    {
        if (serviceType == typeof(DurableExecutionOptions) && serviceKey is null)
        {
            return _durableOptions;
        }

        return base.GetService(serviceType, serviceKey);
    }
}
