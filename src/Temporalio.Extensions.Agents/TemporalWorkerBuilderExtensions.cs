using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Temporalio.Extensions.Agents.HistoryStore;
using Temporalio.Extensions.Hosting;

namespace Temporalio.Extensions.Agents;

/// <summary>
/// Extension methods for <see cref="ITemporalWorkerServiceOptionsBuilder"/> that register
/// Temporal agent infrastructure onto an already-configured worker.
/// </summary>
public static class TemporalWorkerBuilderExtensions
{
    /// <summary>
    /// Registers Temporal Agent infrastructure on the worker: agent factories,
    /// <see cref="ITemporalAgentClient"/>, keyed <see cref="Microsoft.Agents.AI.AIAgent"/> proxy singletons,
    /// <see cref="Workflows.AgentWorkflow"/>, and <see cref="Workflows.AgentActivities"/>.
    /// </summary>
    /// <remarks>
    /// This method expects an <see cref="Temporalio.Client.ITemporalClient"/> to already be present in the
    /// service container, either from using the
    /// <c>AddHostedTemporalWorker(clientTargetHost, clientNamespace, taskQueue)</c> overload
    /// or from a prior call to <c>services.AddTemporalClient(...)</c>.
    /// </remarks>
    /// <param name="builder">The worker options builder returned by AddHostedTemporalWorker.</param>
    /// <param name="configure">Delegate to configure <see cref="TemporalAgentsOptions"/>.</param>
    /// <returns>The same builder for further chaining.</returns>
    public static ITemporalWorkerServiceOptionsBuilder AddTemporalAgents(
        this ITemporalWorkerServiceOptionsBuilder builder,
        Action<TemporalAgentsOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);

        // Fail-fast: detect a duplicate registration on the same worker builder before it can
        // silently override the agent factory dictionary at runtime. Mirrors AddDurableTools'
        // precondition check in the AI library.
        if (builder.Services.Any(d => d.ServiceType == typeof(TemporalAgentsOptions)))
        {
            throw new InvalidOperationException(
                "AddTemporalAgents has already been called on this worker builder. " +
                "Calling it twice would silently override the agent factory dictionary. " +
                "Configure all agents in a single AddTemporalAgents call.");
        }

        var agentsOptions = new TemporalAgentsOptions();
        configure(agentsOptions);

        TemporalAgentsRegistrar.Register(builder.Services, builder, agentsOptions);

        return builder;
    }

    /// <summary>
    /// Convenience helper that registers <typeparamref name="TStore"/> as the singleton
    /// <see cref="IAgentHistoryStore"/> implementation. Equivalent to calling
    /// <c>builder.Services.AddSingleton&lt;IAgentHistoryStore, TStore&gt;()</c> directly.
    /// </summary>
    /// <remarks>
    /// Pair this with <c>opts.UseExternalHistory = true</c> on the matching
    /// <see cref="AddTemporalAgents"/> call. Order does not matter as long as both run
    /// before the worker host is built — the store registration is checked at composition
    /// time inside <see cref="AddTemporalAgents"/>.
    /// </remarks>
    /// <typeparam name="TStore">Concrete <see cref="IAgentHistoryStore"/> implementation.</typeparam>
    /// <param name="builder">The worker options builder.</param>
    /// <returns>The same builder for further chaining.</returns>
    public static ITemporalWorkerServiceOptionsBuilder UseExternalAgentHistory<TStore>(
        this ITemporalWorkerServiceOptionsBuilder builder)
        where TStore : class, IAgentHistoryStore
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.TryAddSingleton<IAgentHistoryStore, TStore>();
        return builder;
    }

    /// <summary>
    /// Registers a <see cref="TemporalAgentsPlugin"/> on the worker and its associated DI services.
    /// </summary>
    /// <param name="builder">The worker options builder returned by AddHostedTemporalWorker.</param>
    /// <param name="plugin">The agents plugin to add.</param>
    /// <returns>The same builder for further chaining.</returns>
    [Experimental("TA001")]
    public static ITemporalWorkerServiceOptionsBuilder AddWorkerPlugin(
        this ITemporalWorkerServiceOptionsBuilder builder,
        TemporalAgentsPlugin plugin)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(plugin);

        TemporalAgentsRegistrar.Register(builder.Services, builder, plugin.Options);
        builder.ConfigureOptions(opts =>
        {
            var list = opts.Plugins?.ToList() ?? [];
            list.Add(plugin);
            opts.Plugins = list;
        });
        return builder;
    }
}
