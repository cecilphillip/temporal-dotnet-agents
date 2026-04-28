using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Temporalio.Client;
using Temporalio.Extensions.Hosting;

namespace Temporalio.Extensions.AI;

/// <summary>
/// Internal helper that performs the DI side of registering durable AI services.
/// Shared by <see cref="DurableAIServiceCollectionExtensions.AddDurableAI"/> and
/// the <c>DurableAIPlugin</c> entry point so the two paths converge on
/// byte-equivalent DI state. Idempotent — safe to call more than once thanks to
/// <see cref="ServiceCollectionDescriptorExtensions.TryAddEnumerable(IServiceCollection, ServiceDescriptor)"/>
/// and <see cref="ServiceCollectionDescriptorExtensions.TryAddSingleton{TService}(IServiceCollection)"/>.
/// </summary>
internal static class DurableAIRegistrar
{
    /// <summary>
    /// Performs DI registration for durable AI: options, registry, session client,
    /// activities, default workflow, and DurableAIDataConverter auto-wiring.
    /// </summary>
    /// <param name="services">The service collection (always required).</param>
    /// <param name="builder">The worker options builder. When non-null, the
    /// default workflow and activities are registered onto the worker. When null
    /// (e.g., the plugin path that does not have a builder handy at registration
    /// time), only the DI-side registrations are applied.</param>
    /// <param name="options">The configured <see cref="DurableExecutionOptions"/>.</param>
    public static void Register(
        IServiceCollection services,
        ITemporalWorkerServiceOptionsBuilder? builder,
        DurableExecutionOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        // Register options as singleton.
        services.TryAddSingleton(options);

        // Register the function registry (populated by AddDurableTools calls).
        services.TryAddSingleton<DurableFunctionRegistry>();

        // Register the function registry as IReadOnlyDictionary for activity resolution.
        services.TryAddSingleton<IReadOnlyDictionary<string, AIFunction>>(
            sp => sp.GetRequiredService<DurableFunctionRegistry>());

        // Register the session client and default workflow only if enabled.
        if (options.RegisterDefaultWorkflow)
        {
            // Register the session client (concrete + interface alias share the same instance).
            services.TryAddSingleton<DurableChatSessionClient>(sp =>
                new DurableChatSessionClient(
                    sp.GetRequiredService<ITemporalClient>(),
                    options,
                    sp.GetService<ILogger<DurableChatSessionClient>>()));
            services.TryAddSingleton<IDurableChatSessionClient>(
                sp => sp.GetRequiredService<DurableChatSessionClient>());

            // Register the default workflow on the worker, if a builder is available.
            builder?.AddWorkflow<DurableChatWorkflow>();
        }

        // Register activities on the worker (always needed) when a builder is available.
        if (builder is not null)
        {
            builder.AddSingletonActivities<DurableChatActivities>();
            builder.AddSingletonActivities<DurableFunctionActivities>();
            builder.AddSingletonActivities<DurableEmbeddingActivities>();
        }

        // Auto-wire DurableAIDataConverter for both client registration patterns.
        // TryAddEnumerable deduplicates if registration happens more than once.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IConfigureOptions<TemporalClientConnectOptions>,
            DurableAIClientOptionsConfigurator>());

        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IPostConfigureOptions<TemporalWorkerServiceOptions>,
            DurableAIWorkerClientConfigurator>());
    }
}
