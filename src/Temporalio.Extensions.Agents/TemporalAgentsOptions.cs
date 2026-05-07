using System.Collections.Generic;
using Microsoft.Agents.AI;
using Temporalio.Client.Schedules;
using Temporalio.Common;
using Temporalio.Extensions.AI;
using Temporalio.Extensions.Agents.HistoryStore;
using Temporalio.Extensions.Agents.State;
using Temporalio.Extensions.Agents.Workflows;

namespace Temporalio.Extensions.Agents;

/// <summary>
/// Options for configuring Temporal agents. Agents are registered exclusively via
/// <see cref="AddDurableAgent(string, Action{DurableAgentBuilder})"/>; the v0.2 surface
/// (<c>AddAIAgent</c>, <c>AddAIAgentFactory</c>, etc.) was removed in v0.3.
/// </summary>
public sealed class TemporalAgentsOptions
{
    // Agent names are case-insensitive across the durable-agent and proxy namespaces.
    private readonly Dictionary<string, DurableAgentRegistration> _durableAgentRegistrations =
        new(StringComparer.OrdinalIgnoreCase);

    // Proxy-only declarations (client-side processes). Stores the optional TTL; the proxy is wired
    // by AddTemporalAgentProxies / TemporalAgentsRegistrar.
    private readonly Dictionary<string, TimeSpan?> _proxyDeclarations =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly List<ScheduleAgentRegistration> _scheduledRuns = [];

    internal TemporalAgentsOptions()
    {
    }

    /// <summary>
    /// Gets or sets the worker-level default TTL for agent workflows. Agents that do not set
    /// <see cref="DurableAgentBuilder.TimeToLive"/> inherit this value. Defaults to 14 days.
    /// Set to <see langword="null"/> to disable TTL by default.
    /// </summary>
    public TimeSpan? DefaultTimeToLive { get; set; } = TimeSpan.FromDays(14);

    /// <summary>
    /// Gets or sets the worker-level default activity start-to-close timeout used by the
    /// <c>RunAgentStep</c> activity. Agents inherit this value when
    /// <see cref="DurableAgentBuilder.ActivityTimeout"/> is unset. Defaults to 5 minutes.
    /// </summary>
    public TimeSpan DefaultActivityTimeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Gets or sets the worker-level default heartbeat timeout for agent step activities.
    /// Agents inherit this value when <see cref="DurableAgentBuilder.HeartbeatTimeout"/> is unset.
    /// Defaults to 2 minutes.
    /// </summary>
    public TimeSpan DefaultHeartbeatTimeout { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Gets or sets the worker-level default approval timeout for human-in-the-loop flows.
    /// Agents inherit this value when <see cref="DurableAgentBuilder.ApprovalTimeout"/> is unset.
    /// Defaults to 7 days.
    /// </summary>
    public TimeSpan DefaultApprovalTimeout { get; set; } = TimeSpan.FromDays(7);

    /// <summary>
    /// Gets or sets the worker-level default retry policy applied to the agent's
    /// <c>RunAgentStep</c> activity. Agents inherit this value when
    /// <see cref="DurableAgentBuilder.RetryPolicy"/> is unset. When <see langword="null"/>,
    /// Temporal SDK defaults apply. Per-tool retry policies are configured separately via
    /// <see cref="DurableAgentBuilder.AddTool(Microsoft.Extensions.AI.AIFunction, Action{DurableToolOptions}?)"/>.
    /// </summary>
    public RetryPolicy? DefaultRetryPolicy { get; set; }

    /// <summary>
    /// Default <c>false</c>. Set to <c>true</c> to opt into upserting
    /// AgentName / SessionCreatedAt / TurnCount search attributes on the workflow.
    /// Requires server-side pre-registration of the attribute keys.
    /// </summary>
    public bool EnableSearchAttributes { get; set; }

    /// <summary>
    /// Gets or sets the worker-level default <see cref="IAgentHistoryStore"/> factory. When a
    /// per-agent <c>HistoryStore</c> is unset on the builder, the agent inherits this value.
    /// </summary>
    public Func<IServiceProvider, IAgentHistoryStore>? HistoryStore { get; set; }

    /// <summary>
    /// Gets or sets the worker-level default maximum number of <see cref="DurableSessionEntry"/>
    /// instances retained before triggering continue-as-new. Agents inherit this value when
    /// <see cref="DurableAgentBuilder.MaxEntryCount"/> is unset. Defaults to 1000. Continue-as-new
    /// also fires on Temporal SDK's own
    /// <see cref="Temporalio.Workflows.Workflow.ContinueAsNewSuggested"/> threshold, whichever
    /// comes first.
    /// </summary>
    public int DefaultMaxEntryCount { get; set; } = 1000;

    /// <summary>
    /// Gets or sets the worker-level default deterministic, pure history reducer applied before
    /// continue-as-new. Agents inherit this value when <see cref="DurableAgentBuilder.HistoryReducer"/>
    /// is unset. When <see langword="null"/>, full history is carried forward verbatim.
    /// </summary>
    /// <remarks>
    /// The reducer receives the full history list and returns the list to carry forward. Prefer
    /// LINQ projections over mutating the input list.
    /// <para>
    /// WARNING: This delegate is not serialized. Re-supply it on every StartWorkflowAsync call
    /// (on the same worker, in-memory carry-forward across continue-as-new is fine).
    /// </para>
    /// </remarks>
    public Func<IList<DurableSessionEntry>, IList<DurableSessionEntry>>? DefaultHistoryReducer { get; set; }

    /// <summary>
    /// Registers a durable agent and returns this options instance for chaining. The configure
    /// delegate populates a <see cref="DurableAgentBuilder"/> whose <c>ChatClient</c>, tools, and
    /// context providers are evaluated lazily at first activity dispatch (cached for the lifetime
    /// of the worker process).
    /// </summary>
    /// <param name="name">
    /// Case-insensitive agent name. Must be unique within this options instance.
    /// </param>
    /// <param name="configure">
    /// Builder callback invoked synchronously during this method. Must assign
    /// <see cref="DurableAgentBuilder.ChatClient"/> before returning, otherwise this method throws
    /// <see cref="InvalidOperationException"/>.
    /// </param>
    /// <returns>This options instance, for fluent chaining.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="name"/> is null/empty.</exception>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="configure"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown when <paramref name="name"/> is already registered, or when the configure delegate
    /// completed without assigning <see cref="DurableAgentBuilder.ChatClient"/>.
    /// </exception>
    public TemporalAgentsOptions AddDurableAgent(string name, Action<DurableAgentBuilder> configure)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(configure);

        if (_durableAgentRegistrations.ContainsKey(name) || _proxyDeclarations.ContainsKey(name))
        {
            throw new InvalidOperationException(
                $"An agent with name '{name}' has already been registered. Agent names must be unique " +
                "across AddDurableAgent and AddAgentProxy.");
        }

        var builder = new DurableAgentBuilder(name);
        configure(builder);

        if (builder.ChatClient is null)
        {
            throw new InvalidOperationException(
                $"DurableAgent '{name}' requires ChatClient. Set agent.ChatClient = sp => sp.GetRequiredService<IChatClient>() in the configure delegate.");
        }

        var registration = builder.ToRegistration();
        _durableAgentRegistrations.Add(name, registration);

        return this;
    }

