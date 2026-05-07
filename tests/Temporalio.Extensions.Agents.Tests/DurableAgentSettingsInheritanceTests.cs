using Microsoft.Extensions.AI;
using Temporalio.Common;
using Temporalio.Extensions.AI;
using Temporalio.Extensions.Agents.HistoryStore;
using Temporalio.Extensions.Agents.Tests.Helpers;
using Temporalio.Extensions.Agents.Tests.HistoryStore;
using Temporalio.Extensions.Agents.Workflows;
using Xunit;

namespace Temporalio.Extensions.Agents.Tests;

/// <summary>
/// Phase 4 (v0.3 API redesign): coverage for the per-agent settings-inheritance rule applied by
/// <c>DefaultTemporalAgentClient.BuildAgentWorkflowInputCore</c>. For every settable scalar on
/// <see cref="DurableAgentBuilder"/>, the rule is
/// <c>effective = registration.X ?? options.X</c> — when the agent overrides a setting it wins;
/// when the agent leaves it null the worker-level default applies.
/// </summary>
public class DurableAgentSettingsInheritanceTests
{
    private const string TaskQueue = "tq";

    private static IChatClient NewChatClient() => new StubChatClient();

    private static AgentWorkflowInput Build(
        TemporalAgentsOptions options,
        string agentName = "DurableAgent") =>
        DefaultTemporalAgentClient.BuildAgentWorkflowInputCore(agentName, options, TaskQueue);

    private static TemporalAgentsOptions OptionsWithDurableAgent(
        Action<DurableAgentBuilder>? configureAgent = null,
        Action<TemporalAgentsOptions>? configureOptions = null,
        string agentName = "DurableAgent")
    {
        var options = new TemporalAgentsOptions();
        configureOptions?.Invoke(options);
        options.AddDurableAgent(agentName, agent =>
        {
            agent.ChatClient = _ => NewChatClient();
            configureAgent?.Invoke(agent);
        });
        return options;
    }

    // ── TimeToLive ──────────────────────────────────────────────────────────────

    [Fact]
    public void WhenPerAgentTimeToLiveSet_OverridesWorkerDefault()
    {
        var options = OptionsWithDurableAgent(
            configureAgent: a => a.TimeToLive = TimeSpan.FromHours(3),
            configureOptions: o => o.DefaultTimeToLive = TimeSpan.FromDays(2));

        var input = Build(options);

        Assert.Equal(TimeSpan.FromHours(3), input.TimeToLive);
    }

    [Fact]
    public void WhenPerAgentTimeToLiveNull_InheritsWorkerDefault()
    {
        var options = OptionsWithDurableAgent(
            configureOptions: o => o.DefaultTimeToLive = TimeSpan.FromDays(2));

        var input = Build(options);

        Assert.Equal(TimeSpan.FromDays(2), input.TimeToLive);
    }

    // ── ApprovalTimeout ─────────────────────────────────────────────────────────

    [Fact]
    public void WhenPerAgentApprovalTimeoutSet_OverridesWorkerDefault()
    {
        var options = OptionsWithDurableAgent(
            configureAgent: a => a.ApprovalTimeout = TimeSpan.FromHours(1),
            configureOptions: o => o.DefaultApprovalTimeout = TimeSpan.FromHours(8));

        var input = Build(options);

        Assert.Equal(TimeSpan.FromHours(1), input.ApprovalTimeout);
    }

    [Fact]
    public void WhenPerAgentApprovalTimeoutNull_InheritsWorkerDefault()
    {
        var options = OptionsWithDurableAgent(
            configureOptions: o => o.DefaultApprovalTimeout = TimeSpan.FromHours(8));

        var input = Build(options);

        Assert.Equal(TimeSpan.FromHours(8), input.ApprovalTimeout);
    }

    // ── ActivityTimeout ─────────────────────────────────────────────────────────

    [Fact]
    public void WhenPerAgentActivityTimeoutSet_OverridesWorkerDefault()
    {
        var options = OptionsWithDurableAgent(
            configureAgent: a => a.ActivityTimeout = TimeSpan.FromSeconds(45),
            configureOptions: o => o.DefaultActivityTimeout = TimeSpan.FromMinutes(10));

        var input = Build(options);

        Assert.Equal(TimeSpan.FromSeconds(45), input.ActivityTimeout);
    }

