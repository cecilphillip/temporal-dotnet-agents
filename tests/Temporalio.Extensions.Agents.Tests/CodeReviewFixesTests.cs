using FakeItEasy;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Temporalio.Client;
using Temporalio.Extensions.AI;
using Temporalio.Extensions.Agents.HistoryStore;
using Temporalio.Extensions.Agents.Tests.Helpers;
using Temporalio.Extensions.Agents.Tests.HistoryStore;
using Temporalio.Extensions.Agents.Tests.StepMode;
using Temporalio.Extensions.Agents.Workflows;
using Temporalio.Extensions.Hosting;
using Temporalio.Testing;
using Xunit;

namespace Temporalio.Extensions.Agents.Tests;

/// <summary>
/// Code-review remediation tests covering:
/// <list type="bullet">
///   <item>B2: <c>UseExternalAgentHistory&lt;TStore&gt;()</c> must flip
///         <see cref="TemporalAgentsOptions.UseExternalHistory"/> AND register the store
///         in a single call — both call orderings (before / after <c>AddTemporalAgents</c>).</item>
///   <item>S2: step-mode activity must seed <see cref="ChatOptions"/> from the agent's
///         registration-time configuration (Temperature, ModelId, etc.) instead of
///         silently dropping them.</item>
/// </list>
/// </summary>
public class CodeReviewFixesTests
{
    // ── B2: UseExternalAgentHistory<TStore>() one-call UX ────────────────────

    /// <summary>
    /// B2 happy path: calling <c>UseExternalAgentHistory&lt;FakeAgentHistoryStore&gt;()</c>
    /// before <c>AddTemporalAgents</c> on the same builder must produce a service container
    /// where (a) the singleton store is registered AND (b) the resolved
    /// <see cref="TemporalAgentsOptions"/> reports <c>UseExternalHistory == true</c>, even
    /// when the user does not set the flag inside the configure delegate.
    /// </summary>
    [Fact]
    public void UseExternalAgentHistory_BeforeAddTemporalAgents_FlipsFlagAndRegistersStore()
    {
        var services = new ServiceCollection();
        var fakeClient = A.Fake<ITemporalClient>();
        services.AddSingleton(fakeClient);
        var builder = services.AddHostedTemporalWorker("test-task-queue");

        // Order: UseExternalAgentHistory FIRST, then AddTemporalAgents with no explicit flag.
        builder
            .UseExternalAgentHistory<FakeAgentHistoryStore>()
            .AddTemporalAgents(opts => opts.AddAIAgent(new StubAIAgent("a")));

        var sp = services.BuildServiceProvider();

        // Both invariants must hold for the convenience extension to deliver the documented UX.
        var options = sp.GetRequiredService<TemporalAgentsOptions>();
        Assert.True(
            options.UseExternalHistory,
            "UseExternalAgentHistory<TStore>() did not flip TemporalAgentsOptions.UseExternalHistory.");

        var store = sp.GetRequiredService<IAgentHistoryStore>();
        Assert.IsType<FakeAgentHistoryStore>(store);
    }

    /// <summary>
    /// B2 reverse order: calling <c>UseExternalAgentHistory&lt;TStore&gt;()</c> AFTER
    /// <c>AddTemporalAgents</c> still yields the same end state. The singleton options instance
    /// is mutated in place; the store is registered second.
    /// </summary>
    [Fact]
    public void UseExternalAgentHistory_AfterAddTemporalAgents_FlipsFlagAndRegistersStore()
    {
        var services = new ServiceCollection();
        var fakeClient = A.Fake<ITemporalClient>();
        services.AddSingleton(fakeClient);
        var builder = services.AddHostedTemporalWorker("test-task-queue");

        // Order: AddTemporalAgents FIRST (with flag NOT set inside configure), then
        // UseExternalAgentHistory.
        builder
            .AddTemporalAgents(opts => opts.AddAIAgent(new StubAIAgent("a")))
            .UseExternalAgentHistory<FakeAgentHistoryStore>();

        var sp = services.BuildServiceProvider();

        var options = sp.GetRequiredService<TemporalAgentsOptions>();
        Assert.True(
            options.UseExternalHistory,
            "UseExternalAgentHistory<TStore>() did not mutate the already-registered options singleton.");

        var store = sp.GetRequiredService<IAgentHistoryStore>();
        Assert.IsType<FakeAgentHistoryStore>(store);
    }

    /// <summary>
    /// Sanity check: when <c>UseExternalAgentHistory</c> is NOT called, the flag stays at its
    /// default and no <see cref="IAgentHistoryStore"/> is registered. This is the negative
    /// control that ensures the marker logic isn't being triggered spuriously.
    /// </summary>
    [Fact]
    public void AddTemporalAgents_WithoutUseExternalAgentHistory_LeavesFlagFalseAndNoStoreRegistered()
    {
        var services = new ServiceCollection();
        var fakeClient = A.Fake<ITemporalClient>();
        services.AddSingleton(fakeClient);
        var builder = services.AddHostedTemporalWorker("test-task-queue");

        builder.AddTemporalAgents(opts => opts.AddAIAgent(new StubAIAgent("a")));

        var sp = services.BuildServiceProvider();
        var options = sp.GetRequiredService<TemporalAgentsOptions>();
        Assert.False(options.UseExternalHistory);
        Assert.Null(sp.GetService<IAgentHistoryStore>());
    }

    // ── S2: step-mode preserves registration-time ChatOptions ─────────────────

