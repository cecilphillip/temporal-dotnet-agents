using Temporalio.Extensions.Agents.State;
using Xunit;
using Xunit.Abstractions;

namespace Temporalio.Extensions.Agents.IntegrationTests;

/// <summary>
/// Tests for the <see cref="AgentWorkflow.GetHistory"/> workflow query, which allows
/// external callers to inspect the conversation state of a running agent session.
/// </summary>
[Trait("Category", "Integration")]
public class WorkflowQueryTests : IClassFixture<IntegrationTestFixture>
{
    private readonly IntegrationTestFixture _fixture;
    private readonly ITestOutputHelper _output;

    public WorkflowQueryTests(IntegrationTestFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    [Fact]
    public async Task QueryHistory_ReturnCorrectEntryCount()
    {
        var session = (TemporalAgentSession)await _fixture.AgentProxy.CreateSessionAsync();

        // Send 2 turns to build up history.
        await _fixture.AgentProxy.RunAsync("First message", session);
        await _fixture.AgentProxy.RunAsync("Second message", session);

        // Query the workflow's internal history.
        var handle = _fixture.Client.GetWorkflowHandle<AgentWorkflow>(
            session.SessionId.WorkflowId);
        var history = await handle.QueryAsync(wf => wf.GetHistory());

        // Each turn produces a request + response entry → 2 turns = 4 entries.
        Assert.Equal(4, history.Count);

        // Entries alternate: request, response, request, response.
        Assert.IsType<TemporalAgentStateRequest>(history[0]);
        Assert.IsType<TemporalAgentStateResponse>(history[1]);
        Assert.IsType<TemporalAgentStateRequest>(history[2]);
        Assert.IsType<TemporalAgentStateResponse>(history[3]);

        _output.WriteLine(
            $"Query returned {history.Count} entries for 2 turns — correct alternating pattern.");
    }

    [Fact]
    public async Task QueryHistory_ContainsCorrectMessageContent()
    {
        var session = (TemporalAgentSession)await _fixture.AgentProxy.CreateSessionAsync();

        await _fixture.AgentProxy.RunAsync("What is Temporal?", session);

        var handle = _fixture.Client.GetWorkflowHandle<AgentWorkflow>(
            session.SessionId.WorkflowId);
        var history = await handle.QueryAsync(wf => wf.GetHistory());

        Assert.Equal(2, history.Count);

        // First entry is the request — should contain the user message.
        var request = Assert.IsType<TemporalAgentStateRequest>(history[0]);
        Assert.NotEmpty(request.Messages);

        var requestMsg = request.Messages[0];
        Assert.Equal("user", requestMsg.Role);
        var requestText = requestMsg.Contents
            .OfType<TemporalAgentStateTextContent>()
            .FirstOrDefault();
        Assert.NotNull(requestText);
        Assert.Equal("What is Temporal?", requestText.Text);

        // Second entry is the response — should contain the echo reply.
        var response = Assert.IsType<TemporalAgentStateResponse>(history[1]);
        Assert.NotEmpty(response.Messages);

        var responseMsg = response.Messages[0];
        Assert.Equal("assistant", responseMsg.Role);
        var responseText = responseMsg.Contents
            .OfType<TemporalAgentStateTextContent>()
            .FirstOrDefault();
        Assert.NotNull(responseText);
        Assert.Contains("Echo [1]: What is Temporal?", responseText.Text);

        _output.WriteLine(
            $"Request text: \"{requestText.Text}\" | Response text: \"{responseText.Text}\"");
    }

    [Fact]
    public async Task QueryHistory_CorrelationIdsMatch()
    {
        var session = (TemporalAgentSession)await _fixture.AgentProxy.CreateSessionAsync();

        await _fixture.AgentProxy.RunAsync("Correlated message", session);

        var handle = _fixture.Client.GetWorkflowHandle<AgentWorkflow>(
            session.SessionId.WorkflowId);
        var history = await handle.QueryAsync(wf => wf.GetHistory());

        Assert.Equal(2, history.Count);

        // Request and response for the same turn share the same CorrelationId.
        Assert.Equal(history[0].CorrelationId, history[1].CorrelationId);
        Assert.False(string.IsNullOrEmpty(history[0].CorrelationId));

        _output.WriteLine($"CorrelationId: {history[0].CorrelationId}");
    }
}
