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
public sealed class DurableEmbeddingGenerator : DelegatingEmbeddingGenerator<string, Embedding<float>>
{
    private readonly DurableExecutionOptions _options;

    /// <summary>
    /// Initializes a new instance of the <see cref="DurableEmbeddingGenerator"/> class.
    /// </summary>
    public DurableEmbeddingGenerator(
        IEmbeddingGenerator<string, Embedding<float>> innerGenerator,
        DurableExecutionOptions options)
        : base(innerGenerator)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

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
            StartToCloseTimeout = _options.ActivityTimeout,
            HeartbeatTimeout = _options.HeartbeatTimeout,
            Summary = BuildActivitySummary(options),
        };

        if (_options.RetryPolicy is not null)
        {
            activityOptions.RetryPolicy = _options.RetryPolicy;
        }

        var output = await Workflow.ExecuteActivityAsync(
            (DurableEmbeddingActivities a) => a.GenerateAsync(input),
            activityOptions).ConfigureAwait(false);

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
            return _options;
        }

        return base.GetService(serviceType, serviceKey);
    }
}
