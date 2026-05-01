using Microsoft.Agents.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Temporalio.Client;
using Temporalio.Extensions.Agents.Workflows;
using Temporalio.Extensions.AI;
using Temporalio.Extensions.Hosting;

namespace Temporalio.Extensions.Agents;

/// <summary>
/// Internal helper that performs the DI side of registering agent services.
/// Shared by <see cref="TemporalWorkerBuilderExtensions.AddTemporalAgents"/> and
/// <see cref="TemporalAgentsPlugin"/> so both paths converge on identical DI state.
/// Idempotent — safe to call more than once thanks to
/// <see cref="ServiceCollectionDescriptorExtensions.TryAddSingleton{TService}(IServiceCollection)"/>
/// and <see cref="ServiceCollectionDescriptorExtensions.TryAddEnumerable"/>.
/// </summary>
internal static class TemporalAgentsRegistrar
{
    /// <summary>
    /// Performs DI registration for temporal agents: options, factory dictionary,
    /// agent client, keyed proxies, workflow, activities, and DurableAIDataConverter
    /// auto-wiring.
    /// </summary>
    /// <param name="services">The service collection (always required).</param>
    /// <param name="builder">The worker options builder. When non-null, the
    /// workflows and activities are registered onto the worker. When null,
    /// only the DI-side registrations are applied.</param>
    /// <param name="agentsOptions">The configured <see cref="TemporalAgentsOptions"/>.</param>
    public static void Register(
        IServiceCollection services,
        ITemporalWorkerServiceOptionsBuilder? builder,
        TemporalAgentsOptions agentsOptions)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(agentsOptions);

        var taskQueue = builder?.TaskQueue ?? string.Empty;

        // Agent factory dictionary — consumed by AgentActivities to resolve real agent instances.
        services.TryAddSingleton<IReadOnlyDictionary<string, Func<IServiceProvider, AIAgent>>>(
            _ => agentsOptions.GetAgentFactories());

        // Options singleton — consumed by DefaultTemporalAgentClient for per-agent TTL resolution.
        services.TryAddSingleton(agentsOptions);

        // ITemporalAgentClient — uses WorkflowUpdate for synchronous request/response semantics.
        // TryAddSingleton allows callers to pre-register a custom implementation (e.g. a test double).
        services.TryAddSingleton<ITemporalAgentClient>(sp =>
            new DefaultTemporalAgentClient(
                sp.GetRequiredService<ITemporalClient>(),
                agentsOptions,
                taskQueue,
                sp.GetService<ILogger<DefaultTemporalAgentClient>>()));

        // Register a keyed AIAgent proxy singleton per declared agent name.
        foreach (var (name, _) in agentsOptions.GetAgentFactories())
        {
            var agentName = name;
            services.AddKeyedSingleton<AIAgent>(agentName, (sp, _) =>
                new TemporalAIAgentProxy(
                    agentName,
                    sp.GetRequiredService<ITemporalAgentClient>(),
                    sp.GetService<ILogger<TemporalAIAgentProxy>>()));
        }

        if (builder is not null)
        {
            // Register the durable session workflow and activity implementations on this worker.
            builder.AddWorkflow<AgentWorkflow>();
            builder.AddSingletonActivities<AgentActivities>();

            // AgentJobWorkflow: simple fire-and-forget workflow for scheduled/deferred runs.
            builder.AddWorkflow<AgentJobWorkflow>();

            // ScheduleActivities: enables one-time deferred runs from orchestrating workflows.
            services.AddSingleton(sp => new ScheduleActivities(
                sp.GetRequiredService<ITemporalClient>(),
                taskQueue));
            builder.AddSingletonActivities<ScheduleActivities>();

            // ScheduleRegistrationService: creates configured schedules at worker startup.
            if (agentsOptions.GetScheduledRuns().Count > 0)
            {
                services.AddHostedService<ScheduleRegistrationService>(sp =>
                    new ScheduleRegistrationService(
                        sp.GetRequiredService<ITemporalAgentClient>(),
                        agentsOptions,
                        sp.GetService<ILogger<ScheduleRegistrationService>>()));
            }
        }

        // Auto-wire TemporalAgentDataConverter. Mirrors what AddDurableAI() does for the AI
        // library — except the MAF converter is a strict superset that also handles the
        // agent-specific session-entry subclasses (AgentSessionRequest / AgentSessionResponse).
        // The MAF converter accepts and overrides DurableAIDataConverter via its plugin's
        // dual-precondition guard, so mixing AddTemporalAgents() with AddDurableAI() on the
        // same builder produces the MAF-aware converter (which is what we want — it preserves
        // all AI-library serialization behavior plus the agent extensions).
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IConfigureOptions<TemporalClientConnectOptions>,
            TemporalAgentClientOptionsConfigurator>());

        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IPostConfigureOptions<TemporalWorkerServiceOptions>,
            TemporalAgentWorkerClientConfigurator>());
    }
}
