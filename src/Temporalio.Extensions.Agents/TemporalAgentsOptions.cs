using Microsoft.Agents.AI;

namespace Temporalio.Extensions.Agents;

/// <summary>
/// Options for configuring Temporal agents.
/// </summary>
public sealed class TemporalAgentsOptions
{
    // Agent names are case-insensitive
    private readonly Dictionary<string, Func<IServiceProvider, AIAgent>> _agentFactories =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, TimeSpan?> _agentTimeToLive =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, string> _agentDescriptors =
        new(StringComparer.OrdinalIgnoreCase);

    private AIAgent? _routerAgent;

    internal TemporalAgentsOptions()
    {
    }

    /// <summary>
    /// Gets or sets the default TTL for agent workflows. Defaults to 14 days.
    /// Set to <see langword="null"/> to disable TTL for agents without explicit TTL configuration.
    /// </summary>
    public TimeSpan? DefaultTimeToLive { get; set; } = TimeSpan.FromDays(14);

    /// <summary>
    /// Gets or sets the <c>StartToCloseTimeout</c> applied to every
    /// <see cref="AgentActivities.ExecuteAgentAsync"/> activity invocation.
    /// This bounds the total wall-clock time allowed for one agent turn, including
    /// any tool calls and retries within that turn.
    /// When <see langword="null"/>, the workflow uses a 30-minute default.
    /// </summary>
    public TimeSpan? ActivityStartToCloseTimeout { get; set; }

    /// <summary>
    /// Gets or sets the <c>HeartbeatTimeout</c> for agent activity invocations.
    /// If the activity does not send a heartbeat within this interval Temporal
    /// considers it lost and schedules a retry. Relevant when streaming is enabled
    /// because the activity heartbeats on each streamed chunk.
    /// When <see langword="null"/>, the workflow uses a 5-minute default.
    /// </summary>
    public TimeSpan? ActivityHeartbeatTimeout { get; set; }

    /// <summary>Adds an agent factory with an optional per-agent TTL.</summary>
    public TemporalAgentsOptions AddAIAgentFactory(string name, Func<IServiceProvider, AIAgent> factory, TimeSpan? timeToLive = null)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(factory);
        _agentFactories.Add(name, factory);
        if (timeToLive.HasValue)
        {
            _agentTimeToLive[name] = timeToLive;
        }

