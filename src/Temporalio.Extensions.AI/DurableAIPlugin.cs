using System.Diagnostics.CodeAnalysis;
using Temporalio.Worker;
using Temporalio.Workflows;

namespace Temporalio.Extensions.AI;

/// <summary>
/// An <see cref="ITemporalWorkerPlugin"/> that registers the durable AI default
/// workflow on the worker and ensures <see cref="DurableAIDataConverter"/> is
/// applied to the underlying client via <see cref="DurableAIDataConverterPlugin"/>.
/// </summary>
/// <remarks>
/// <para>
/// This is a parallel registration path to
/// <see cref="DurableAIServiceCollectionExtensions.AddDurableAI"/>. The two paths
/// are intended to converge on byte-equivalent DI state — neither replaces the
/// other.
/// </para>
/// <para>
/// Activities cannot be registered from <see cref="ConfigureWorker"/> directly
/// (they need DI and there is no <see cref="IServiceProvider"/> available at
/// this hook). Use <see cref="TemporalPluginBuilderExtensions.AddWorkerPlugin(Hosting.ITemporalWorkerServiceOptionsBuilder, DurableAIPlugin)"/>
/// to register both the plugin and the DI side in one call.
/// </para>
/// <para>
/// WARNING: This API is experimental and may change in future releases.
/// Suppress diagnostic <c>TAI001</c> to opt in.
/// </para>
/// </remarks>
[Experimental("TAI001")]
public sealed class DurableAIPlugin : ITemporalWorkerPlugin
{
    /// <summary>
    /// The canonical plugin name used to identify this plugin in worker plugin lists.
    /// </summary>
    public const string PluginName = "Temporalio.Extensions.AI.DurableAIPlugin";

    /// <summary>
    /// Initializes a new instance with default <see cref="DurableExecutionOptions"/>.
    /// </summary>
    public DurableAIPlugin()
        : this(new DurableExecutionOptions())
    {
    }

    /// <summary>
    /// Initializes a new instance and applies the configure delegate to a fresh
    /// <see cref="DurableExecutionOptions"/> instance.
    /// </summary>
    /// <param name="configure">Delegate for mutating the options.</param>
    public DurableAIPlugin(Action<DurableExecutionOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        Options = new DurableExecutionOptions();
        configure(Options);
    }

    /// <summary>
    /// Initializes a new instance with an explicit options object. The plugin
    /// holds a reference to the supplied instance — do not mutate it after
    /// passing it in.
    /// </summary>
    public DurableAIPlugin(DurableExecutionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        Options = options;
    }

    /// <summary>
    /// Gets the options carried by this plugin. Used by the
    /// <see cref="TemporalPluginBuilderExtensions.AddWorkerPlugin(Hosting.ITemporalWorkerServiceOptionsBuilder, DurableAIPlugin)"/>
    /// overload to drive DI registration.
    /// </summary>
    internal DurableExecutionOptions Options { get; }

    /// <inheritdoc />
    public string Name => PluginName;

    /// <inheritdoc />
    public void ConfigureWorker(TemporalWorkerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        // Set TaskQueue from options if not already set on the worker.
        if (string.IsNullOrEmpty(options.TaskQueue) && !string.IsNullOrEmpty(Options.TaskQueue))
        {
            options.TaskQueue = Options.TaskQueue;
        }

        // Register the default workflow if enabled and not already present.
        if (Options.RegisterDefaultWorkflow
            && !ContainsWorkflow(options.Workflows, typeof(DurableChatWorkflow)))
        {
            options.AddWorkflow<DurableChatWorkflow>();
        }

        // Note: TemporalWorkerOptions has no ClientOptions. The client (and the
        // DurableAIDataConverter applied to it) is configured separately via the
        // IPostConfigureOptions<TemporalWorkerServiceOptions> path registered by
        // DurableAIRegistrar. The AddWorkerPlugin(DurableAIPlugin) overload wires
        // those configurators alongside this plugin so both halves agree.
    }

    /// <inheritdoc />
    public Task<TResult> RunWorkerAsync<TResult>(
        TemporalWorker worker,
        Func<TemporalWorker, CancellationToken, Task<TResult>> continuation,
        CancellationToken stoppingToken)
    {
        ArgumentNullException.ThrowIfNull(continuation);
        return continuation(worker, stoppingToken);
    }

    /// <inheritdoc />
    public void ConfigureReplayer(WorkflowReplayerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (Options.RegisterDefaultWorkflow
            && !ContainsWorkflow(options.Workflows, typeof(DurableChatWorkflow)))
        {
            options.AddWorkflow<DurableChatWorkflow>();
        }
    }

    /// <inheritdoc />
    public IAsyncEnumerable<WorkflowReplayResult> ReplayWorkflowsAsync(
        WorkflowReplayer replayer,
        Func<WorkflowReplayer, IAsyncEnumerable<WorkflowReplayResult>> continuation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(continuation);
        return continuation(replayer);
    }

    /// <inheritdoc />
    public Task<IEnumerable<WorkflowReplayResult>> ReplayWorkflowsAsync(
        WorkflowReplayer replayer,
        Func<WorkflowReplayer, CancellationToken, Task<IEnumerable<WorkflowReplayResult>>> continuation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(continuation);
        return continuation(replayer, cancellationToken);
    }

    private static bool ContainsWorkflow(IList<WorkflowDefinition> workflows, Type type)
    {
        for (var i = 0; i < workflows.Count; i++)
        {
            if (workflows[i].Type == type)
            {
                return true;
            }
        }
        return false;
    }
}
