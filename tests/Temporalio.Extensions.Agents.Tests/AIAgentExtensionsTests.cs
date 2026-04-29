using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Temporalio.Extensions.Agents.Tests.Helpers;
using Xunit;

namespace Temporalio.Extensions.Agents.Tests;

/// <summary>
/// Tests for <see cref="AIAgentExtensions.RunFireAndForgetAsync"/>.
/// </summary>
public class AIAgentExtensionsTests
{
    [Fact]
    public async Task RunFireAndForgetAsync_PassesIsFireAndForget()
    {
        var capturing = new CapturingAgent("Test");
        var session = await capturing.CreateSessionAsync();

        await capturing.RunFireAndForgetAsync("hi", session);

        var captured = Assert.IsType<TemporalAgentRunOptions>(capturing.LastOptions);
        Assert.True(captured.IsFireAndForget);
    }

    [Fact]
    public async Task RunFireAndForgetAsync_PassesSessionAndCancellationToken()
    {
        var capturing = new CapturingAgent("Test");
        var session = await capturing.CreateSessionAsync();
        using var cts = new CancellationTokenSource();

        await capturing.RunFireAndForgetAsync("hi", session, cts.Token);

        Assert.Same(session, capturing.LastSession);
        Assert.Equal(cts.Token, capturing.LastCancellationToken);
    }

    [Fact]
    public async Task RunFireAndForgetAsync_NullAgent_Throws()
    {
        AIAgent? agent = null;
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            agent!.RunFireAndForgetAsync("hi"));
    }

    [Fact]
    public async Task RunFireAndForgetAsync_EmptyMessage_Throws()
    {
        var capturing = new CapturingAgent("Test");
        await Assert.ThrowsAsync<ArgumentException>(() =>
            capturing.RunFireAndForgetAsync(string.Empty));
    }

    /// <summary>
    /// Records the options/session/cancellation token passed into RunCoreAsync so tests can assert on them.
    /// </summary>
    private sealed class CapturingAgent(string name) : AIAgent
    {
        public override string? Name { get; } = name;

        public AgentRunOptions? LastOptions { get; private set; }
        public AgentSession? LastSession { get; private set; }
        public CancellationToken LastCancellationToken { get; private set; }

        protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken = default) =>
            new(new StubSession());

        protected override ValueTask<JsonElement> SerializeSessionCoreAsync(
            AgentSession session,
            JsonSerializerOptions? jsonSerializerOptions = null,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
            JsonElement serializedState,
            JsonSerializerOptions? jsonSerializerOptions = null,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        protected override Task<AgentResponse> RunCoreAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session = null,
            AgentRunOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            LastOptions = options;
            LastSession = session;
            LastCancellationToken = cancellationToken;
            return Task.FromResult(new AgentResponse());
        }

        protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session = null,
            AgentRunOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new AgentResponseUpdate();
            await Task.CompletedTask;
        }
    }

    private sealed class StubSession : AgentSession
    {
    }
}