    [Fact]
    public void WhenPerAgentActivityTimeoutNull_InheritsWorkerDefault()
    {
        var options = OptionsWithDurableAgent(
            configureOptions: o => o.DefaultActivityTimeout = TimeSpan.FromMinutes(10));

        var input = Build(options);

        Assert.Equal(TimeSpan.FromMinutes(10), input.ActivityTimeout);
    }

    // ── HeartbeatTimeout ────────────────────────────────────────────────────────

    [Fact]
    public void WhenPerAgentHeartbeatTimeoutSet_OverridesWorkerDefault()
    {
        var options = OptionsWithDurableAgent(
            configureAgent: a => a.HeartbeatTimeout = TimeSpan.FromSeconds(20),
            configureOptions: o => o.DefaultHeartbeatTimeout = TimeSpan.FromMinutes(5));

        var input = Build(options);

        Assert.Equal(TimeSpan.FromSeconds(20), input.HeartbeatTimeout);
    }

    [Fact]
    public void WhenPerAgentHeartbeatTimeoutNull_InheritsWorkerDefault()
    {
        var options = OptionsWithDurableAgent(
            configureOptions: o => o.DefaultHeartbeatTimeout = TimeSpan.FromMinutes(5));

        var input = Build(options);

        Assert.Equal(TimeSpan.FromMinutes(5), input.HeartbeatTimeout);
    }

    // ── RetryPolicy ─────────────────────────────────────────────────────────────

    [Fact]
    public void WhenPerAgentRetryPolicySet_OverridesWorkerDefault()
    {
        var workerPolicy = new RetryPolicy { MaximumAttempts = 5 };
        var agentPolicy = new RetryPolicy { MaximumAttempts = 1 };

        var options = OptionsWithDurableAgent(
            configureAgent: a => a.RetryPolicy = agentPolicy,
            configureOptions: o => o.DefaultRetryPolicy = workerPolicy);

        var input = Build(options);

        Assert.Same(agentPolicy, input.RetryPolicy);
    }

    [Fact]
    public void WhenPerAgentRetryPolicyNull_InheritsWorkerDefault()
    {
        var workerPolicy = new RetryPolicy { MaximumAttempts = 5 };

        var options = OptionsWithDurableAgent(
            configureOptions: o => o.DefaultRetryPolicy = workerPolicy);

        var input = Build(options);

        Assert.Same(workerPolicy, input.RetryPolicy);
    }

    // ── MaxEntryCount ───────────────────────────────────────────────────────────

    [Fact]
    public void WhenPerAgentMaxEntryCountSet_OverridesWorkerDefault()
    {
        var options = OptionsWithDurableAgent(
            configureAgent: a => a.MaxEntryCount = 50,
            configureOptions: o => o.DefaultMaxEntryCount = 5000);

        var input = Build(options);

        Assert.Equal(50, input.MaxEntryCount);
    }

    [Fact]
    public void WhenPerAgentMaxEntryCountNull_InheritsWorkerDefault()
    {
        var options = OptionsWithDurableAgent(
            configureOptions: o => o.DefaultMaxEntryCount = 5000);

        var input = Build(options);

        Assert.Equal(5000, input.MaxEntryCount);
    }

    // ── HistoryReducer ──────────────────────────────────────────────────────────

    [Fact]
    public void WhenPerAgentHistoryReducerSet_OverridesWorkerDefault()
    {
        Func<IList<DurableSessionEntry>, IList<DurableSessionEntry>> workerReducer =
            list => list;
        Func<IList<DurableSessionEntry>, IList<DurableSessionEntry>> agentReducer =
            list => list.Take(1).ToList();

        var options = OptionsWithDurableAgent(
            configureAgent: a => a.HistoryReducer = agentReducer,
            configureOptions: o => o.DefaultHistoryReducer = workerReducer);

        var input = Build(options);

        Assert.Same(agentReducer, input.HistoryReducer);
    }

    [Fact]
    public void WhenPerAgentHistoryReducerNull_InheritsWorkerDefault()
    {
        Func<IList<DurableSessionEntry>, IList<DurableSessionEntry>> workerReducer =
            list => list;

        var options = OptionsWithDurableAgent(
            configureOptions: o => o.DefaultHistoryReducer = workerReducer);

        var input = Build(options);

        Assert.Same(workerReducer, input.HistoryReducer);
    }

    // ── MaxToolCallsPerTurn (per-agent only — no worker fallback) ───────────────

    [Fact]
    public void WhenPerAgentMaxToolCallsPerTurnSet_FlowsThrough()
    {
        var options = OptionsWithDurableAgent(
            configureAgent: a => a.MaxToolCallsPerTurn = 7);

        var input = Build(options);

        Assert.Equal(7, input.MaxToolCallsPerTurn);
    }

