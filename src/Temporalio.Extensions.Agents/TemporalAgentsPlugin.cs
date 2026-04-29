using System.Diagnostics.CodeAnalysis;
using Temporalio.Worker;
using Temporalio.Workflows;

namespace Temporalio.Extensions.Agents;

/// <summary>
/// An <see cref="ITemporalWorkerPlugin"/> that registers the Agents workflows
/// on the worker.
/// </summary>
/// <remarks>
/// <para>
/// This is a parallel registration path to
/// <see cref="TemporalWorkerBuilderExtensions.AddTemporalAgents"/>. The two paths
/// are intended to converge on identical DI state.
/// </para>
/// <para>
/// Activities cannot be registered from <see cref="ConfigureWorker"/> directly
/// (they need DI and there is no <see cref="IServiceProvider"/> available at
/// this hook). Use <see cref="TemporalWorkerBuilderExtensions.AddWorkerPlugin(Temporalio.Extensions.Hosting.ITemporalWorkerServiceOptionsBuilder, TemporalAgentsPlugin)"/>
/// to register both the plugin and the DI side in one call.
/// </para>
/// <para>
/// WARNING: This API is experimental and may change in future releases.
/// Suppress diagnostic <c>TA001</c> to opt in.
/// </para>
/// </remarks>
[Experimental("TA001")]
public sealed class TemporalAgentsPlugin : ITemporalWorkerPlugin
{
    /// <summary>
    /// The canonical plugin name used to identify this plugin in worker plugin lists.
    /// </summary>
    public const string PluginName = "Temporalio.Extensions.Agents.TemporalAgentsPlugin";

    /// <summary>
    /// Initializes a new instance with default <see cref="TemporalAgentsOptions"/>.
    /// </summary>
    public TemporalAgentsPlugin()
        : this(new TemporalAgentsOptions())
    {
    }

    /// <summary>
    /// Initializes a new instance and applies the configure delegate to a fresh
    /// <see cref="TemporalAgentsOptions"/> instance.
    /// </summary>
    /// <param name="configure">Delegate for mutating the options.</param>
    public TemporalAgentsPlugin(Action<TemporalAgentsOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        Options = new TemporalAgentsOptions();
        configure(Options);
    }

    /// <summary>
    /// Initializes a new instance with an explicit options object. The plugin
    /// holds a reference to the supplied instance — do not mutate it after
    /// passing it in.
    /// </summary>
    public TemporalAgentsPlugin(TemporalAgentsOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        Options = options;
    }

    /// <summary>
    /// Gets the options carried by this plugin. Used by the
    /// <see cref="TemporalWorkerBuilderExtensions.AddWorkerPlugin(Temporalio.Extensions.Hosting.ITemporalWorkerServiceOptionsBuilder, TemporalAgentsPlugin)"/>
    /// overload to drive DI registration.
    /// </summary>
    internal TemporalAgentsOptions Options { get; }

    /// <inheritdoc/>
    public string Name => PluginName;

    /// <inheritdoc/>
    public void ConfigureWorker(TemporalWorkerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!ContainsWorkflow(options.Workflows, typeof(Workflows.AgentWorkflow)))
            options.AddWorkflow<Workflows.AgentWorkflow>();
        if (!ContainsWorkflow(options.Workflows, typeof(Workflows.AgentJobWorkflow)))
            options.AddWorkflow<Workflows.AgentJobWorkflow>();
    }

    /// <inheritdoc/>
    public Task<TResult> RunWorkerAsync<TResult>(
        TemporalWorker worker,
        Func<TemporalWorker, CancellationToken, Task<TResult>> continuation,
        CancellationToken stoppingToken)
    {
        ArgumentNullException.ThrowIfNull(continuation);
        return continuation(worker, stoppingToken);
    }

    /// <inheritdoc/>
    public void ConfigureReplayer(WorkflowReplayerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!ContainsWorkflow(options.Workflows, typeof(Workflows.AgentWorkflow)))
            options.AddWorkflow<Workflows.AgentWorkflow>();
    }

    /// <inheritdoc/>
    public IAsyncEnumerable<WorkflowReplayResult> ReplayWorkflowsAsync(
        WorkflowReplayer replayer,
        Func<WorkflowReplayer, IAsyncEnumerable<WorkflowReplayResult>> continuation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(continuation);
        return continuation(replayer);
    }

    /// <inheritdoc/>
    public Task<IEnumerable<WorkflowReplayResult>> ReplayWorkflowsAsync(
        WorkflowReplayer replayer,
        Func<WorkflowReplayer, CancellationToken, Task<IEnumerable<WorkflowReplayResult>>> continuation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(continuation);
        return continuation(replayer, cancellationToken);
    }

    private static bool ContainsWorkflow(IList<WorkflowDefinition> workflows, Type type) =>
        workflows.Any(w => w.Type == type);
}
