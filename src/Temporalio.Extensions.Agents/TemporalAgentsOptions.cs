using System.Collections.Generic;
using Microsoft.Agents.AI;
using Temporalio.Client.Schedules;
using Temporalio.Common;
using Temporalio.Extensions.AI;
using Temporalio.Extensions.Agents.State;
using Temporalio.Extensions.Agents.Workflows;

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

    private readonly Dictionary<string, string> _agentDescriptions =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly List<ScheduleAgentRegistration> _scheduledRuns = [];

    internal TemporalAgentsOptions()
    {
    }

    /// <summary>
    /// Gets or sets the default TTL for agent workflows. Defaults to 14 days.
    /// Set to <see langword="null"/> to disable TTL for agents without explicit TTL configuration.
    /// </summary>
    public TimeSpan? DefaultTimeToLive { get; set; } = TimeSpan.FromDays(14);

    /// <summary>
    /// Gets or sets the activity timeout applied to every
    /// <see cref="AgentActivities.ExecuteAgentAsync"/> activity invocation.
    /// This bounds the total wall-clock time allowed for one agent turn, including
    /// any tool calls and retries within that turn. Defaults to 5 minutes.
    /// </summary>
    public TimeSpan ActivityTimeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Gets or sets the heartbeat timeout for agent activity invocations.
    /// If the activity does not send a heartbeat within this interval Temporal
    /// considers it lost and schedules a retry. Relevant when streaming is enabled
    /// because the activity heartbeats on each streamed chunk. Defaults to 2 minutes.
    /// </summary>
    public TimeSpan HeartbeatTimeout { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Gets or sets the maximum time the workflow will wait for a human to respond
    /// to an approval request before timing out. Defaults to 7 days.
    /// When the timeout elapses, <see cref="AgentWorkflow.RequestApprovalAsync"/>
    /// returns a rejected <see cref="DurableApprovalDecision"/> with a timeout comment.
    /// </summary>
    public TimeSpan ApprovalTimeout { get; set; } = TimeSpan.FromDays(7);

    /// <summary>
    /// Activity retry policy applied at every agent activity dispatch.
    /// When null, Temporal SDK defaults apply (unbounded retries).
    /// </summary>
    public RetryPolicy? RetryPolicy { get; set; }

    /// <summary>
    /// Default <c>false</c>. Set to <c>true</c> to opt into upserting
    /// AgentName / SessionCreatedAt / TurnCount search attributes on the workflow.
    /// Requires server-side pre-registration of the attribute keys.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Breaking change in this release</b>: previous versions upserted these attributes
    /// unconditionally. To restore prior behavior, set <c>EnableSearchAttributes = true</c>
    /// on your <c>AddTemporalAgents</c> call.
    /// </para>
    /// </remarks>
    public bool EnableSearchAttributes { get; set; }

    /// <summary>
    /// Maximum number of history entries before triggering continue-as-new.
    /// Default 1000. Continue-as-new also fires on Temporal SDK's own
    /// <see cref="Temporalio.Workflows.Workflow.ContinueAsNewSuggested"/> threshold, whichever comes first.
    /// </summary>
    public int MaxEntryCount { get; set; } = 1000;

    /// <summary>
    /// Optional pure-function reducer applied to the carried history before continue-as-new.
    /// Runs in workflow context — must be deterministic.
    /// When null, full history is carried forward verbatim.
    /// </summary>
    /// <remarks>
    /// The reducer receives the full history list and returns the list to carry forward.
    /// Prefer LINQ projections over mutating the input list. The library may add fields
    /// to <see cref="DurableSessionEntry"/> (or its subclasses) in future versions — design
    /// reducers to be tolerant of unknown subtypes.
    /// <para>
    /// WARNING: This delegate is not serialized. Re-supply it on every StartWorkflowAsync call
    /// (on the same worker, in-memory carry-forward across continue-as-new is fine).
    /// </para>
    /// </remarks>
    public Func<IList<DurableSessionEntry>, IList<DurableSessionEntry>>? HistoryReducer { get; set; }

    /// <summary>
    /// When <see langword="true"/>, agent conversation history is loaded and appended via a
    /// registered <see cref="HistoryStore.IAgentHistoryStore"/> instead of being carried
    /// inside the Temporal <c>ActivityScheduled</c> event. Defaults to <see langword="false"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Setting this to <see langword="true"/> requires an <see cref="HistoryStore.IAgentHistoryStore"/>
    /// implementation to be registered in DI. Worker startup throws
    /// <see cref="InvalidOperationException"/> if no store is registered.
    /// </para>
    /// <para>
    /// Migration: workflows started while this setting was <see langword="false"/> continue
    /// using in-memory history until they complete or hit continue-as-new. The
    /// <c>UseExternalStore</c> flag travels with the running workflow input.
    /// </para>
    /// </remarks>
    public bool UseExternalHistory { get; set; }

    /// <summary>Adds an agent factory with an optional per-agent TTL.</summary>
    /// <param name="name">
    /// Case-insensitive agent name. Must be unique across registrations.
    /// </param>
    /// <param name="factory">
    /// Factory invoked once at activity dispatch time to obtain the <see cref="AIAgent"/> instance.
    /// Receives the activity's <see cref="IServiceProvider"/>.
    /// </param>
    /// <param name="timeToLive">
    /// Per-agent session TTL. When null, falls back to <see cref="DefaultTimeToLive"/>
    /// (14 days by default).
    /// </param>
    /// <param name="description">
    /// Optional human-readable description of what the agent does. When provided, the agent
    /// appears in <see cref="GetAgentDescriptors"/> for use in routing prompts.
    /// </param>
    /// <returns>This options instance, for fluent chaining.</returns>
    public TemporalAgentsOptions AddAIAgentFactory(string name, Func<IServiceProvider, AIAgent> factory, TimeSpan? timeToLive = null, string? description = null)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(factory);
        if (_agentFactories.ContainsKey(name))
            throw new ArgumentException($"An agent factory with name '{name}' has already been registered.", nameof(name));
        _agentFactories.Add(name, factory);
        if (timeToLive.HasValue)
        {
            _agentTimeToLive[name] = timeToLive;
        }

        if (!string.IsNullOrWhiteSpace(description))
        {
            _agentDescriptions[name] = description;
        }

        return this;
    }

    /// <summary>Adds multiple agents at once.</summary>
    /// <param name="agents">
    /// One or more <see cref="AIAgent"/> instances to register. Each agent must have a
    /// non-null, non-whitespace <see cref="AIAgent.Name"/> and must not duplicate an
    /// already-registered name.
    /// </param>
    /// <returns>This options instance, for fluent chaining.</returns>
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
    /// <param name="agent">
    /// The <see cref="AIAgent"/> instance to register. Its <see cref="AIAgent.Name"/> must be
    /// non-null, non-whitespace, and unique within this options instance.
    /// </param>
    /// <param name="timeToLive">
    /// Per-agent session TTL. When null, falls back to <see cref="DefaultTimeToLive"/>
    /// (14 days by default).
    /// </param>
    /// <returns>This options instance, for fluent chaining.</returns>
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

        // Auto-extract the description from the agent if one is set.
        if (!string.IsNullOrWhiteSpace(agent.Description))
        {
            _agentDescriptions[agent.Name] = agent.Description;
        }

        return this;
    }

    /// <summary>
    /// Declares a named agent proxy for client-only scenarios where the real agent
    /// implementation runs in a separate worker process.
    /// No factory is required; call this from <see cref="ServiceCollectionExtensions.AddTemporalAgentProxies"/>
    /// instead of <see cref="AddAIAgent"/> or <see cref="AddAIAgentFactory"/>.
    /// </summary>
    /// <param name="name">
    /// Case-insensitive agent name that must match the name used by the remote worker.
    /// Must be unique within this options instance.
    /// </param>
    /// <param name="timeToLive">
    /// Per-agent session TTL used when the proxy starts a new workflow. When null,
    /// <see cref="DefaultTimeToLive"/> is used.
    /// </param>
    /// <returns>This options instance, for fluent chaining.</returns>
    public TemporalAgentsOptions AddAgentProxy(string name, TimeSpan? timeToLive = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        if (_agentFactories.ContainsKey(name))
        {
            throw new ArgumentException($"An agent with name '{name}' has already been registered.", nameof(name));
        }

        // Guard factory — if somehow invoked from a worker context it fails fast with a clear message.
        // Note: this throw is defense-in-depth. The keyed AIAgent proxy registered for proxy-only
        // entries (see TemporalWorkerBuilderExtensions) is fully functional in worker processes —
        // it routes RunAsync calls through Temporal updates, never invoking this factory locally.
        // The factory only fires if AgentActivities.ExecuteAgentAsync attempts to execute a
        // proxy-only agent name on the worker, which is the failure mode we want to surface clearly.
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
    /// <param name="name">
    /// Case-insensitive agent name. Must be unique across registrations.
    /// </param>
    /// <param name="asyncFactory">
    /// Async factory invoked once at activity dispatch time. The result is awaited
    /// synchronously via <c>GetAwaiter().GetResult()</c> — safe at startup but not on the
    /// workflow thread. Receives the activity's <see cref="IServiceProvider"/>.
    /// </param>
    /// <param name="timeToLive">
    /// Per-agent session TTL. When null, falls back to <see cref="DefaultTimeToLive"/>
    /// (14 days by default).
    /// </param>
    /// <param name="description">
    /// Optional human-readable description of what the agent does. When provided, the agent
    /// appears in <see cref="GetAgentDescriptors"/> for use in routing prompts.
    /// </param>
    /// <returns>This options instance, for fluent chaining.</returns>
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
        TimeSpan? timeToLive = null,
        string? description = null)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(asyncFactory);

        // Resolve at worker startup (blocking is safe here — DI container is being built).
        return AddAIAgentFactory(name, sp => asyncFactory(sp).GetAwaiter().GetResult(), timeToLive, description);
    }

    /// <summary>
    /// Registers a scheduled agent run that is created with Temporal at worker startup.
    /// </summary>
    /// <param name="agentName">Name of the agent to invoke on each schedule tick.</param>
    /// <param name="scheduleId">
    /// Unique schedule identifier. If a schedule with this ID already exists on startup,
    /// a warning is logged and the existing schedule is left unchanged.
    /// </param>
    /// <param name="request">The request to send to the agent on each scheduled run.</param>
    /// <param name="spec">When and how often the schedule fires.</param>
    /// <param name="policy">Overlap and catchup policy. Defaults to <see cref="SchedulePolicy"/> defaults.</param>
    /// <remarks>
    /// <b>Config drift:</b> changing <paramref name="spec"/> in code does not update an existing
    /// schedule on restart — the already-exists warning is logged and the old spec remains active.
    /// To apply an updated spec, delete the schedule first via
    /// <see cref="ITemporalAgentClient.GetAgentScheduleHandle"/> and then restart the worker.
    /// </remarks>
    public TemporalAgentsOptions AddScheduledAgentRun(
        string agentName,
        string scheduleId,
        RunRequest request,
        ScheduleSpec spec,
        SchedulePolicy? policy = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentName);
        ArgumentException.ThrowIfNullOrWhiteSpace(scheduleId);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(spec);

        _scheduledRuns.Add(new ScheduleAgentRegistration(agentName, scheduleId, request, spec, policy));
        return this;
    }

    /// <summary>Gets all registered scheduled runs for use by <see cref="ScheduleRegistrationService"/>.</summary>
    internal IReadOnlyList<ScheduleAgentRegistration> GetScheduledRuns() => _scheduledRuns;

    // ── Agent Registry (read-only introspection) ──────────────────────────

    /// <summary>
    /// Returns the names of all registered agents (case-preserving, in registration order).
    /// Useful for health-check endpoints, admin dashboards, and startup validation.
    /// </summary>
    /// <returns>
    /// A snapshot of the registered agent names at the time of the call. The list is
    /// independent of the internal registry — mutations to it do not affect registrations.
    /// </returns>
    public IReadOnlyList<string> GetRegisteredAgentNames() =>
        [.. _agentFactories.Keys];

    /// <summary>
    /// Returns <see langword="true"/> if an agent with the given name is registered.
    /// The check is case-insensitive.
    /// </summary>
    /// <param name="name">
    /// The agent name to look up. Null or empty returns <see langword="false"/> without throwing.
    /// </param>
    /// <returns>
    /// <see langword="true"/> if <paramref name="name"/> matches a registered agent name
    /// (case-insensitive); <see langword="false"/> otherwise.
    /// </returns>
    public bool IsAgentRegistered(string name) =>
        !string.IsNullOrEmpty(name) && _agentFactories.ContainsKey(name);

    /// <summary>
    /// Returns descriptors for all registered agents that have a description.
    /// Agents registered without a description (e.g. classifier agents that are not routable
    /// specialists) are excluded. Use this in routing activities to build an LLM dispatch prompt.
    /// </summary>
    /// <returns>
    /// A snapshot of <see cref="AgentDescriptor"/> entries for agents with descriptions,
    /// in registration order. The list is independent of the internal registry.
    /// </returns>
    public IReadOnlyList<AgentDescriptor> GetAgentDescriptors() =>
        [.. _agentDescriptions.Select(kvp => new AgentDescriptor(kvp.Key, kvp.Value))];

    /// <summary>
    /// Returns the description for the given agent, or <see langword="null"/> if the agent
    /// has no description or is not registered. The lookup is case-insensitive.
    /// </summary>
    /// <param name="agentName">
    /// The agent name to look up. Null or empty returns <see langword="null"/> without throwing.
    /// </param>
    public string? GetAgentDescription(string agentName) =>
        string.IsNullOrEmpty(agentName) ? null : _agentDescriptions.GetValueOrDefault(agentName);

    /// <summary>Gets all registered agent factories.</summary>
    internal IReadOnlyDictionary<string, Func<IServiceProvider, AIAgent>> GetAgentFactories() =>
        _agentFactories.AsReadOnly();

    /// <summary>Gets the TTL for a specific agent, falling back to <see cref="DefaultTimeToLive"/>.</summary>
    internal TimeSpan? GetTimeToLive(string agentName) =>
        _agentTimeToLive.GetValueOrDefault(agentName, DefaultTimeToLive);
}
