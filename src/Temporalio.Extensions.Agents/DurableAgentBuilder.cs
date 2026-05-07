using System.Collections.Generic;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Temporalio.Common;
using Temporalio.Extensions.AI;
using Temporalio.Extensions.Agents.HistoryStore;

namespace Temporalio.Extensions.Agents;

/// <summary>
/// Internal carrier for a tool registered on a <see cref="DurableAgentBuilder"/>. The factory is
/// invoked at first activity dispatch (the same lifecycle as <see cref="DurableAgentBuilder.ChatClient"/>);
/// the resolved <see cref="AIFunction"/> is cached for the lifetime of the worker.
/// </summary>
internal sealed record DurableToolRegistration(
    string Name,
    Func<IServiceProvider, AIFunction> Factory,
    DurableToolOptions Options);

/// <summary>
/// Fluent builder for registering a durable agent via <c>TemporalAgentsOptions.AddDurableAgent</c>.
/// Properties capture per-agent scalars; <see cref="AddTool(AIFunction, Action{DurableToolOptions}?)"/> and
/// <see cref="AddContextProvider(AIContextProvider)"/> capture per-agent collections.
/// </summary>
/// <remarks>
/// <para>
/// Per-agent scalar settings (timeouts, retry policy, max entry count, etc.) default to <see langword="null"/>
/// and inherit the corresponding worker-level value from <see cref="TemporalAgentsOptions"/> when unset.
/// <see cref="MaxToolCallsPerTurn"/> is the only per-agent setting with a built-in default
/// (<c>20</c>) — there is no worker-level fallback for it.
/// </para>
/// <para>
/// All <c>Add*</c> methods return the builder so configuration can be expressed fluently; using the
/// property setters directly is also fully supported.
/// </para>
/// </remarks>
public sealed class DurableAgentBuilder
{
    // Tools are stored in registration order; names are case-insensitive (consistent with
    // TemporalAgentsOptions agent-name handling).
    private readonly List<DurableToolRegistration> _tools = new();
    private readonly HashSet<string> _toolNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Func<IServiceProvider, AIContextProvider>> _contextProviders = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="DurableAgentBuilder"/> class with the given agent
    /// name. This constructor is internal — instances are produced by
    /// <c>TemporalAgentsOptions.AddDurableAgent</c>.
    /// </summary>
    /// <param name="name">The case-insensitive agent name. Must be non-null and non-whitespace.</param>
    internal DurableAgentBuilder(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
    }

    /// <summary>
    /// Gets the agent name. Immutable for the life of the builder.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Gets or sets a human-readable description of the agent. When set, the agent appears in
    /// <c>TemporalAgentsOptions.GetAgentDescriptors()</c> for use in routing prompts.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// Gets or sets the agent's system instructions. When set, the library stamps these onto every
    /// LLM call's <see cref="ChatOptions.Instructions"/> regardless of what is set on
    /// <see cref="ChatOptions"/>.
    /// </summary>
    /// <remarks>
    /// Optional. Tool-only agents (no system prompt) are supported by leaving this <see langword="null"/>.
    /// </remarks>
    public string? Instructions { get; set; }

    /// <summary>
    /// Gets or sets the factory used to obtain the agent's <see cref="IChatClient"/>. The factory is
    /// invoked once at first activity dispatch and the result is cached for the lifetime of the
    /// worker process.
    /// </summary>
    /// <remarks>
    /// Required at composition time. Registration with a <see langword="null"/> chat client throws
    /// at the end of the configure delegate.
    /// </remarks>
    public Func<IServiceProvider, IChatClient>? ChatClient { get; set; }

