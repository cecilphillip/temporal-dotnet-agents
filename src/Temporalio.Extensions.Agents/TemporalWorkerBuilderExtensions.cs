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

        // If UseExternalAgentHistory<TStore>() was called *before* AddTemporalAgents on the
        // same builder, the marker singleton is present. Honor the user's intent by flipping
        // the flag on the freshly built options, regardless of what `configure` set, so the
        // single-call UX advertised by UseExternalAgentHistory's XML doc is correct.
        if (builder.Services.Any(d => d.ServiceType == typeof(ExternalHistoryMarker)))
        {
            agentsOptions.UseExternalHistory = true;
        }

        TemporalAgentsRegistrar.Register(builder.Services, builder, agentsOptions);

        return builder;
    }

    /// <summary>
    /// One-call convenience helper that registers <typeparamref name="TStore"/> as the
    /// singleton <see cref="IAgentHistoryStore"/> implementation AND flips
    /// <see cref="TemporalAgentsOptions.UseExternalHistory"/> to <see langword="true"/> on
    /// the matching <see cref="AddTemporalAgents"/> call. The user does not need to set the
    /// flag separately.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Order does not matter as long as both run before the worker host is built. When this
    /// method is called <em>before</em> <see cref="AddTemporalAgents"/>, an internal marker
    /// is dropped onto the service collection and the next <see cref="AddTemporalAgents"/>
    /// call reads it after running the user's configure delegate, forcing
    /// <c>UseExternalHistory = true</c>. When this method is called <em>after</em>
    /// <see cref="AddTemporalAgents"/>, the singleton <see cref="TemporalAgentsOptions"/>
    /// is mutated in place.
    /// </para>
    /// <para>
    /// The store registration is checked at composition time inside
    /// <see cref="AddTemporalAgents"/>; a misconfigured pairing fails fast on worker startup.
    /// </para>
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

        // Drop a marker so a subsequent AddTemporalAgents call can flip UseExternalHistory.
        builder.Services.TryAddSingleton<ExternalHistoryMarker>(_ => ExternalHistoryMarker.Instance);

        // If AddTemporalAgents already ran (the options singleton is registered as an instance),
        // mutate the existing options directly so this method is order-independent.
        var optionsDescriptor = builder.Services.FirstOrDefault(
            d => d.ServiceType == typeof(TemporalAgentsOptions));
        if (optionsDescriptor?.ImplementationInstance is TemporalAgentsOptions existingOptions)
        {
            existingOptions.UseExternalHistory = true;
        }

        return builder;
    }

    /// <summary>
    /// Internal marker used by <see cref="UseExternalAgentHistory{TStore}"/> to communicate
    /// intent across to a later <see cref="AddTemporalAgents"/> call on the same builder.
    /// </summary>
    internal sealed class ExternalHistoryMarker
    {
        public static readonly ExternalHistoryMarker Instance = new();
        private ExternalHistoryMarker() { }
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
