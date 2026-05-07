using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Temporalio.Client;
using Temporalio.Extensions.Agents.IntegrationTests.Helpers;
using Temporalio.Extensions.Agents.Session;
using Temporalio.Extensions.Agents.Workflows;
using Temporalio.Extensions.Hosting;
using Xunit;

namespace Temporalio.Extensions.Agents.IntegrationTests;

/// <summary>
/// End-to-end integration tests for <c>Temporalio.Extensions.Agents</c>.
/// Each test connects to a live (local) Temporal server via <see cref="IntegrationTestFixture"/>
/// and exercises the full path: proxy → <see cref="DefaultTemporalAgentClient"/> →
/// <see cref="AgentWorkflow"/> Update → <see cref="AgentActivities"/> → <see cref="Helpers.EchoAIAgent"/>.
/// </summary>
[Trait("Category", "Integration")]
public class AgentIntegrationTests : IClassFixture<IntegrationTestFixture>
{
    // Unique suffix per test-run to prevent cross-run workflow ID collisions
    // (relevant for tests that use explicit session IDs).
    private static readonly string s_runId = Guid.NewGuid().ToString("N")[..8];

    private readonly IntegrationTestFixture _fixture;

    public AgentIntegrationTests(IntegrationTestFixture fixture)
    {
        _fixture = fixture;
    }

    // ── Single-turn ────────────────────────────────────────────────────────────

    [Fact]
    public async Task SingleTurn_ReturnsEchoResponse()
    {
        var session = await _fixture.AgentProxy.CreateSessionAsync();

        var response = await _fixture.AgentProxy.RunAsync("Hello!", session);

        Assert.NotNull(response);
        Assert.NotEmpty(response.Messages);
        Assert.Contains("Echo [1]: Hello!", response.Messages[0].Text);
    }

    [Fact]
    public async Task SingleTurn_SessionRoutesToExpectedWorkflowId()
    {
        var session = (TemporalAgentSession)await _fixture.AgentProxy.CreateSessionAsync();

        var response = await _fixture.AgentProxy.RunAsync("Ping", session);

        // The v0.3 durable path does not stamp AgentResponse.AgentId (AgentWorkflowWrapper
        // was removed). Instead, verify session-to-workflow routing: the workflow we just
        // ran is reachable via the session's WorkflowId, and its history includes the turn.
        Assert.NotNull(response);
        Assert.NotEmpty(response.Messages);
        Assert.StartsWith("ta-echoagent-", session.SessionId.WorkflowId);

        var handle = _fixture.Client.GetWorkflowHandle<AgentWorkflow>(session.SessionId.WorkflowId);
        var history = await handle.QueryAsync(wf => wf.GetHistory());
        Assert.NotEmpty(history);
    }

    // ── Multi-turn / history ───────────────────────────────────────────────────

    [Fact]
    public async Task MultiTurn_PreservesConversationHistory()
    {
        var session = await _fixture.AgentProxy.CreateSessionAsync();

        var r1 = await _fixture.AgentProxy.RunAsync("First message", session);
        Assert.Contains("Echo [1]: First message", r1.Messages[0].Text);

        var r2 = await _fixture.AgentProxy.RunAsync("Second message", session);
        // The activity rebuilt history with 2 user messages → turn count is 2.
        Assert.Contains("Echo [2]: Second message", r2.Messages[0].Text);
    }

    [Fact]
    public async Task MultiTurn_ThreeTurns_TurnCountIncrementsCorrectly()
    {
        var session = await _fixture.AgentProxy.CreateSessionAsync();

        await _fixture.AgentProxy.RunAsync("A", session);
        await _fixture.AgentProxy.RunAsync("B", session);
        var r3 = await _fixture.AgentProxy.RunAsync("C", session);

        Assert.Contains("Echo [3]: C", r3.Messages[0].Text);
    }

    // ── Fire-and-forget ────────────────────────────────────────────────────────

    [Fact]
    public async Task FireAndForget_ReturnsEmptyResponseImmediately()
    {
        var session = await _fixture.AgentProxy.CreateSessionAsync();
        var options = new TemporalAgentRunOptions { IsFireAndForget = true };

        var response = await _fixture.AgentProxy.RunAsync("Background task", session, options);

        Assert.NotNull(response);
        Assert.Empty(response.Messages);
    }

    // ── Session resume ─────────────────────────────────────────────────────────

