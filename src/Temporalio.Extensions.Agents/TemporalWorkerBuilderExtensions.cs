using Microsoft.Agents.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Temporalio.Client;
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
        ArgumentNullException.ThrowIfNull(configure);

        var agentsOptions = new TemporalAgentsOptions();
        configure(agentsOptions);

        var taskQueue = builder.TaskQueue;
        var services = builder.Services;

        // Agent factory dictionary — consumed by AgentActivities to resolve real agent instances.
        // Uses AddSingleton (not Try) so a duplicate AddTemporalAgents call on the same worker
        // produces a clear failure rather than silently using the first registration.
        services.AddSingleton<IReadOnlyDictionary<string, Func<IServiceProvider, AIAgent>>>(
            _ => agentsOptions.GetAgentFactories());

        // Options singleton — consumed by DefaultTemporalAgentClient for per-agent TTL resolution.
        services.AddSingleton(agentsOptions);

        // Register AIAgentRouter when a router agent has been configured.
        if (agentsOptions.GetRouterAgent() is { } routerAgent)
        {
            services.TryAddSingleton<IAgentRouter>(new AIAgentRouter(routerAgent));
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

        return builder;
    }
}
