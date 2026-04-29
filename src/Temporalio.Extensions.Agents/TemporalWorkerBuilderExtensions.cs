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
/// Extension methods for <see cref="ITemporalWorkerServiceOptionsBuilder"/> that register
/// Temporal agent infrastructure onto an already-configured worker.
/// </summary>
public static class TemporalWorkerBuilderExtensions
{
    /// <summary>
    /// Registers Temporal Agent infrastructure on the worker: agent factories,
    /// <see cref="ITemporalAgentClient"/>, keyed <see cref="AIAgent"/> proxy singletons,
    /// <see cref="AgentWorkflow"/>, and <see cref="AgentActivities"/>.
    /// </summary>
    /// <remarks>
    /// This method expects an <see cref="ITemporalClient"/> to already be present in the
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

        var taskQueue = builder.TaskQueue;
        var services = builder.Services;

        // Agent factory dictionary — consumed by AgentActivities to resolve real agent instances.
        // TryAddSingleton keeps the registration idempotent; the fail-fast guard above already
        // rejects a duplicate AddTemporalAgents call before we get here.
        services.TryAddSingleton<IReadOnlyDictionary<string, Func<IServiceProvider, AIAgent>>>(
            _ => agentsOptions.GetAgentFactories());

        // Options singleton — consumed by DefaultTemporalAgentClient for per-agent TTL resolution.
        services.TryAddSingleton(agentsOptions);

        // Register AIAgentRouter when a router agent has been configured.
        if (agentsOptions.GetRouterAgent() is { } routerAgent)
        {
            services.TryAddSingleton<IAgentRouter>(sp =>
                new AIAgentRouter(routerAgent, sp.GetService<ILogger<AIAgentRouter>>()));
        }

        // ITemporalAgentClient — uses WorkflowUpdate for synchronous request/response semantics.
        // TryAddSingleton allows callers to pre-register a custom implementation (e.g. a test double).
        services.TryAddSingleton<ITemporalAgentClient>(sp =>
            new DefaultTemporalAgentClient(
                sp.GetRequiredService<ITemporalClient>(),
                agentsOptions,
                taskQueue,
                sp.GetService<ILogger<DefaultTemporalAgentClient>>(),
                sp.GetService<IAgentRouter>()));

        // Register a keyed AIAgent proxy singleton per declared agent name.
        // Note: this loop also registers proxies for entries declared via AddAgentProxy(),
        // even though their factories throw when invoked locally. This is intentional —
        // the keyed TemporalAIAgentProxy never calls the factory; it routes RunAsync calls
        // through Temporal updates. Registering the proxy in the worker process makes it
        // resolvable as a keyed AIAgent (e.g., in single-process apps that use the same
        // service container for both worker hosting and external agent invocation).
        foreach (var (name, _) in agentsOptions.GetAgentFactories())
        {
            var agentName = name;
            services.AddKeyedSingleton<AIAgent>(agentName, (sp, _) =>
                new TemporalAIAgentProxy(
                    agentName,
                    sp.GetRequiredService<ITemporalAgentClient>(),
                    sp.GetService<ILogger<TemporalAIAgentProxy>>()));
        }

        // Register the durable session workflow and activity implementations on this worker.
        builder.AddWorkflow<AgentWorkflow>();
        builder.AddSingletonActivities<AgentActivities>();

        // ── Scheduling support ──────────────────────────────────────────────

        // AgentJobWorkflow: simple fire-and-forget workflow for scheduled/deferred runs.
        builder.AddWorkflow<AgentJobWorkflow>();

        // ScheduleActivities: enables one-time deferred runs from orchestrating workflows.
        // Pre-register with a factory so the taskQueue closure is captured correctly.
        // AddSingletonActivities uses TryAddSingleton internally, so it respects this registration.
        services.AddSingleton(sp => new ScheduleActivities(
            sp.GetRequiredService<ITemporalClient>(),
            taskQueue));
        builder.AddSingletonActivities<ScheduleActivities>();

        // ScheduleRegistrationService: creates configured schedules at worker startup.
        // Only registered when at least one scheduled run has been declared.
        // Uses AddHostedService (TryAddEnumerable) rather than AddSingleton<IHostedService> so
        // that deduplication is keyed on (IHostedService, ScheduleRegistrationService) — not just
        // IHostedService — leaving all other hosted service registrations unaffected.
        if (agentsOptions.GetScheduledRuns().Count > 0)
        {
            services.AddHostedService<ScheduleRegistrationService>(sp =>
                new ScheduleRegistrationService(
                    sp.GetRequiredService<ITemporalAgentClient>(),
                    agentsOptions,
                    sp.GetService<ILogger<ScheduleRegistrationService>>()));
        }

        // Auto-wire DurableAIDataConverter — mirrors what AddDurableAI() does so that callers
        // using AddTemporalAgents() without an explicit AddDurableAI() call still get the
        // correct data converter applied. TryAddEnumerable is idempotent on
        // (ServiceType, ImplementationType), so calling both AddDurableAI() and
        // AddTemporalAgents() in the same app will not double-register these configurators.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IConfigureOptions<TemporalClientConnectOptions>,
            DurableAIClientOptionsConfigurator>());

        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IPostConfigureOptions<TemporalWorkerServiceOptions>,
            DurableAIWorkerClientConfigurator>());

        return builder;
    }
}