    [Fact]
    public async Task SessionResume_SameSessionIdRoutesToSameWorkflow()
    {
        // Turn 1 — establish history.
        var session = (TemporalAgentSession)await _fixture.AgentProxy.CreateSessionAsync();
        await _fixture.AgentProxy.RunAsync("Turn 1", session);

        // Reconstruct the session purely from its ID (simulates reconnect after process restart).
        var resumedSession = new TemporalAgentSession(session.SessionId);
        var response = await _fixture.AgentProxy.RunAsync("Turn 2", resumedSession);

        // History is preserved — the workflow state includes the prior turn.
        Assert.Contains("Echo [2]: Turn 2", response.Messages[0].Text);
    }

    // ── Explicit session IDs ───────────────────────────────────────────────────

    [Fact]
    public async Task ExplicitSessionId_DeterministicRoutingForSameKey()
    {
        var sessionId = new TemporalAgentSessionId("EchoAgent", $"user_{s_runId}");
        var session = new TemporalAgentSession(sessionId);

        var r1 = await _fixture.AgentProxy.RunAsync("First call", session);
        // Second call to the same explicit session ID routes to the same workflow.
        var r2 = await _fixture.AgentProxy.RunAsync("Second call", session);

        Assert.Contains("Echo [1]:", r1.Messages[0].Text);
        Assert.Contains("Echo [2]:", r2.Messages[0].Text);
    }

    [Fact]
    public async Task ExplicitSessionId_WorkflowIdMatchesExpectedFormat()
    {
        var sessionId = new TemporalAgentSessionId("EchoAgent", $"check_{s_runId}");
        Assert.Equal($"ta-echoagent-check_{s_runId}", sessionId.WorkflowId);
    }

    // ── Agent not registered ────────────────────────────────────────────────

    [Fact]
    public void GetTemporalAgentProxy_UnregisteredAgent_ThrowsKeyNotFoundException()
    {
        // Resolve the IServiceProvider from the fixture's host.
        // The fixture registers "EchoAgent" — any other name should fail.
        using var host = _fixture.BuildHost();

        var ex = Assert.Throws<KeyNotFoundException>(
            () => host.Services.GetTemporalAgentProxy("NonExistentAgent"));

        Assert.Contains("NonExistentAgent", ex.Message);
    }

    // ── Agent factory with DI dependencies ────────────────────────────────────

    [Fact]
    public async Task AgentFactory_ResolvesServiceDependencies_AtActivityTime()
    {
        // Register a custom service and an agent factory that depends on it.
        // The factory is called at activity execution time with the worker's IServiceProvider.
        var taskQueue = $"factory-di-{Guid.NewGuid():N}";
        var greeting = "Greetings from DI!";

        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<ITemporalClient>(_fixture.Client);

        // Register a custom service that the agent factory will resolve.
        builder.Services.AddSingleton<IGreetingService>(new GreetingService(greeting));

        builder.Services
            .AddHostedTemporalWorker(taskQueue)
            .AddTemporalAgents(options => options.AddDurableAgent("DIAgent", a =>
            {
                a.ChatClient = sp =>
                {
                    var greetingSvc = sp.GetRequiredService<IGreetingService>();
                    return new GreetingChatClient(greetingSvc);
                };
            }));

        using var host = builder.Build();
        await host.StartAsync();

        try
        {
            var proxy = host.Services.GetTemporalAgentProxy("DIAgent");
            var session = await proxy.CreateSessionAsync();

            var response = await proxy.RunAsync("Hello", session);

            // The agent should include the DI-resolved greeting in its response.
            Assert.NotNull(response);
            Assert.NotEmpty(response.Messages);
            Assert.Contains(greeting, response.Messages[0].Text);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    // ── Helpers for DI factory test ─────────────────────────────────────────────

    /// <summary>Simple service interface for DI factory testing.</summary>
    private interface IGreetingService
    {
        string GetGreeting();
    }

    /// <summary>Implementation of <see cref="IGreetingService"/>.</summary>
    private sealed class GreetingService(string greeting) : IGreetingService
    {
        public string GetGreeting() => greeting;
    }

    /// <summary>
    /// Chat client that uses an injected <see cref="IGreetingService"/> to produce its response.
    /// </summary>
    private sealed class GreetingChatClient : IChatClient
    {
        private readonly IGreetingService _greetingService;

        public GreetingChatClient(IGreetingService greetingService)
        {
            _greetingService = greetingService;
        }

        public ChatClientMetadata Metadata { get; } = new("greeting");

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new ChatResponse(
                new ChatMessage(ChatRole.Assistant, _greetingService.GetGreeting())));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var resp = await GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
            foreach (var msg in resp.Messages)
            {
                yield return new ChatResponseUpdate(msg.Role, msg.Text);
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }
}
