using Microsoft.Agents.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Temporalio.Client;
using Temporalio.Extensions.Hosting;

namespace Temporalio.Extensions.Agents;

/// <summary>
/// Agent-specific extension methods for <see cref="IServiceCollection"/>.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Gets a registered Temporal agent proxy by name.
    /// </summary>
    public static AIAgent GetTemporalAgentProxy(this IServiceProvider services, string name)
    {
        return services.GetKeyedService<AIAgent>(name)
            ?? throw new KeyNotFoundException($"A Temporal agent with name '{name}' has not been registered.");
    }

    /// <summary>
    /// Configures Temporal Agents: registers agent factories, a hosted Temporal worker,
    /// <see cref="ITemporalAgentClient"/>, and keyed proxy singletons.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Delegate to configure <see cref="TemporalAgentsOptions"/>.</param>
    /// <param name="taskQueue">The Temporal task queue for the agent worker.</param>
    /// <param name="targetHost">Optional Temporal server address (e.g. "localhost:7233").
    /// When provided, an <see cref="ITemporalClient"/> is registered.</param>
    /// <param name="namespace">Optional Temporal namespace. Defaults to "default".</param>
    /// <param name="workerBuilder">Optional delegate to further configure the worker.</param>
    public static IServiceCollection ConfigureTemporalAgents(
        this IServiceCollection services,
        Action<TemporalAgentsOptions> configure,
        string taskQueue,
        string? targetHost = null,
        string? @namespace = null,
        Action<ITemporalWorkerServiceOptionsBuilder>? workerBuilder = null)
    {
        ArgumentNullException.ThrowIfNull(configure);
        ArgumentException.ThrowIfNullOrWhiteSpace(taskQueue);

        // Optionally register an ITemporalClient if connection info was provided.
        if (targetHost is not null)
        {
            services.AddTemporalClient(targetHost, @namespace ?? "default");
        }

        // Register the hosted Temporal worker with agent infrastructure via the new extension.
        var workerBuilder_ = services
            .AddHostedTemporalWorker(taskQueue)
            .AddTemporalAgents(configure);

        workerBuilder?.Invoke(workerBuilder_);

        return services;
    }

    /// <summary>
    /// Registers client-side Temporal Agent infrastructure only: an <see cref="ITemporalAgentClient"/>
    /// and keyed <see cref="AIAgent"/> proxy singletons. No Temporal worker is registered.
    /// </summary>
    /// <remarks>
    /// Use this in processes that only send messages to agent sessions (e.g. an API server, CLI tool)
    /// when the Temporal worker runs in a separate process. Declare the agents you want proxies for
    /// using <see cref="TemporalAgentsOptions.AddAgentProxy"/> inside the <paramref name="configure"/> delegate.
    /// </remarks>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Delegate to declare proxy agent names and optional TTLs.</param>
    /// <param name="taskQueue">The Temporal task queue that the worker is listening on.</param>
    /// <param name="targetHost">Optional Temporal server address (e.g. "localhost:7233").
    /// When provided, an <see cref="ITemporalClient"/> is registered.</param>
    /// <param name="namespace">Optional Temporal namespace. Defaults to "default".</param>
    public static IServiceCollection AddTemporalAgentProxies(
        this IServiceCollection services,
        Action<TemporalAgentsOptions> configure,
        string taskQueue,
        string? targetHost = null,
        string? @namespace = null)
    {
        ArgumentNullException.ThrowIfNull(configure);
        ArgumentException.ThrowIfNullOrWhiteSpace(taskQueue);

        var options = new TemporalAgentsOptions();
        configure(options);

        // Options are used by DefaultTemporalAgentClient for TTL resolution when starting sessions.
        services.AddSingleton(options);

        if (targetHost is not null)
        {
            services.AddTemporalClient(targetHost, @namespace ?? "default");
        }

        services.AddSingleton<ITemporalAgentClient>(sp =>
            new DefaultTemporalAgentClient(
                sp.GetRequiredService<ITemporalClient>(),
                options,
                taskQueue,
                sp.GetService<ILogger<DefaultTemporalAgentClient>>()));

        // Register a keyed proxy for every declared agent name.
        // The real agent implementation lives in the worker process — no factory needed here.
        foreach (var (name, _) in options.GetAgentFactories())
        {
            services.AddKeyedSingleton<AIAgent>(name, (sp, _) =>
                new TemporalAIAgentProxy(
                    name,
                    sp.GetRequiredService<ITemporalAgentClient>(),
                    sp.GetService<ILogger<TemporalAIAgentProxy>>()));
        }

        return services;
    }

    /// <summary>Validates that the named agent is registered.</summary>
    internal static void ValidateAgentIsRegistered(IServiceProvider services, string agentName)
    {
        var agents = services.GetService<IReadOnlyDictionary<string, Func<IServiceProvider, AIAgent>>>()
            ?? throw new InvalidOperationException(
                $"Temporal agents have not been configured. Call {nameof(ConfigureTemporalAgents)} first.");

        if (!agents.ContainsKey(agentName))
        {
            throw new AgentNotRegisteredException(agentName);
        }
    }
}
