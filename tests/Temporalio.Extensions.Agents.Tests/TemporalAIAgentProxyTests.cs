using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Moq;
using Xunit;

namespace Temporalio.Extensions.Agents.Tests;

/// <summary>
/// Tests for <see cref="TemporalAIAgentProxy"/>, the external-caller agent that delegates
/// to <see cref="ITemporalAgentClient"/> via Temporal workflow updates.
/// </summary>
public class TemporalAIAgentProxyTests
{
    // ─── Session creation ────────────────────────────────────────────────────

    [Fact]
    public async Task CreateSessionAsync_ReturnsTemporalAgentSession()
    {
        var proxy = CreateProxy("TestAgent");
        var session = await proxy.CreateSessionAsync();
        Assert.IsType<TemporalAgentSession>(session);
    }

    [Fact]
    public async Task CreateSessionAsync_SessionId_ContainsAgentName()
    {
        var proxy = CreateProxy("TestAgent");
        var session = (TemporalAgentSession)await proxy.CreateSessionAsync();
        Assert.Equal("TestAgent", session.SessionId.AgentName, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateSessionAsync_WorkflowId_StartsWithTaPrefix()
    {
        var proxy = CreateProxy("MyAgent");
        var session = (TemporalAgentSession)await proxy.CreateSessionAsync();
        Assert.StartsWith("ta-myagent-", session.SessionId.WorkflowId);
    }

    [Fact]
    public async Task CreateSessionAsync_ProducesUniqueKeys_OnEachCall()
    {
        var proxy = CreateProxy("TestAgent");
        var s1 = (TemporalAgentSession)await proxy.CreateSessionAsync();
        var s2 = (TemporalAgentSession)await proxy.CreateSessionAsync();
        Assert.NotEqual(s1.SessionId.Key, s2.SessionId.Key);
    }

    // ─── RunAsync delegates to ITemporalAgentClient ─────────────────────────

    [Fact]
    public async Task RunAsync_DelegatesToRunAgentAsync()
    {
        var mockClient = new Mock<ITemporalAgentClient>();
        mockClient
            .Setup(c => c.RunAgentAsync(
                It.IsAny<TemporalAgentSessionId>(),
                It.IsAny<RunRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentResponse
            {
                Messages = [new ChatMessage(ChatRole.Assistant, "response")],
                CreatedAt = DateTimeOffset.UtcNow
            });

        var proxy = new TemporalAIAgentProxy("TestAgent", mockClient.Object);
        var session = (TemporalAgentSession)await proxy.CreateSessionAsync();

        var response = await proxy.RunAsync("Hello", session);

        mockClient.Verify(c => c.RunAgentAsync(
            It.Is<TemporalAgentSessionId>(id =>
                id.AgentName.Equals("TestAgent", StringComparison.OrdinalIgnoreCase)),
            It.IsAny<RunRequest>(),
            It.IsAny<CancellationToken>()),
            Times.Once);

        Assert.NotNull(response);
    }

    [Fact]
    public async Task RunAsync_MessageContent_PassedInRequest()
    {
        RunRequest? capturedRequest = null;
        var mockClient = new Mock<ITemporalAgentClient>();
        mockClient
            .Setup(c => c.RunAgentAsync(
                It.IsAny<TemporalAgentSessionId>(),
                It.IsAny<RunRequest>(),
                It.IsAny<CancellationToken>()))
            .Callback<TemporalAgentSessionId, RunRequest, CancellationToken>(
                (_, r, _) => capturedRequest = r)
            .ReturnsAsync(new AgentResponse());

        var proxy = new TemporalAIAgentProxy("TestAgent", mockClient.Object);
        var session = (TemporalAgentSession)await proxy.CreateSessionAsync();

        await proxy.RunAsync("Hello agent!", session);

        Assert.NotNull(capturedRequest);
        Assert.Single(capturedRequest!.Messages);
        Assert.Equal("Hello agent!", capturedRequest.Messages[0].Text);
    }

    // ─── Fire-and-forget ─────────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_WithIsFireAndForget_CallsFireAndForgetMethod()
    {
        var mockClient = new Mock<ITemporalAgentClient>();
        mockClient
            .Setup(c => c.RunAgentFireAndForgetAsync(
                It.IsAny<TemporalAgentSessionId>(),
                It.IsAny<RunRequest>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var proxy = new TemporalAIAgentProxy("TestAgent", mockClient.Object);
        var session = (TemporalAgentSession)await proxy.CreateSessionAsync();

        var options = new TemporalAgentRunOptions { IsFireAndForget = true };
        await proxy.RunAsync("Fire!", session, options);

        // RunAgentAsync should NOT be called
        mockClient.Verify(c => c.RunAgentAsync(
            It.IsAny<TemporalAgentSessionId>(),
            It.IsAny<RunRequest>(),
            It.IsAny<CancellationToken>()),
            Times.Never);

        // RunAgentFireAndForgetAsync SHOULD be called
        mockClient.Verify(c => c.RunAgentFireAndForgetAsync(
            It.IsAny<TemporalAgentSessionId>(),
            It.IsAny<RunRequest>(),
            It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task RunAsync_WithIsFireAndForget_ReturnsEmptyResponse()
    {
        var mockClient = new Mock<ITemporalAgentClient>();
        mockClient
            .Setup(c => c.RunAgentFireAndForgetAsync(
                It.IsAny<TemporalAgentSessionId>(),
                It.IsAny<RunRequest>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var proxy = new TemporalAIAgentProxy("TestAgent", mockClient.Object);
        var session = (TemporalAgentSession)await proxy.CreateSessionAsync();

        var options = new TemporalAgentRunOptions { IsFireAndForget = true };
        var response = await proxy.RunAsync("Fire!", session, options);

        // Fire-and-forget returns an empty (non-null) response immediately
        Assert.NotNull(response);
        Assert.Empty(response.Messages);
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private static TemporalAIAgentProxy CreateProxy(string name)
    {
        var mockClient = new Mock<ITemporalAgentClient>();
        mockClient
            .Setup(c => c.RunAgentAsync(
                It.IsAny<TemporalAgentSessionId>(),
                It.IsAny<RunRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentResponse());

        return new TemporalAIAgentProxy(name, mockClient.Object);
    }
}