    /// <summary>
    /// Declares a named agent proxy for client-only scenarios where the real agent
    /// implementation runs in a separate worker process. No factory is required; call this from
    /// <see cref="ServiceCollectionExtensions.AddTemporalAgentProxies"/> instead of
    /// <see cref="AddDurableAgent(string, Action{DurableAgentBuilder})"/>.
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

        if (_durableAgentRegistrations.ContainsKey(name) || _proxyDeclarations.ContainsKey(name))
        {
            throw new ArgumentException(
                $"An agent with name '{name}' has already been registered.", nameof(name));
        }

        _proxyDeclarations.Add(name, timeToLive);
        return this;
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
    /// Returns the names of all registered agents (durable and proxy), in registration order.
    /// </summary>
    public IReadOnlyList<string> GetRegisteredAgentNames()
    {
        var names = new List<string>(_durableAgentRegistrations.Count + _proxyDeclarations.Count);
        names.AddRange(_durableAgentRegistrations.Keys);
        names.AddRange(_proxyDeclarations.Keys);
        return names;
    }

    /// <summary>
    /// Returns <see langword="true"/> if an agent with the given name is registered.
    /// The check is case-insensitive.
    /// </summary>
    public bool IsAgentRegistered(string name) =>
        !string.IsNullOrEmpty(name)
        && (_durableAgentRegistrations.ContainsKey(name) || _proxyDeclarations.ContainsKey(name));

    /// <summary>
    /// Returns descriptors for all registered durable agents that have a description.
    /// Use this in routing activities to build an LLM dispatch prompt.
    /// </summary>
    public IReadOnlyList<AgentDescriptor> GetAgentDescriptors() =>
        [.. _durableAgentRegistrations
            .Where(kvp => !string.IsNullOrWhiteSpace(kvp.Value.Description))
            .Select(kvp => new AgentDescriptor(kvp.Key, kvp.Value.Description!))];

    /// <summary>
    /// Returns the description for the given agent, or <see langword="null"/> if the agent
    /// has no description or is not registered. The lookup is case-insensitive.
    /// </summary>
    public string? GetAgentDescription(string agentName)
    {
        if (string.IsNullOrEmpty(agentName))
        {
            return null;
        }

        return _durableAgentRegistrations.TryGetValue(agentName, out var reg)
            ? reg.Description
            : null;
    }

    /// <summary>
    /// Gets the durable-agent registrations. Empty when no <see cref="AddDurableAgent"/> calls
    /// have been made.
    /// </summary>
    internal IReadOnlyDictionary<string, DurableAgentRegistration> DurableAgentRegistrations =>
        _durableAgentRegistrations;

    /// <summary>Gets the proxy-only declarations.</summary>
    internal IReadOnlyDictionary<string, TimeSpan?> ProxyDeclarations => _proxyDeclarations;

    /// <summary>
    /// Gets the resolved TTL for a specific agent. Per-agent value (durable registration or proxy
    /// declaration) wins; otherwise falls back to <see cref="DefaultTimeToLive"/>.
    /// </summary>
    internal TimeSpan? GetTimeToLive(string agentName)
    {
        if (_durableAgentRegistrations.TryGetValue(agentName, out var reg) && reg.TimeToLive.HasValue)
        {
            return reg.TimeToLive;
        }

        if (_proxyDeclarations.TryGetValue(agentName, out var proxyTtl) && proxyTtl.HasValue)
        {
            return proxyTtl;
        }

        return DefaultTimeToLive;
    }
}