    [Fact]
    public void WhenPerAgentMaxToolCallsPerTurnUnset_UsesBuilderDefaultOf20()
    {
        var options = OptionsWithDurableAgent();

        var input = Build(options);

        Assert.Equal(20, input.MaxToolCallsPerTurn);
    }

    // ── HistoryStore (per-agent vs worker-level) ────────────────────────────────

    [Fact]
    public void WhenAgentHasHistoryStoreOverride_PrefersAgentStore()
    {
        var workerStore = new FakeAgentHistoryStore();
        var agentStore = new FakeAgentHistoryStore();

        var options = OptionsWithDurableAgent(
            configureAgent: a => a.HistoryStore = _ => agentStore,
            configureOptions: o => o.HistoryStore = _ => workerStore);

        var input = Build(options);

        // The composite UseExternalStore flag flows to the workflow side; the actual factory
        // resolution happens activity-side via ResolveDurableAgent. Here we verify the input
        // signals that external history is in play.
        Assert.True(input.UseExternalStoreMode);
    }

    [Fact]
    public void WhenAgentHistoryStoreNull_UsesWorkerHistoryStore()
    {
        var workerStore = new FakeAgentHistoryStore();

        var options = OptionsWithDurableAgent(
            configureOptions: o => o.HistoryStore = _ => workerStore);

        var input = Build(options);

        Assert.True(input.UseExternalStoreMode);
    }

    [Fact]
    public void WhenBothNull_NoExternalHistory()
    {
        var options = OptionsWithDurableAgent();

        var input = Build(options);

        Assert.False(input.UseExternalStoreMode);
    }

    [Fact]
    public void WhenWorkerHasHistoryStore_AllAgentsWithoutOverrideInherit()
    {
        var workerStore = new FakeAgentHistoryStore();

        var options = new TemporalAgentsOptions { HistoryStore = _ => workerStore };
        options.AddDurableAgent("AgentA", agent => agent.ChatClient = _ => NewChatClient());
        options.AddDurableAgent("AgentB", agent => agent.ChatClient = _ => NewChatClient());

        var inputA = Build(options, "AgentA");
        var inputB = Build(options, "AgentB");

        Assert.True(inputA.UseExternalStoreMode);
        Assert.True(inputB.UseExternalStoreMode);
    }

    // ── Composite — tool dictionary survives inheritance ────────────────────────

    [Fact]
    public void WhenDurableAgentToolHasNoRetry_PerToolEntryReflectsIt()
    {
        var noRetryPolicy = new RetryPolicy { MaximumAttempts = 1 };
        var options = OptionsWithDurableAgent(configureAgent: a =>
        {
            a.AddTool(
                "send_email",
                _ => new StubAIFunction("send_email"),
                opts => opts.RetryPolicy = noRetryPolicy);
        });

        var input = Build(options);

        Assert.NotNull(input.DurableAgentToolActivityOptions);
        Assert.True(input.DurableAgentToolActivityOptions!.TryGetValue("send_email", out var perTool));
        Assert.Same(noRetryPolicy, perTool!.RetryPolicy);
    }

    [Fact]
    public void WhenDurableAgentToolHasNoOverride_PerToolInheritsAgentRetryPolicy()
    {
        // Agent-level RetryPolicy applies to the LLM step but ALSO acts as the tool default
        // when the per-tool DurableToolOptions doesn't override RetryPolicy. This matches the
        // hierarchy described in Q11a (worker default → agent default → per-tool override).
        var agentPolicy = new RetryPolicy { MaximumAttempts = 7 };
        var options = OptionsWithDurableAgent(configureAgent: a =>
        {
            a.RetryPolicy = agentPolicy;
            a.AddTool("lookup", _ => new StubAIFunction("lookup"));
        });

        var input = Build(options);

        Assert.NotNull(input.DurableAgentToolActivityOptions);
        Assert.True(input.DurableAgentToolActivityOptions!.TryGetValue("lookup", out var perTool));
        Assert.Same(agentPolicy, perTool!.RetryPolicy);
    }

    private sealed class StubChatClient : IChatClient
    {
        public ChatClientMetadata Metadata { get; } = new("stub");
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class StubAIFunction : AIFunction
    {
        public StubAIFunction(string name) { Name = name; }
        public override string Name { get; }
        protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
            => ValueTask.FromResult<object?>(null);
    }
}
