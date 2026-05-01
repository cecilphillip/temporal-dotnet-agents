using System.Text.Json;
using FakeItEasy;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Temporalio.Extensions.Agents.Session;
using Temporalio.Extensions.Agents.Workflows;
using Xunit;

namespace Temporalio.Extensions.Agents.Tests;

/// <summary>
/// Tests for the optional <c>correlationId</c> parameter on
/// <see cref="StructuredOutputExtensions.RunAgentAsync{T}"/> and the AIAgent overload.
/// The proxy / agent overloads thread the value through <see cref="TemporalAgentRunOptions"/>;
/// the <see cref="ITemporalAgentClient"/> overload threads it directly into a fresh
/// <see cref="RunRequest"/>.
/// </summary>
public class StructuredOutputExtensionsTests
{
    private record Reply(string Answer);

    [Fact]
    public async Task RunAgentAsync_WithCorrelationId_OverridesRequestCorrelationId()
    {
        RunRequest? capturedRequest = null;
        var fakeClient = A.Fake<ITemporalAgentClient>();
        A.CallTo(() => fakeClient.RunAgentAsync(
                A<TemporalAgentSessionId>._,
                A<RunRequest>._,
                A<CancellationToken>._))
            .Invokes((TemporalAgentSessionId _, RunRequest r, CancellationToken _) => capturedRequest = r)
            .Returns(Task.FromResult(new AgentResponse
            {
                Messages = [new ChatMessage(ChatRole.Assistant, JsonSerializer.Serialize(new Reply("ok")))],
            }));

        var sessionId = TemporalAgentSessionId.WithRandomKey("StructuredAgent");
        var request = new RunRequest("question") { CorrelationId = "original" };

        var result = await fakeClient.RunAgentAsync<Reply>(
            sessionId,
            request,
            options: null,
            correlationId: "caller-supplied-corr");

        Assert.Equal("ok", result.Answer);
        Assert.NotNull(capturedRequest);
        Assert.Equal("caller-supplied-corr", capturedRequest!.CorrelationId);
    }

    [Fact]
    public async Task RunAgentAsync_WithoutCorrelationId_UsesRequestCorrelationId()
    {
        RunRequest? capturedRequest = null;
        var fakeClient = A.Fake<ITemporalAgentClient>();
        A.CallTo(() => fakeClient.RunAgentAsync(
                A<TemporalAgentSessionId>._,
                A<RunRequest>._,
                A<CancellationToken>._))
            .Invokes((TemporalAgentSessionId _, RunRequest r, CancellationToken _) => capturedRequest = r)
            .Returns(Task.FromResult(new AgentResponse
            {
                Messages = [new ChatMessage(ChatRole.Assistant, JsonSerializer.Serialize(new Reply("ok")))],
            }));

        var sessionId = TemporalAgentSessionId.WithRandomKey("StructuredAgent");
        var request = new RunRequest("question") { CorrelationId = "original" };

        var result = await fakeClient.RunAgentAsync<Reply>(sessionId, request);

        Assert.Equal("ok", result.Answer);
        Assert.NotNull(capturedRequest);
        Assert.Equal("original", capturedRequest!.CorrelationId);
    }

    [Fact]
    public async Task RunAsyncOnAIAgent_WithCorrelationId_PassesViaTemporalAgentRunOptions()
    {
        // The AIAgent overload feeds the optional correlationId through TemporalAgentRunOptions
        // so that the underlying TemporalAIAgentProxy.RunCoreAsync picks it up via the
        // pattern-match path. Use the proxy directly with a fake client to verify.
        RunRequest? capturedRequest = null;
        var fakeClient = A.Fake<ITemporalAgentClient>();
        A.CallTo(() => fakeClient.RunAgentAsync(
                A<TemporalAgentSessionId>._,
                A<RunRequest>._,
                A<CancellationToken>._))
            .Invokes((TemporalAgentSessionId _, RunRequest r, CancellationToken _) => capturedRequest = r)
            .Returns(Task.FromResult(new AgentResponse
            {
                Messages = [new ChatMessage(ChatRole.Assistant, JsonSerializer.Serialize(new Reply("ok")))],
            }));

        var proxy = new TemporalAIAgentProxy("TestAgent", fakeClient);
        var session = (TemporalAgentSession)await proxy.CreateSessionAsync();

        var result = await ((AIAgent)proxy).RunAsync<Reply>(
            [new ChatMessage(ChatRole.User, "q")],
            session,
            options: null,
            correlationId: "structured-corr");

        Assert.Equal("ok", result.Answer);
        Assert.NotNull(capturedRequest);
        Assert.Equal("structured-corr", capturedRequest!.CorrelationId);
    }
}