    /// <summary>
    /// S2 main assertion: an agent registered with a configured <see cref="ChatOptions"/>
    /// (Temperature = 0.2, ModelId = "test-model") must have those values flow into the
    /// <see cref="IChatClient"/> call inside <c>RunAgentStepAsync</c>. The
    /// <see cref="ScriptedChatClient"/> captures the <see cref="ChatOptions"/> it sees on each
    /// call so the test can assert the registered values are honored.
    /// </summary>
    [Fact]
    public async Task RunAgentStepAsync_PreservesRegistrationTimeChatOptions()
    {
        // Scripted client returns a single final assistant message. The capturing layer is the
        // value we care about — what ChatOptions did the activity construct?
        var scripted = new ScriptedChatClient(
        [
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")),
        ]);

        // Build DI: IChatClient is resolved by the step activity from this provider.
        var sp = new ServiceCollection()
            .AddSingleton<IChatClient>(scripted)
            .BuildServiceProvider();

        // Construct ChatClientAgent with explicit ChatOptions — this is the value the user
        // configures at registration time and which the (current) step activity drops.
        var configuredOptions = new ChatOptions
        {
            Temperature = 0.2f,
            ModelId = "test-model",
        };

        var agent = new ChatClientAgent(
            scripted,
            new ChatClientAgentOptions
            {
                Name = "Configured",
                ChatOptions = configuredOptions,
                UseProvidedChatClientAsIs = true,
            });

        // Capture the agent's options into TemporalAgentsOptions just like AddAIAgent would
        // (registration-time path). We construct the options via the public `AddAIAgent`
        // entry — its internal constructor is exercised by the ServiceCollection extension,
        // so we go through the same fluent API the user touches.
        var services = new ServiceCollection();
        services.AddSingleton<IChatClient>(scripted);
        var fakeClient = A.Fake<ITemporalClient>();
        services.AddSingleton(fakeClient);
        var builder = services.AddHostedTemporalWorker("step-queue");
        builder.AddTemporalAgents(opts =>
        {
            // Step mode requires DurableExecutionOptions to be registered; we add it
            // manually since the test doesn't go through AddDurableAI.
            opts.AddAIAgent(agent);
        });

        var rootSp = services.BuildServiceProvider();

        // Build the activity directly (the per-tool / step-mode dispatch uses a private
        // factory dictionary populated from TemporalAgentsOptions.GetAgentFactories).
        var factories = rootSp.GetRequiredService<IReadOnlyDictionary<string, Func<IServiceProvider, AIAgent>>>();
        var activities = new AgentActivities(factories, rootSp);

        var stepInput = new AgentStepInput
        {
            AgentName = "Configured",
            Request = new RunRequest("hi") { CorrelationId = "c1" },
            AccumulatedMessages = [new ChatMessage(ChatRole.User, "hi")],
            SessionId = Session.TemporalAgentSessionId.WithRandomKey("Configured"),
        };

        var env = new ActivityEnvironment();
        var result = await env.RunAsync(() => activities.RunAgentStepAsync(stepInput));

        Assert.True(result.IsFinal);

        // Inspect the captured ChatOptions — the registration-time Temperature and ModelId
        // must round-trip into the per-call options. Without the fix these are null.
        var observedOptions = Assert.Single(scripted.Calls).Options;
        Assert.NotNull(observedOptions);
        Assert.Equal(0.2f, observedOptions.Temperature);
        Assert.Equal("test-model", observedOptions.ModelId);
    }

    /// <summary>
    /// Companion to the main S2 test: confirms that even when the registration-time options
    /// carry a <see cref="ChatOptions.ResponseFormat"/>, the request-scoped value (from
    /// <see cref="RunRequest.ResponseFormat"/>) wins. The fix preserves Temperature / ModelId
    /// but must continue to override the three per-turn fields (Instructions, Tools,
    /// ResponseFormat) so existing behavior is unchanged.
    /// </summary>
    [Fact]
    public async Task RunAgentStepAsync_RequestScopedFieldsStillOverrideRegistrationOptions()
    {
        var scripted = new ScriptedChatClient(
        [
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")),
        ]);

        var registeredOptions = new ChatOptions
        {
            Temperature = 0.7f,
            // Pretend the registration carried Json — we'll override it from the request.
            ResponseFormat = ChatResponseFormat.Json,
        };

        var agent = new ChatClientAgent(
            scripted,
            new ChatClientAgentOptions
            {
                Name = "Override",
                ChatOptions = registeredOptions,
                UseProvidedChatClientAsIs = true,
            });

        var services = new ServiceCollection();
        services.AddSingleton<IChatClient>(scripted);
        var fakeClient = A.Fake<ITemporalClient>();
        services.AddSingleton(fakeClient);
        var builder = services.AddHostedTemporalWorker("step-queue");
        builder.AddTemporalAgents(opts => opts.AddAIAgent(agent));

        var rootSp = services.BuildServiceProvider();

        var factories = rootSp.GetRequiredService<IReadOnlyDictionary<string, Func<IServiceProvider, AIAgent>>>();
        var activities = new AgentActivities(factories, rootSp);

        var stepInput = new AgentStepInput
        {
            AgentName = "Override",
            // Request explicitly carries no response format → the activity must replace the
            // registration-time JSON with null.
            Request = new RunRequest("hi") { CorrelationId = "c1" },
            AccumulatedMessages = [new ChatMessage(ChatRole.User, "hi")],
            SessionId = Session.TemporalAgentSessionId.WithRandomKey("Override"),
        };

        var env = new ActivityEnvironment();
        _ = await env.RunAsync(() => activities.RunAgentStepAsync(stepInput));

        var observed = Assert.Single(scripted.Calls).Options;
        Assert.NotNull(observed);
        // Temperature carried forward from registration.
        Assert.Equal(0.7f, observed.Temperature);
        // ResponseFormat overridden by request (which had null) — confirms the override is live.
        Assert.Null(observed.ResponseFormat);
    }
}
