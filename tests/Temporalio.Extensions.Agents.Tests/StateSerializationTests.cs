using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Temporalio.Extensions.Agents.State;
using Temporalio.Extensions.Agents.Workflows;
using Xunit;

namespace Temporalio.Extensions.Agents.Tests;

/// <summary>
/// Tests for the internal state serialization hierarchy used to persist conversation
/// history across the workflow→activity boundary.
/// </summary>
public class StateSerializationTests
{
    private static readonly JsonSerializerOptions s_opts = TemporalAgentJsonUtilities.DefaultOptions;

    // ─── TemporalAgentStateRequest ───────────────────────────────────────────

    [Fact]
    public void FromRunRequest_PreservesCorrelationId()
    {
        var request = new RunRequest("Hello") { CorrelationId = "test-corr" };
        var stateRequest = TemporalAgentStateRequest.FromRunRequest(request, DateTimeOffset.UtcNow);
        Assert.Equal(request.CorrelationId, stateRequest.CorrelationId);
    }

    [Fact]
    public void FromRunRequest_PreservesMessageRole()
    {
        var request = new RunRequest("Hello", role: ChatRole.User) { CorrelationId = "test-corr" };
        var stateRequest = TemporalAgentStateRequest.FromRunRequest(request, DateTimeOffset.UtcNow);
        Assert.Single(stateRequest.Messages);
        Assert.Equal(ChatRole.User, stateRequest.Messages[0].Role);
    }

    [Fact]
    public void FromRunRequest_WithJsonFormat_SetsResponseType_Json()
    {
        var request = new RunRequest("q", responseFormat: ChatResponseFormat.Json) { CorrelationId = "test-corr" };
        var stateRequest = TemporalAgentStateRequest.FromRunRequest(request, DateTimeOffset.UtcNow);
        Assert.Equal("json", stateRequest.ResponseType);
    }

    // ─── Polymorphic type discriminators ────────────────────────────────────

    [Fact]
    public void Request_JsonContains_TypeDiscriminator_Request()
    {
        var request = new RunRequest("Hello") { CorrelationId = "test-corr" };
        var stateRequest = TemporalAgentStateRequest.FromRunRequest(request, DateTimeOffset.UtcNow);
        var json = JsonSerializer.Serialize<TemporalAgentStateEntry>(stateRequest, s_opts);
        // Whitespace-tolerant: AIJsonUtilities.DefaultOptions enables WriteIndented.
        Assert.Contains("\"$type\"", json);
        Assert.Contains("\"request\"", json);
    }

    [Fact]
    public void Response_JsonContains_TypeDiscriminator_Response()
    {
        var agentResponse = new AgentResponse
        {
            Messages = [new ChatMessage(ChatRole.Assistant, "Hi")],
            CreatedAt = DateTimeOffset.UtcNow
        };
        var stateResponse = TemporalAgentStateResponse.FromResponse("corr-1", agentResponse, DateTimeOffset.UtcNow);
        var json = JsonSerializer.Serialize<TemporalAgentStateEntry>(stateResponse, s_opts);
        // Whitespace-tolerant: AIJsonUtilities.DefaultOptions enables WriteIndented.
        Assert.Contains("\"$type\"", json);
        Assert.Contains("\"response\"", json);
    }

    // ─── TemporalAgentStateResponse ──────────────────────────────────────────

    [Fact]
    public void Response_ToResponse_PreservesMessageRole()
    {
        var agentResponse = new AgentResponse
        {
            Messages = [new ChatMessage(ChatRole.Assistant, "Hi there")],
            CreatedAt = DateTimeOffset.UtcNow
        };
        var stateResponse = TemporalAgentStateResponse.FromResponse("corr-1", agentResponse, DateTimeOffset.UtcNow);
        var roundTripped = stateResponse.ToResponse();

        Assert.Single(roundTripped.Messages);
        Assert.Equal(ChatRole.Assistant, roundTripped.Messages[0].Role);
        Assert.Equal("Hi there", roundTripped.Messages[0].Text);
    }

    [Fact]
    public void Response_JsonRoundTrip_PreservesMessages()
    {
        var agentResponse = new AgentResponse
        {
            Messages = [new ChatMessage(ChatRole.Assistant, "Round-trip me")],
            CreatedAt = DateTimeOffset.UtcNow
        };
        var stateResponse = TemporalAgentStateResponse.FromResponse("corr-1", agentResponse, DateTimeOffset.UtcNow);

        var json = JsonSerializer.Serialize<TemporalAgentStateEntry>(stateResponse, s_opts);
        var deserialized = JsonSerializer.Deserialize<TemporalAgentStateEntry>(json, s_opts) as TemporalAgentStateResponse;

        Assert.NotNull(deserialized);
        Assert.Single(deserialized.Messages);
        Assert.Equal("Round-trip me", deserialized.Messages[0].Contents
            .OfType<TextContent>()
            .FirstOrDefault()?.Text);
    }

    // ─── Entry list serialization ─────────────────────────────────────────

    [Fact]
    public void EntryList_JsonRoundTrip_PreservesPolymorphism()
    {
        var request = new RunRequest("q") { CorrelationId = "test-corr" };
        var agentResponse = new AgentResponse
        {
            Messages = [new ChatMessage(ChatRole.Assistant, "a")],
            CreatedAt = DateTimeOffset.UtcNow
        };

        var entries = new List<TemporalAgentStateEntry>
        {
            TemporalAgentStateRequest.FromRunRequest(request, DateTimeOffset.UtcNow),
            TemporalAgentStateResponse.FromResponse(request.CorrelationId!, agentResponse, DateTimeOffset.UtcNow)
        };

        var json = JsonSerializer.Serialize<IReadOnlyList<TemporalAgentStateEntry>>(entries, s_opts);
        var deserialized = JsonSerializer.Deserialize<IReadOnlyList<TemporalAgentStateEntry>>(json, s_opts);

        Assert.NotNull(deserialized);
        Assert.Equal(2, deserialized.Count);
        Assert.IsType<TemporalAgentStateRequest>(deserialized[0]);
        Assert.IsType<TemporalAgentStateResponse>(deserialized[1]);
    }
}
