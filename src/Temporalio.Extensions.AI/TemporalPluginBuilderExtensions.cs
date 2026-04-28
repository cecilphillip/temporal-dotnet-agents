using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Options;
using Temporalio.Client;
using Temporalio.Extensions.Hosting;
using Temporalio.Worker;

namespace Temporalio.Extensions.AI;

/// <summary>
/// Extension methods for adding Temporal SDK plugins to worker and client option builders.
/// </summary>
/// <remarks>
/// WARNING: These APIs are experimental and may change in future releases.
/// Suppress diagnostic <c>TAI001</c> to opt in.
/// </remarks>
public static class TemporalPluginBuilderExtensions
{
    /// <summary>
    /// Adds a worker plugin to the hosted Temporal worker.
    /// </summary>
    /// <param name="builder">The worker options builder.</param>
    /// <param name="plugin">The worker plugin to add.</param>
    /// <returns>The same builder for further chaining.</returns>
    [Experimental("TAI001")]
    public static ITemporalWorkerServiceOptionsBuilder AddWorkerPlugin(
        this ITemporalWorkerServiceOptionsBuilder builder,
        ITemporalWorkerPlugin plugin)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(plugin);
        return builder.ConfigureOptions(opts =>
        {
            var list = opts.Plugins?.ToList() ?? [];
            list.Add(plugin);
            opts.Plugins = list;
        });
    }

    /// <summary>
    /// Adds a <see cref="DurableAIPlugin"/> to the worker AND registers the
    /// matching DI services (workflow, activities, function registry, session
    /// client, options, and DurableAIDataConverter auto-wiring) in one call.
    /// </summary>
    /// <param name="builder">The worker options builder.</param>
    /// <param name="plugin">The durable AI plugin to add.</param>
    /// <returns>The same builder for further chaining.</returns>
    /// <remarks>
    /// <para>
    /// This is a convenience overload over the generic
    /// <see cref="AddWorkerPlugin(Hosting.ITemporalWorkerServiceOptionsBuilder, ITemporalWorkerPlugin)"/>
    /// that handles the activities-via-DI constraint: activities cannot be
    /// registered from a plugin's <c>ConfigureWorker</c> hook because there is no
    /// <see cref="IServiceProvider"/> available there. By calling
    /// <see cref="DurableAIServiceCollectionExtensions.AddDurableAI"/>'s underlying
    /// registrar here, both halves of the registration agree.
    /// </para>
    /// <para>
    /// The DI registration is idempotent — if
    /// <see cref="DurableAIServiceCollectionExtensions.AddDurableAI"/> has already
    /// been called on the same builder, this overload will not double-register.
    /// </para>
    /// <para>
    /// WARNING: This API is experimental and may change in future releases.
    /// Suppress diagnostic <c>TAI001</c> to opt in.
    /// </para>
    /// </remarks>
    [Experimental("TAI001")]
    public static ITemporalWorkerServiceOptionsBuilder AddWorkerPlugin(
        this ITemporalWorkerServiceOptionsBuilder builder,
        DurableAIPlugin plugin)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(plugin);

        // Mirror AddDurableAI's TaskQueue defaulting: if the plugin's options
        // don't carry a queue, inherit it from the worker builder so that
        // DurableExecutionOptions.Validate() succeeds.
        if (string.IsNullOrEmpty(plugin.Options.TaskQueue))
        {
            plugin.Options.TaskQueue = builder.TaskQueue;
        }
        plugin.Options.Validate();

        // 1. Register DI services (workflow, activities, registry, session
        //    client, options singleton, and DurableAIDataConverter wiring).
        DurableAIRegistrar.Register(builder.Services, builder, plugin.Options);

        // 2. Add the plugin to the worker plugin chain via the generic overload.
        return AddWorkerPlugin(builder, (ITemporalWorkerPlugin)plugin);
    }

    /// <summary>
    /// Adds a client plugin to the hosted Temporal worker's client connection.
    /// </summary>
    /// <param name="builder">The worker options builder.</param>
    /// <param name="plugin">The client plugin to add.</param>
    /// <returns>The same builder for further chaining.</returns>
    /// <remarks>
    /// Only applies when the worker service creates its own client (3-arg
    /// <c>AddHostedTemporalWorker</c> overload). For <c>AddTemporalClient()</c> use the
    /// <see cref="AddClientPlugin(OptionsBuilder{TemporalClientConnectOptions}, ITemporalClientPlugin)"/>
    /// overload instead.
    /// </remarks>
    [Experimental("TAI001")]
    public static ITemporalWorkerServiceOptionsBuilder AddClientPlugin(
        this ITemporalWorkerServiceOptionsBuilder builder,
        ITemporalClientPlugin plugin)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(plugin);
        return builder.ConfigureOptions(opts =>
        {
            if (opts.ClientOptions is null) return;
            var list = opts.ClientOptions.Plugins?.ToList() ?? [];
            list.Add(plugin);
            opts.ClientOptions.Plugins = list;
        });
    }

    /// <summary>
    /// Adds a client plugin to the <see cref="TemporalClientConnectOptions"/> options builder.
    /// </summary>
    /// <param name="builder">The options builder for <see cref="TemporalClientConnectOptions"/>.</param>
    /// <param name="plugin">The client plugin to add.</param>
    /// <returns>The same builder for further chaining.</returns>
    /// <remarks>
    /// Use with the <see cref="OptionsBuilder{TOptions}"/> returned by <c>AddTemporalClient()</c>
    /// for client-only registrations that have no hosted worker.
    /// </remarks>
    [Experimental("TAI001")]
    public static OptionsBuilder<TemporalClientConnectOptions> AddClientPlugin(
        this OptionsBuilder<TemporalClientConnectOptions> builder,
        ITemporalClientPlugin plugin)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(plugin);
        return builder.Configure(opts =>
        {
            var list = opts.Plugins?.ToList() ?? [];
            list.Add(plugin);
            opts.Plugins = list;
        });
    }
}