    /// <summary>
    /// Gets or sets a template <see cref="ChatOptions"/> instance applied to every LLM call. Use
    /// for LLM-call settings such as <see cref="ChatOptions.Temperature"/>,
    /// <see cref="ChatOptions.ResponseFormat"/>, <see cref="ChatOptions.MaxOutputTokens"/>, etc.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="ChatOptions.Tools"/> and <see cref="ChatOptions.Instructions"/> set on this
    /// property are ignored. The agent's tools come from <see cref="AddTool(AIFunction, Action{DurableToolOptions}?)"/>
    /// calls; the agent's instructions come from the <see cref="Instructions"/> property. Use
    /// <see cref="ChatOptions"/> for LLM-call settings only (Temperature, ResponseFormat,
    /// MaxOutputTokens, etc.).
    /// </para>
    /// </remarks>
    public ChatOptions? ChatOptions { get; set; }

    /// <summary>
    /// Gets or sets the per-agent session TTL. When <see langword="null"/>, inherits the worker-level
    /// <c>TemporalAgentsOptions.DefaultTimeToLive</c>.
    /// </summary>
    public TimeSpan? TimeToLive { get; set; }

    /// <summary>
    /// Gets or sets the per-agent maximum time the workflow waits for a human approval response.
    /// When <see langword="null"/>, inherits the worker-level <c>TemporalAgentsOptions.DefaultApprovalTimeout</c>.
    /// </summary>
    public TimeSpan? ApprovalTimeout { get; set; }

    /// <summary>
    /// Gets or sets the per-agent activity start-to-close timeout used for the
    /// <c>RunAgentStep</c> activity. When <see langword="null"/>, inherits the worker-level
    /// <c>TemporalAgentsOptions.DefaultActivityTimeout</c>.
    /// </summary>
    public TimeSpan? ActivityTimeout { get; set; }

    /// <summary>
    /// Gets or sets the per-agent activity heartbeat timeout used for the <c>RunAgentStep</c>
    /// activity. When <see langword="null"/>, inherits the worker-level
    /// <c>TemporalAgentsOptions.DefaultHeartbeatTimeout</c>.
    /// </summary>
    public TimeSpan? HeartbeatTimeout { get; set; }

    /// <summary>
    /// Gets or sets the retry policy applied to this agent's <c>RunAgentStep</c> activity (the LLM
    /// call). Per-tool retry policies are configured via
    /// <see cref="AddTool(AIFunction, Action{DurableToolOptions}?)"/>.
    /// When <see langword="null"/>, inherits the worker-level <c>TemporalAgentsOptions.DefaultRetryPolicy</c>.
    /// </summary>
    /// <remarks>
    /// This policy applies to the LLM step only — it does not cascade to per-tool activity dispatches.
    /// Configure tool retries individually via <see cref="DurableToolOptions"/> (typically
    /// <see cref="DurableToolOptions.NoRetry"/> for non-idempotent write tools).
    /// </remarks>
    public RetryPolicy? RetryPolicy { get; set; }

    /// <summary>
    /// Gets or sets the per-agent maximum number of <see cref="DurableSessionEntry"/> instances
    /// retained before triggering continue-as-new. When <see langword="null"/>, inherits the
    /// worker-level <c>TemporalAgentsOptions.DefaultMaxEntryCount</c>.
    /// </summary>
    public int? MaxEntryCount { get; set; }

    /// <summary>
    /// Gets or sets the maximum number of LLM-step iterations within a single agent turn. Each
    /// iteration may dispatch a parallel batch of tool activities. When the cap is exceeded the
    /// workflow returns a structured error response. Defaults to <c>20</c>.
    /// </summary>
    /// <remarks>
    /// There is no worker-level fallback — every agent uses the value set on its builder (or the
    /// default <c>20</c>).
    /// </remarks>
    public int MaxToolCallsPerTurn { get; set; } = 20;

    /// <summary>
    /// Gets or sets a deterministic, pure reducer applied to the agent's accumulated history before
    /// continue-as-new. When <see langword="null"/>, the full history is carried forward verbatim.
    /// </summary>
    public Func<IList<DurableSessionEntry>, IList<DurableSessionEntry>>? HistoryReducer { get; set; }

