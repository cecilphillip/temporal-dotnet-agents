# Testing — Temporalio.Extensions.AI

## Two Testing Layers

Durable AI code has two distinct testing concerns, and they call for different tools:

| Layer | What to test | Tool |
|---|---|---|
| **Application logic** | Code that depends on `IDurableChatSessionClient` — controllers, services, background jobs | xUnit unit tests with a stub implementation |
| **Library integration** | The workflow, activities, and middleware together — that the full pipeline produces correct results | xUnit integration tests using `WorkflowEnvironment.StartLocalAsync()` |

The rule of thumb: if your code just *calls* `ChatAsync` or `GetHistoryAsync`, unit test it with a stub. If you are verifying that conversation history accumulates correctly or that `ContinueAsNew` works, use an integration test.

---

## Unit Testing Application Code

### Stub `IDurableChatSessionClient`

`DurableChatSessionClient` is thin Temporal protocol infrastructure — it adapts `ChatAsync` calls to workflow updates. Testing it directly (by mocking `ITemporalClient` and asserting `ExecuteUpdateAsync` was called) only verifies the SDK's API surface, not your business logic.

The right move is to write your application code against `IDurableChatSessionClient` and inject a stub in tests:

```csharp
// Production service
public class ConversationService(IDurableChatSessionClient client)
{
    public async Task<string> AskAsync(string sessionId, string question)
    {
        var response = await client.ChatAsync(
            sessionId,
            [new ChatMessage(ChatRole.User, question)]);

        return response.Text ?? string.Empty;
    }
}
```

```csharp
// Stub for unit tests
public class StubChatSessionClient : IDurableChatSessionClient
{
    public Func<string, IEnumerable<ChatMessage>, ChatOptions?, CancellationToken, Task<ChatResponse>>
        ChatAsyncHandler { get; set; } = (_, _, _, _) =>
            Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, "stub reply")]));

    public Task<ChatResponse> ChatAsync(string conversationId, IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, CancellationToken cancellationToken = default)
        => ChatAsyncHandler(conversationId, messages, options, cancellationToken);

    public Task<IReadOnlyList<ChatMessage>> GetHistoryAsync(string conversationId,
        CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<ChatMessage>>([]);

    public Task<DurableApprovalRequest?> GetPendingApprovalAsync(string conversationId,
        CancellationToken cancellationToken = default)
        => Task.FromResult<DurableApprovalRequest?>(null);

    public Task<DurableApprovalDecision> SubmitApprovalAsync(string conversationId,
        DurableApprovalDecision decision, CancellationToken cancellationToken = default)
        => Task.FromResult(decision);
}
```

```csharp
// Test
[Fact]
public async Task AskAsync_Returns_AssistantText()
{
    var stub = new StubChatSessionClient();
    stub.ChatAsyncHandler = (_, _, _, _) =>
        Task.FromResult(new ChatResponse(
            [new ChatMessage(ChatRole.Assistant, "Paris.")]));

    var service = new ConversationService(stub);
    var result  = await service.AskAsync("conv-1", "What is the capital of France?");

    Assert.Equal("Paris.", result);
}
```

Register the stub in DI with the interface type:

```csharp
services.AddSingleton<IDurableChatSessionClient, StubChatSessionClient>();
```

---

## Integration Testing with `WorkflowEnvironment`

Integration tests use `WorkflowEnvironment.StartLocalAsync()`, which starts an embedded Temporal server inside the test process. No external `temporal server start-dev` is required — the server starts and stops with the test suite.

### NuGet packages

```xml
<PackageReference Include="Temporalio.Testing" Version="1.11.1" />
<PackageReference Include="xunit" Version="2.9.3" />
<PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.12.0" />
```

### Fixture pattern

Share the `WorkflowEnvironment` and the hosted worker across all tests in a class via `IClassFixture<T>`. Starting a local server takes a couple of seconds — sharing it amortizes that cost.

```csharp
public sealed class IntegrationTestFixture : IAsyncLifetime
{
    private IHost? _host;

    public const string TaskQueue = "test-durable-ai";
    public WorkflowEnvironment Environment { get; private set; } = null!;
    public ITemporalClient Client => Environment.Client;
    public TestChatClient ChatClient { get; } = new();
    public DurableChatSessionClient SessionClient { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        Environment = await WorkflowEnvironment.StartLocalAsync();

        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<ITemporalClient>(Environment.Client);
        builder.Services.AddSingleton<IChatClient>(ChatClient);

        builder.Services
            .AddHostedTemporalWorker(TaskQueue)
            .AddDurableAI(opts =>
            {
                opts.ActivityTimeout   = TimeSpan.FromSeconds(30);
                opts.HeartbeatTimeout  = TimeSpan.FromSeconds(10);
                opts.SessionTimeToLive = TimeSpan.FromMinutes(5);
            });

        _host = builder.Build();
        await _host.StartAsync();
        SessionClient = _host.Services.GetRequiredService<DurableChatSessionClient>();
    }

    public async Task DisposeAsync()
    {
        if (_host is not null) { await _host.StopAsync(); _host.Dispose(); }
        await Environment.ShutdownAsync();
    }
}
```

