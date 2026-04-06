# Testing Agents

How to test TemporalAgents integrations — from fast unit tests with no server to full integration tests running against a real Temporal environment. The codebase ships with 214 unit tests and 51 integration tests; this guide walks through the patterns they use.

---

## Table of Contents

1. [Overview](#overview)
2. [Unit Testing (No Temporal Server)](#unit-testing-no-temporal-server)
3. [Integration Testing (Real Temporal Server)](#integration-testing-real-temporal-server)
4. [Running Tests](#running-tests)

---

## Overview

The test suite is split into two projects:

| Project | Count | Server Required | Purpose |
|---------|-------|-----------------|---------|
| `Temporalio.Extensions.Agents.Tests` | 214 | No | Configuration, routing, DI registration, serialization |
| `Temporalio.Extensions.Agents.IntegrationTests` | 51 | Yes | Full agent execution, HITL, continue-as-new, history preservation |

**General principles:**

- **Hand-written test doubles** over mocking frameworks — `StubAIAgent` and `CapturingChatClient` are preferred over Moq
- **xUnit** with `[Fact]` attributes and `Assert.*` assertions
- **Exact exception types** — `Assert.Throws<T>` matches the exact type (`ArgumentNullException`, not `ArgumentException`)
- **FakeItEasy** for interfaces that can't easily be stubbed (e.g., `ITemporalClient`)

---

## Unit Testing (No Temporal Server)

### StubAIAgent — Minimal Test Double

`StubAIAgent` is a concrete `AIAgent` that returns a fixed response without calling any LLM. It's the workhorse of the unit test suite:

```csharp
internal sealed class StubAIAgent(
    string? name,
    AgentResponse? fixedResponse = null,
    string? description = null) : AIAgent
{
    public override string? Name { get; } = name;
    public override string? Description { get; } = description;

    protected override ValueTask<AgentSession> CreateSessionCoreAsync(
        CancellationToken cancellationToken = default) =>
        new(new TemporalAgentSession(
            TemporalAgentSessionId.WithRandomKey(Name ?? "stub")));

    protected override Task<AgentResponse> RunCoreAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session = null,
        AgentRunOptions? options = null,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(_fixedResponse);
}
```

**Usage in router tests:**

```csharp
private static StubAIAgent ResponseWithText(string text) =>
    new("Router", new AgentResponse { Text = text });

[Fact]
public async Task RouteAsync_ExactMatch_ReturnsAgentName()
{
    var router = new AIModelAgentRouter(ResponseWithText("WeatherAgent"));

    var result = await router.RouteAsync(
        [new ChatMessage(ChatRole.User, "What's the weather?")],
        descriptors);

    Assert.Equal("WeatherAgent", result);
}
```

### CapturingChatClient — Spy for ChatOptions

`CapturingChatClient` records the `ChatOptions` from each call, letting you assert that middleware propagates options correctly:

```csharp
internal sealed class CapturingChatClient : IChatClient
{
    public List<ChatOptions?> CapturedOptions { get; } = [];
    public ChatOptions? LastOptions => CapturedOptions.LastOrDefault();

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        CapturedOptions.Add(options);
        return Task.FromResult(new ChatResponse(/* stub */));
    }
}
```

### Testing TemporalAgentsOptions Configuration

The fluent `.AddTemporalAgents()` API registers workflows, activities, keyed proxies, and the agent client. Test it by building a `ServiceCollection` and inspecting the resulting `IServiceProvider`:

```csharp
[Fact]
public void AddTemporalAgents_RegistersKeyedAIAgentProxies()
{
    var services = new ServiceCollection();
    services.AddSingleton(A.Fake<ITemporalClient>());

    services
        .AddHostedTemporalWorker("test-queue")
        .AddTemporalAgents(opts =>
        {
            opts.AddAIAgent(new StubAIAgent("AgentA"));
            opts.AddAIAgent(new StubAIAgent("AgentB"));
        });

    var sp = services.BuildServiceProvider();

    // Each agent is registered as a keyed singleton
    var proxyA = sp.GetTemporalAgentProxy("AgentA");
    var proxyB = sp.GetTemporalAgentProxy("AgentB");

    Assert.NotNull(proxyA);
    Assert.NotNull(proxyB);
    Assert.IsType<TemporalAIAgentProxy>(proxyA);
}
```

**Guard clause testing** — validate that null/empty config throws the right exception type:

```csharp
[Fact]
public void AddTemporalAgents_ThrowsOnNullConfigure()
{
    var services = new ServiceCollection();
    services.AddSingleton(A.Fake<ITemporalClient>());

    var builder = services.AddHostedTemporalWorker("q");

    Assert.Throws<ArgumentNullException>(
        () => builder.AddTemporalAgents(null!));
}
```

### Testing AIModelAgentRouter

The router has several edge cases worth testing — exact match, fuzzy match, ambiguous responses, and hallucinations:

```csharp
private static readonly AgentDescriptor[] descriptors =
[
    new("WeatherAgent", "Handles weather queries"),
    new("BillingAgent", "Handles billing inquiries"),
    new("TechSupport", "Handles technical support")
];

[Fact]
public async Task RouteAsync_FuzzyMatch_ReturnsAgentName()
{
    // LLM returns "I think WeatherAgent is best" — fuzzy finds it
    var router = new AIModelAgentRouter(
        ResponseWithText("I think WeatherAgent is best"));

    var result = await router.RouteAsync(messages, descriptors);
    Assert.Equal("WeatherAgent", result);
}

[Fact]
public async Task RouteAsync_MultipleNamesInResponse_ThrowsAmbiguousException()
{
    // LLM mentions two agents — ambiguous
    var router = new AIModelAgentRouter(
        ResponseWithText("WeatherAgent or BillingAgent"));

    await Assert.ThrowsAsync<InvalidOperationException>(
        () => router.RouteAsync(messages, descriptors));
}

[Fact]
public async Task RouteAsync_UnknownName_ThrowsInvalidOperationException()
{
    var router = new AIModelAgentRouter(
        ResponseWithText("NonexistentAgent"));

    await Assert.ThrowsAsync<InvalidOperationException>(
        () => router.RouteAsync(messages, descriptors));
}
```

### Testing StateBag Serialization

`ExecuteAgentInput` and `ExecuteAgentResult` carry an optional serialized StateBag. Verify round-trip fidelity:

```csharp
[Fact]
public void ExecuteAgentInput_WithStateBag_RoundTrips()
{
    var bag = new AgentSessionStateBag();
    bag["key1"] = "value1";

    var input = new ExecuteAgentInput("TestAgent", request, history,
        serializedStateBag: bag.Serialize());

    var json = JsonSerializer.Serialize(input);
    var deserialized = JsonSerializer.Deserialize<ExecuteAgentInput>(json)!;

    Assert.NotNull(deserialized.SerializedStateBag);
}

[Fact]
public void TemporalAgentSession_SerializeStateBag_EmptyBag_ReturnsNull()
{
    var session = new TemporalAgentSession(
        TemporalAgentSessionId.WithRandomKey("test"));

    // Empty bags serialize to null — no wasted payload
    Assert.Null(session.SerializeStateBag());
}
```

---

## Integration Testing (Real Temporal Server)

### IntegrationTestFixture Pattern

The `IntegrationTestFixture` manages a local Temporal test server and a hosted worker, shared across all tests in a class via `IClassFixture<>`:

```csharp
public sealed class IntegrationTestFixture : IAsyncLifetime
{
    private IHost? _host;

    public const string TaskQueue = "integration-test-agents";
    public WorkflowEnvironment Environment { get; private set; } = null!;
    public ITemporalClient Client => Environment.Client;
    public AIAgent AgentProxy { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        // Starts an in-process Temporal server — no external process needed
        Environment = await WorkflowEnvironment.StartLocalAsync();

        _host = BuildHost();
        await _host.StartAsync();

        AgentProxy = _host.Services.GetTemporalAgentProxy("EchoAgent");
    }

    public IHost BuildHost()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<ITemporalClient>(Environment.Client);

        builder.Services
            .AddHostedTemporalWorker(TaskQueue)
            .AddTemporalAgents(options =>
                options.AddAIAgent(new EchoAIAgent("EchoAgent")));

        return builder.Build();
    }

    public async Task DisposeAsync()
    {
        if (_host is not null)
        {
            await _host.StopAsync();
            _host.Dispose();
        }
        await Environment.ShutdownAsync();
    }
}
```

The `BuildHost()` method is public so tests that need isolated configuration (e.g., custom timeouts) can build their own host.

### Custom Test Agents

All integration test agents extend `TestAgentBase`, which provides session management and a `CreateEchoResponse` helper:

| Agent | Behavior | Tests |
|-------|----------|-------|
| `EchoAIAgent` | Returns `"Echo [{turnCount}]: {lastMessage}"` | History rebuild, multi-turn conversations |
| `SlowThenFastAIAgent` | First N calls delay, then instant | Activity timeout enforcement |
| `FailThenSucceedAIAgent` | First N calls throw, then succeed | Retry mechanism, error propagation |
| `EmptyResponseAIAgent` | Returns empty `AgentResponse` | Edge-case response handling |

**Example — `EchoAIAgent`:**

```csharp
public class EchoAIAgent(string name) : TestAgentBase(name)
{
    protected override Task<AgentResponse> RunCoreAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session = null,
        AgentRunOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var messageList = messages.ToList();
        var turnCount = messageList.Count(m => m.Role == ChatRole.User);
        var lastUserMessage = messageList.LastOrDefault(m => m.Role == ChatRole.User);

        return Task.FromResult(
            CreateEchoResponse(turnCount, lastUserMessage?.Text ?? ""));
    }
}
```

### Single-Turn and Multi-Turn Tests

```csharp
public class AgentIntegrationTests(IntegrationTestFixture fixture)
    : IClassFixture<IntegrationTestFixture>
{
    [Fact]
    public async Task SingleTurn_ReturnsEchoResponse()
    {
        var session = await fixture.AgentProxy.CreateSessionAsync();
        var response = await fixture.AgentProxy.RunAsync("Hello", session);

        Assert.Contains("Echo", response.Text);
    }

    [Fact]
    public async Task MultiTurn_PreservesConversationHistory()
    {
        var session = await fixture.AgentProxy.CreateSessionAsync();

        var r1 = await fixture.AgentProxy.RunAsync("First message", session);
        var r2 = await fixture.AgentProxy.RunAsync("Second message", session);

        // Turn count increments as history accumulates
        Assert.Contains("Echo [1]", r1.Text);
        Assert.Contains("Echo [2]", r2.Text);
    }
}
```

### HITL Approval Flow Tests

Integration tests cover both the timeout path and the happy path:

```csharp
[Fact]
public async Task RequestApproval_TimesOut_ReturnsRejectedDecision()
{
    // Configure a 2-second approval timeout
    // ... (custom host with short ApprovalTimeout)

    // Send update that triggers approval request
    var handle = env.Client.GetWorkflowHandle<AgentWorkflow>(workflowId);
    var decision = await handle.ExecuteUpdateAsync<AgentWorkflow, DurableApprovalDecision>(
        wf => wf.RequestApprovalAsync(new DurableApprovalRequest
        {
            RequestId   = Guid.NewGuid().ToString("N"),
            Description = "Test action — Test details"
        }));

    Assert.False(decision.Approved); // Timed out → rejected
}

[Fact]
public async Task SubmitApproval_BeforeTimeout_ReturnsApprovedDecision()
{
    // Configure a 5-minute approval timeout
    // Start approval in background, then submit decision

    var approvalDecision = new DurableApprovalDecision
    {
        RequestId = pending.RequestId,
        Approved  = true,
        Reason    = "Looks good"
    };

    var decision = await handle.ExecuteUpdateAsync<AgentWorkflow, DurableApprovalDecision>(
        wf => wf.SubmitApprovalAsync(approvalDecision));

    Assert.True(decision.Approved);
}
```

### Continue-as-New Tests

These tests verify that conversation history, turn counts, and timeouts survive continue-as-new transitions:

```csharp
[Fact]
public async Task ContinueAsNew_HistoryCarriedForward_ConversationContinuesSeamlessly()
{
    // Use a custom WorkflowEnvironment with a low history count
    // threshold (20 events) to trigger continue-as-new quickly
    var env = await WorkflowEnvironment.StartLocalAsync();

    // Send enough turns to trigger continue-as-new
    for (int i = 1; i <= 10; i++)
    {
        var response = await proxy.RunAsync($"Turn {i}", session);
        Assert.Contains($"Echo [{i}]", response.Text);
    }

    // Conversation state is preserved seamlessly
}

[Fact]
public async Task ContinueAsNew_RunIdChangesAfterSufficientHistory()
{
    var initialRunId = /* capture from describe */;

    // Generate enough history to trigger continue-as-new
    // ...

    var currentRunId = /* describe again */;
    Assert.NotEqual(initialRunId, currentRunId);
}
```

### Custom Host Per Test

When a test needs isolated configuration (different agents, timeouts, etc.), build a fresh host using the fixture's `WorkflowEnvironment`:

```csharp
[Fact]
public async Task AgentFactory_ResolvesServiceDependencies_AtActivityTime()
{
    var builder = Host.CreateApplicationBuilder();
    builder.Services.AddSingleton<ITemporalClient>(fixture.Environment.Client);
    builder.Services.AddSingleton<IMyCustomService, MyCustomService>();

    builder.Services
        .AddHostedTemporalWorker("custom-queue-" + Guid.NewGuid())
        .AddTemporalAgents(opts =>
        {
            opts.AddAIAgentFactory("FactoryAgent",
                sp => new MyAgent(sp.GetRequiredService<IMyCustomService>()));
        });

    using var host = builder.Build();
    await host.StartAsync();

    var proxy = host.Services.GetTemporalAgentProxy("FactoryAgent");
    // ... test with the custom agent
}
```

---

## Running Tests

```bash
# Unit tests only (fast, no server needed)
just test-unit

# Integration tests (requires: temporal server start-dev)
just test-integration

# Both suites
just test

# Filter by test name
just test-filter "FullyQualifiedName~Router"

# Unit tests with code coverage
just test-coverage
```

> **Integration tests** require a running Temporal server:
> ```bash
> temporal server start-dev --namespace default
> ```
> Alternatively, `WorkflowEnvironment.StartLocalAsync()` in the test fixture starts an in-process server automatically — no manual setup needed for the standard integration test suite.

---

## References

- `tests/Temporalio.Extensions.Agents.Tests/` — 214 unit tests
- `tests/Temporalio.Extensions.Agents.IntegrationTests/` — 51 integration tests
- [Durability & Determinism](../architecture/durability-and-determinism.md) — why activity results are cached on replay
- [Agent Sessions & Workflow Loop](../architecture/agent-sessions-and-workflow-loop.md) — session lifecycle under test

---

_Last updated: 2026-03-13_