    /// <summary>
    /// Gets or sets a per-agent <see cref="IAgentHistoryStore"/> factory. When <see langword="null"/>,
    /// inherits the worker-level <c>TemporalAgentsOptions.HistoryStore</c> (which itself may be
    /// <see langword="null"/>, meaning no external history is used). The factory is invoked once at
    /// first activity dispatch.
    /// </summary>
    /// <remarks>
    /// There is no per-agent explicit-disable mechanism. Users who want one agent on a worker to
    /// opt out of an externally configured store should split that agent into a separate worker
    /// registration.
    /// </remarks>
    public Func<IServiceProvider, IAgentHistoryStore>? HistoryStore { get; set; }

    /// <summary>
    /// Registers a concrete <see cref="AIFunction"/> as a tool for this agent. The tool's
    /// <see cref="AIFunction.Name"/> must be unique within this agent.
    /// </summary>
    /// <param name="tool">The tool instance.</param>
    /// <param name="configure">Optional configuration callback for per-tool activity overrides.</param>
    /// <returns>This builder, for fluent chaining.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="tool"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="tool"/> has a null/empty <see cref="AIFunction.Name"/>, or when a
    /// tool with the same name has already been registered on this agent.
    /// </exception>
    public DurableAgentBuilder AddTool(AIFunction tool, Action<DurableToolOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(tool);
        if (string.IsNullOrEmpty(tool.Name))
        {
            throw new ArgumentException(
                "Tool must have a non-empty Name.",
                nameof(tool));
        }

        AddToolCore(tool.Name, _ => tool, configure);
        return this;
    }

    /// <summary>
    /// Registers a tool produced by a factory. The factory is invoked at first activity dispatch
    /// (the same lifecycle as <see cref="ChatClient"/>) and the resolved <see cref="AIFunction"/>
    /// is cached for the worker's lifetime.
    /// </summary>
    /// <param name="name">
    /// The tool name. Must be non-null and non-whitespace, and unique within this agent. Required as
    /// an explicit parameter so duplicate-name detection happens synchronously at registration —
    /// without it, the duplicate check would be deferred to first dispatch when the factory runs.
    /// </param>
    /// <param name="factory">Factory that produces the <see cref="AIFunction"/>.</param>
    /// <param name="configure">Optional configuration callback for per-tool activity overrides.</param>
    /// <returns>This builder, for fluent chaining.</returns>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="name"/> is null/empty, or when a tool with the same name has
    /// already been registered on this agent.
    /// </exception>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="factory"/> is <see langword="null"/>.</exception>
    public DurableAgentBuilder AddTool(string name, Func<IServiceProvider, AIFunction> factory, Action<DurableToolOptions>? configure = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(factory);

        AddToolCore(name, factory, configure);
        return this;
    }

    /// <summary>
    /// Registers multiple concrete tools at once. Equivalent to calling
    /// <see cref="AddTool(AIFunction, Action{DurableToolOptions}?)"/> for each entry, in order.
    /// </summary>
    /// <param name="tools">One or more <see cref="AIFunction"/> instances to register.</param>
    /// <returns>This builder, for fluent chaining.</returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="tools"/> is <see langword="null"/> or contains a <see langword="null"/> entry.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Thrown when any entry has a null/empty <see cref="AIFunction.Name"/>, or duplicates a name
    /// already registered on this agent.
    /// </exception>
    public DurableAgentBuilder AddTools(params AIFunction[] tools)
    {
        ArgumentNullException.ThrowIfNull(tools);
        foreach (var tool in tools)
        {
            AddTool(tool);
        }

        return this;
    }

