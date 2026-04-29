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
        Assert.Equal(ChatRole.User.Value, stateRequest.Messages[0].Role);
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
        Assert.Contains("\"$type\":\"request\"", json);
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
        Assert.Contains("\"$type\":\"response\"", json);
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
            .OfType<TemporalAgentStateTextContent>()
            .FirstOrDefault()?.Text);
    }

    // ─── Content type round-trips ─────────────────────────────────────────

    [Fact]
    public void TextContent_RoundTrips_Via_ToAIContent()
    {
        var original = new TextContent("Hello from text");
        var stateContent = TemporalAgentStateContent.FromAIContent(original);

        Assert.IsType<TemporalAgentStateTextContent>(stateContent);
        var roundTripped = stateContent.ToAIContent() as TextContent;
        Assert.NotNull(roundTripped);
        Assert.Equal(original.Text, roundTripped.Text);
    }

    [Fact]
    public void TextContent_JsonDiscriminator_IsText()
    {
        var content = TemporalAgentStateContent.FromAIContent(new TextContent("hi"));
        var json = JsonSerializer.Serialize<TemporalAgentStateContent>(content, s_opts);
        Assert.Contains("\"$type\":\"text\"", json);
    }

    [Fact]
    public void FunctionCallContent_RoundTrips_Via_ToAIContent()
    {
        var original = new FunctionCallContent(
            callId: "call-1",
            name: "MyFunc",
            arguments: new Dictionary<string, object?> { ["arg1"] = "value1" });

        var stateContent = TemporalAgentStateContent.FromAIContent(original);
        Assert.IsType<TemporalAgentStateFunctionCallContent>(stateContent);

        var roundTripped = stateContent.ToAIContent() as FunctionCallContent;
        Assert.NotNull(roundTripped);
        Assert.Equal(original.CallId, roundTripped.CallId);
        Assert.Equal(original.Name, roundTripped.Name);
    }

    [Fact]
    public void FunctionCallContent_JsonDiscriminator_IsFunctionCall()
    {
        var content = TemporalAgentStateContent.FromAIContent(
            new FunctionCallContent("id", "fn", null));
        var json = JsonSerializer.Serialize<TemporalAgentStateContent>(content, s_opts);
        Assert.Contains("\"$type\":\"functionCall\"", json);
    }

    [Fact]
    public void ErrorContent_RoundTrips_Via_ToAIContent()
    {
        var original = new ErrorContent("Something went wrong");
        var stateContent = TemporalAgentStateContent.FromAIContent(original);
        Assert.IsType<TemporalAgentStateErrorContent>(stateContent);

        var roundTripped = stateContent.ToAIContent() as ErrorContent;
        Assert.NotNull(roundTripped);
        Assert.Equal(original.Message, roundTripped.Message);
    }

    [Fact]
    public void ErrorContent_JsonDiscriminator_IsError()
    {
        var content = TemporalAgentStateContent.FromAIContent(new ErrorContent("err"));
        var json = JsonSerializer.Serialize<TemporalAgentStateContent>(content, s_opts);
        Assert.Contains("\"$type\":\"error\"", json);
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