        return this;
    }

    /// <summary>Adds multiple agents at once.</summary>
    public TemporalAgentsOptions AddAIAgents(params IEnumerable<AIAgent> agents)
    {
        ArgumentNullException.ThrowIfNull(agents);
        foreach (var agent in agents)
        {
            AddAIAgent(agent);
        }

        return this;
    }

    /// <summary>Adds a single agent with an optional per-agent TTL.</summary>
    public TemporalAgentsOptions AddAIAgent(AIAgent agent, TimeSpan? timeToLive = null)
    {
        ArgumentNullException.ThrowIfNull(agent);

        if (string.IsNullOrWhiteSpace(agent.Name))
        {
            throw new ArgumentException($"{nameof(agent.Name)} must not be null or whitespace.", nameof(agent));
        }

        if (_agentFactories.ContainsKey(agent.Name))
        {
            throw new ArgumentException($"An agent with name '{agent.Name}' has already been registered.", nameof(agent));
        }

        _agentFactories.Add(agent.Name, _ => agent);
        if (timeToLive.HasValue)
        {
            _agentTimeToLive[agent.Name] = timeToLive;
        }

        return this;
    }

    /// <summary>
    /// Declares a named agent proxy for client-only scenarios where the real agent
    /// implementation runs in a separate worker process.
    /// No factory is required; call this from <see cref="ServiceCollectionExtensions.AddTemporalAgentProxies"/>
    /// instead of <see cref="AddAIAgent"/> or <see cref="AddAIAgentFactory"/>.
    /// </summary>
    public TemporalAgentsOptions AddAgentProxy(string name, TimeSpan? timeToLive = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        if (_agentFactories.ContainsKey(name))
        {
            throw new ArgumentException($"An agent with name '{name}' has already been registered.", nameof(name));
        }

        // Guard factory — if somehow invoked from a worker context it fails fast with a clear message.
        _agentFactories.Add(name, _ => throw new InvalidOperationException(
            $"Agent '{name}' was registered with AddAgentProxy() for client-only use. " +
            $"Register the real agent via AddAIAgent() or AddAIAgentFactory() in the worker process."));

        if (timeToLive.HasValue)
        {
            _agentTimeToLive[name] = timeToLive;
        }

        return this;
    }

    // ── Async factory overload (GAP 7: MCP convenience) ──────────────────────

    /// <summary>
    /// Adds an agent using an <c>async</c> factory.
    /// The factory is invoked synchronously (blocking) during worker startup, not on hot paths.
    /// </summary>
    /// <remarks>
    /// Use this overload when agent setup requires async work, such as connecting to an MCP
    /// server and listing its tools:
    /// <code>
    /// opts.AddAIAgentFactory("MyAgent", async sp =>
    /// {
    ///     // McpClientTool extends AIFunction (MEAI-native) — no adapter needed.
    ///     var mcpClient = await McpClientFactory.CreateAsync(transport);
    ///     var mcpTools  = await mcpClient.ListToolsAsync();
    ///     return chatClient.AsAIAgent("MyAgent", tools: [.. staticTools, .. mcpTools]);
    /// });
    /// </code>
    /// </remarks>
    public TemporalAgentsOptions AddAIAgentFactory(
        string name,
        Func<IServiceProvider, Task<AIAgent>> asyncFactory,
        TimeSpan? timeToLive = null)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(asyncFactory);

        // Resolve at worker startup (blocking is safe here — DI container is being built).
        return AddAIAgentFactory(name, sp => asyncFactory(sp).GetAwaiter().GetResult(), timeToLive);
    }

    // ── Routing support (GAP 2) ──────────────────────────────────────────────

    /// <summary>
    /// Registers a human-readable description for the named agent, used by
    /// <see cref="IAgentRouter"/> to build routing prompts.
    /// Multiple agents should each have a descriptor for routing to work correctly.
    /// </summary>
    public TemporalAgentsOptions AddAgentDescriptor(string name, string description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        _agentDescriptors[name] = description;
        return this;
    }

    /// <summary>
    /// Sets the <see cref="AIAgent"/> used by <see cref="LlmAgentRouter"/> to classify
    /// incoming messages. When set, <see cref="LlmAgentRouter"/> is automatically registered
    /// in the DI container by <c>AddTemporalAgents()</c>.
    /// </summary>
    /// <remarks>
    /// The router agent should be a lightweight chat agent whose system prompt instructs it
    /// to respond with only the target agent name. Its output is consumed programmatically.
    /// </remarks>
    public TemporalAgentsOptions SetRouterAgent(AIAgent agent)
    {
        ArgumentNullException.ThrowIfNull(agent);
        _routerAgent = agent;
        return this;
    }

    /// <summary>Gets all registered agent factories.</summary>
    internal IReadOnlyDictionary<string, Func<IServiceProvider, AIAgent>> GetAgentFactories() =>
        _agentFactories.AsReadOnly();

    /// <summary>Gets the TTL for a specific agent, falling back to <see cref="DefaultTimeToLive"/>.</summary>
    internal TimeSpan? GetTimeToLive(string agentName) =>
        _agentTimeToLive.TryGetValue(agentName, out TimeSpan? ttl) ? ttl : DefaultTimeToLive;

    /// <summary>Gets all registered agent descriptors for use by <see cref="IAgentRouter"/>.</summary>
    internal IReadOnlyList<AgentDescriptor> GetAgentDescriptors() =>
        _agentDescriptors
            .Select(kv => new AgentDescriptor(kv.Key, kv.Value))
            .ToList();

    /// <summary>Gets the router agent, or <see langword="null"/> if not configured.</summary>
    internal AIAgent? GetRouterAgent() => _routerAgent;
}