    /// <summary>
    /// Registers a concrete <see cref="AIContextProvider"/> for this agent.
    /// </summary>
    /// <param name="provider">The provider instance.</param>
    /// <returns>This builder, for fluent chaining.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="provider"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// In durable agents, the provider's <c>InvokingAsync</c> and <c>InvokedAsync</c> hooks fire
    /// once per LLM call (per <c>RunAgentStep</c> activity), not once per turn. Make these hooks
    /// idempotent and cheap, or cache results via <c>StateBag</c> to skip redundant work within a
    /// turn. The provider instance is constructed once per agent per worker process and shared
    /// across all sessions on that worker — treat fields as effectively read-only after
    /// construction; per-session state must live in the <c>StateBag</c>.
    /// </remarks>
    public DurableAgentBuilder AddContextProvider(AIContextProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        _contextProviders.Add(_ => provider);
        return this;
    }

    /// <summary>
    /// Registers an <see cref="AIContextProvider"/> via a factory. The factory is invoked once at
    /// first activity dispatch (the same lifecycle as <see cref="ChatClient"/>) and the resolved
    /// instance is cached for the worker's lifetime.
    /// </summary>
    /// <param name="factory">Factory that produces the <see cref="AIContextProvider"/>.</param>
    /// <returns>This builder, for fluent chaining.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="factory"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// In durable agents, the provider's <c>InvokingAsync</c> and <c>InvokedAsync</c> hooks fire
    /// once per LLM call (per <c>RunAgentStep</c> activity), not once per turn. Make these hooks
    /// idempotent and cheap, or cache results via <c>StateBag</c> to skip redundant work within a
    /// turn. The provider instance is constructed once per agent per worker process and shared
    /// across all sessions on that worker — treat fields as effectively read-only after
    /// construction; per-session state must live in the <c>StateBag</c>.
    /// </remarks>
    public DurableAgentBuilder AddContextProvider(Func<IServiceProvider, AIContextProvider> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _contextProviders.Add(factory);
        return this;
    }

    /// <summary>Internal accessor for Phase 2 registration plumbing.</summary>
    internal IReadOnlyList<DurableToolRegistration> ToolRegistrations => _tools;

    /// <summary>Internal accessor for Phase 2 registration plumbing.</summary>
    internal IReadOnlyList<Func<IServiceProvider, AIContextProvider>> ContextProviderFactories => _contextProviders;

    /// <summary>
    /// Produces an immutable <see cref="DurableAgentRegistration"/> snapshot of this builder. Called
    /// by <c>TemporalAgentsOptions.AddDurableAgent</c> after the configure delegate completes.
    /// </summary>
    /// <returns>The flattened registration record.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when <see cref="ChatClient"/> is <see langword="null"/>.
    /// </exception>
    internal DurableAgentRegistration ToRegistration()
    {
        if (ChatClient is null)
        {
            throw new InvalidOperationException(
                $"DurableAgentBuilder for agent '{Name}' has no ChatClient set. Assign agent.ChatClient = sp => ... in the configure delegate.");
        }

        return new DurableAgentRegistration(
            Name: Name,
            Description: Description,
            Instructions: Instructions,
            ChatClient: ChatClient,
            ChatOptions: ChatOptions,
            Tools: _tools.ToArray(),
            ContextProviderFactories: _contextProviders.ToArray(),
            HistoryStore: HistoryStore,
            TimeToLive: TimeToLive,
            ApprovalTimeout: ApprovalTimeout,
            ActivityTimeout: ActivityTimeout,
            HeartbeatTimeout: HeartbeatTimeout,
            RetryPolicy: RetryPolicy,
            MaxEntryCount: MaxEntryCount,
            MaxToolCallsPerTurn: MaxToolCallsPerTurn,
            HistoryReducer: HistoryReducer);
    }

    private void AddToolCore(string name, Func<IServiceProvider, AIFunction> factory, Action<DurableToolOptions>? configure)
    {
        if (!_toolNames.Add(name))
        {
            throw new ArgumentException(
                $"Tool '{name}' is already registered on agent '{Name}'.",
                nameof(name));
        }

        var options = new DurableToolOptions();
        configure?.Invoke(options);
        _tools.Add(new DurableToolRegistration(name, factory, options));
    }
}