### `TestChatClient` — the `IChatClient` stub

Register a deterministic `IChatClient` stub so tests are not coupled to a live LLM. The pattern used in this library's own integration tests:

```csharp
public sealed class TestChatClient : IChatClient
{
    private int _callCount;

    public int CallCount => _callCount;
    public List<IList<ChatMessage>> ReceivedMessages { get; } = [];
    public string ResponsePrefix { get; set; } = "Response";

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var list = messages.ToList();
        lock (ReceivedMessages) { ReceivedMessages.Add(list); }
        Interlocked.Increment(ref _callCount);

        var lastUser = list.LastOrDefault(m => m.Role == ChatRole.User)?.Text ?? "(empty)";

        return Task.FromResult(new ChatResponse(
            [new ChatMessage(ChatRole.Assistant, $"{ResponsePrefix}: {lastUser}")])
        {
            ModelId = "test-model",
            Usage = new UsageDetails
            {
                InputTokenCount  = lastUser.Length,
                OutputTokenCount = lastUser.Length + ResponsePrefix.Length + 2,
            },
        });
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await GetResponseAsync(messages, options, cancellationToken);
        foreach (var update in response.ToChatResponseUpdates()) yield return update;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }
}
```

`ResponsePrefix` lets individual tests vary the reply without creating a new stub class.

### Writing integration tests

```csharp
public class DurableChatSessionTests(IntegrationTestFixture fixture)
    : IClassFixture<IntegrationTestFixture>
{
    [Fact]
    public async Task ChatAsync_Returns_AssistantResponse()
    {
        var conversationId = $"test-{Guid.NewGuid():N}";

        var response = await fixture.SessionClient.ChatAsync(
            conversationId,
            [new ChatMessage(ChatRole.User, "Hello")]);

        Assert.Equal("Response: Hello", response.Text);
    }

    [Fact]
    public async Task MultiTurn_Accumulates_History()
    {
        var conversationId = $"test-{Guid.NewGuid():N}";

        await fixture.SessionClient.ChatAsync(conversationId,
            [new ChatMessage(ChatRole.User, "First message")]);

        await fixture.SessionClient.ChatAsync(conversationId,
            [new ChatMessage(ChatRole.User, "Second message")]);

        var history = await fixture.SessionClient.GetHistoryAsync(conversationId);

        // user + assistant + user + assistant = 4 messages
        Assert.Equal(4, history.Count);
        Assert.Equal(ChatRole.User, history[0].Role);
        Assert.Equal(ChatRole.Assistant, history[1].Role);
    }

    [Fact]
    public async Task Same_ConversationId_Reuses_Workflow()
    {
        var conversationId = $"test-{Guid.NewGuid():N}";
        int callsBefore = fixture.ChatClient.CallCount;

        await fixture.SessionClient.ChatAsync(conversationId,
            [new ChatMessage(ChatRole.User, "Turn 1")]);
        await fixture.SessionClient.ChatAsync(conversationId,
            [new ChatMessage(ChatRole.User, "Turn 2")]);

        // Two ChatAsync calls → two LLM calls, one workflow
        Assert.Equal(callsBefore + 2, fixture.ChatClient.CallCount);
    }
}
```

> **Unique conversation IDs per test:** Always generate a fresh `conversationId` (e.g., `Guid.NewGuid()`) in each test. Tests sharing a conversation ID share workflow state — history from one test leaks into another.

---

## What NOT to Test

**Don't mock `ITemporalClient` to assert SDK calls.** Tests like "verify that `StartWorkflowAsync` was called with `WorkflowIdConflictPolicy.UseExisting`" only verify the Temporal SDK's API surface. They don't catch bugs in serialization, history management, or retry behavior. Use integration tests for those.

**Don't unit test `DurableChatSessionClient` directly.** It is thin infrastructure — a Temporal protocol adapter. Its correct behavior is proven by the integration tests. Making application code that *depends on it* testable is the purpose of `IDurableChatSessionClient`.

**Don't use a real LLM in integration tests.** LLM responses are non-deterministic, slow, and cost money. The `TestChatClient` pattern gives you full control over what the "LLM" returns, making assertions reliable.

---

## Running the Tests

```bash
# Unit tests — no server required
just test-unit-ai

# Integration tests — uses embedded Temporal server (no external process needed)
just test-integration-ai

# All tests
just test
```

The AI integration tests use `WorkflowEnvironment.StartLocalAsync()`, which downloads and runs an embedded Temporal CLI binary. No separate `temporal server start-dev` process is needed for the AI integration tests. The Agents integration tests (in `Temporalio.Extensions.Agents.IntegrationTests`) **do** require an external server — see `just test-integration`.
