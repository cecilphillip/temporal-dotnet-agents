using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Temporalio.Extensions.AI;
using Temporalio.Extensions.Agents.State;
using Temporalio.Extensions.Agents.Workflows;
using Xunit;

namespace Temporalio.Extensions.Agents.Tests;

/// <summary>
/// Tests for the session-history serialization hierarchy used to persist conversation
/// history across the workflow→activity boundary. The MAF library extends the AI library's
/// <see cref="DurableSessionEntry"/> hierarchy with <see cref="AgentSessionRequest"/> and
/// <see cref="AgentSessionResponse"/>; the runtime polymorphism modifier in
/// <c>TemporalAgentJsonUtilities</c> registers the <c>"agent_request"</c> /
/// <c>"agent_response"</c> discriminators so MAF entries round-trip with their
/// agent-specific fields intact.
/// </summary>
public class StateSerializationTests
{
    private static readonly JsonSerializerOptions s_opts = TemporalAgentJsonUtilities.DefaultOptions;

    // ─── AgentSessionRequest ─────────────────────────────────────────────────

    [Fact]
    public void FromRunRequest_PreservesCorrelationId()
    {
        var request = new RunRequest("Hello") { CorrelationId = "test-corr" };
        var stateRequest = AgentSessionRequest.FromRunRequest(request, DateTimeOffset.UtcNow);
        Assert.Equal(request.CorrelationId, stateRequest.CorrelationId);
    }

    [Fact]
    public void FromRunRequest_PreservesMessageRole()
    {
        var request = new RunRequest("Hello", role: ChatRole.User) { CorrelationId = "test-corr" };
        var stateRequest = AgentSessionRequest.FromRunRequest(request, DateTimeOffset.UtcNow);
        Assert.Single(stateRequest.Messages);
        Assert.Equal(ChatRole.User, stateRequest.Messages[0].Role);
    }

    [Fact]
    public void FromRunRequest_WithJsonFormat_SetsResponseType_Json()
    {
        var request = new RunRequest("q", responseFormat: ChatResponseFormat.Json) { CorrelationId = "test-corr" };
        var stateRequest = AgentSessionRequest.FromRunRequest(request, DateTimeOffset.UtcNow);
        Assert.Equal("json", stateRequest.ResponseType);
    }

    [Fact]
    public void FromRunRequest_PropagatesOrchestrationId()
    {
        var request = new RunRequest("Hello")
        {
            CorrelationId = "test-corr",
            OrchestrationId = "wf-123",
        };
        var stateRequest = AgentSessionRequest.FromRunRequest(request, DateTimeOffset.UtcNow);
        Assert.Equal("wf-123", stateRequest.OrchestrationId);
    }

    // ─── Polymorphic type discriminators ────────────────────────────────────

    [Fact]
    public void Request_JsonContains_TypeDiscriminator_AgentRequest()
    {
        var request = new RunRequest("Hello") { CorrelationId = "test-corr" };
        var stateRequest = AgentSessionRequest.FromRunRequest(request, DateTimeOffset.UtcNow);
        var json = JsonSerializer.Serialize<DurableSessionEntry>(stateRequest, s_opts);
        Assert.Contains("\"$type\"", json);
        Assert.Contains("\"agent_request\"", json);
    }

    [Fact]
    public void Response_JsonContains_TypeDiscriminator_AgentResponse()
    {
        var agentResponse = new AgentResponse
        {
            Messages = [new ChatMessage(ChatRole.Assistant, "Hi")],
            CreatedAt = DateTimeOffset.UtcNow,
        };
        var stateResponse = AgentSessionResponse.FromAgentResponse("corr-1", agentResponse, DateTimeOffset.UtcNow);
        var json = JsonSerializer.Serialize<DurableSessionEntry>(stateResponse, s_opts);
        Assert.Contains("\"$type\"", json);
        Assert.Contains("\"agent_response\"", json);
    }

    // ─── AgentSessionResponse ────────────────────────────────────────────────

    [Fact]
    public void Response_ToResponse_PreservesMessageRole()
    {
        var agentResponse = new AgentResponse
        {
            Messages = [new ChatMessage(ChatRole.Assistant, "Hi there")],
            CreatedAt = DateTimeOffset.UtcNow,
        };
        var stateResponse = AgentSessionResponse.FromAgentResponse("corr-1", agentResponse, DateTimeOffset.UtcNow);
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
            CreatedAt = DateTimeOffset.UtcNow,
        };
        var stateResponse = AgentSessionResponse.FromAgentResponse("corr-1", agentResponse, DateTimeOffset.UtcNow);

        var json = JsonSerializer.Serialize<DurableSessionEntry>(stateResponse, s_opts);
        var deserialized = JsonSerializer.Deserialize<DurableSessionEntry>(json, s_opts) as AgentSessionResponse;

        Assert.NotNull(deserialized);
        Assert.Single(deserialized.Messages);
        Assert.Equal("Round-trip me", deserialized.Messages[0].Contents
            .OfType<TextContent>()
            .FirstOrDefault()?.Text);
    }

    [Fact]
    public void Response_JsonRoundTrip_PreservesUsage()
    {
        var agentResponse = new AgentResponse
        {
            Messages = [new ChatMessage(ChatRole.Assistant, "Hi")],
            CreatedAt = DateTimeOffset.UtcNow,
            Usage = new UsageDetails
            {
                InputTokenCount = 10,
                OutputTokenCount = 5,
                TotalTokenCount = 15,
            },
        };
        var stateResponse = AgentSessionResponse.FromAgentResponse("corr-1", agentResponse, DateTimeOffset.UtcNow);

        var json = JsonSerializer.Serialize<DurableSessionEntry>(stateResponse, s_opts);
        var deserialized = JsonSerializer.Deserialize<DurableSessionEntry>(json, s_opts) as AgentSessionResponse;

        Assert.NotNull(deserialized);
        Assert.NotNull(deserialized.Usage);
        Assert.Equal(10, deserialized.Usage.InputTokenCount);
        Assert.Equal(5, deserialized.Usage.OutputTokenCount);
        Assert.Equal(15, deserialized.Usage.TotalTokenCount);
    }

    // ─── Entry list serialization ────────────────────────────────────────────

    [Fact]
    public void EntryList_JsonRoundTrip_PreservesPolymorphism()
    {
        var request = new RunRequest("q") { CorrelationId = "test-corr" };
        var agentResponse = new AgentResponse
        {
            Messages = [new ChatMessage(ChatRole.Assistant, "a")],
            CreatedAt = DateTimeOffset.UtcNow,
        };

        var entries = new List<DurableSessionEntry>
        {
            AgentSessionRequest.FromRunRequest(request, DateTimeOffset.UtcNow),
            AgentSessionResponse.FromAgentResponse(request.CorrelationId!, agentResponse, DateTimeOffset.UtcNow),
        };

        var json = JsonSerializer.Serialize<IReadOnlyList<DurableSessionEntry>>(entries, s_opts);
        var deserialized = JsonSerializer.Deserialize<IReadOnlyList<DurableSessionEntry>>(json, s_opts);

        Assert.NotNull(deserialized);
        Assert.Equal(2, deserialized.Count);
        Assert.IsType<AgentSessionRequest>(deserialized[0]);
        Assert.IsType<AgentSessionResponse>(deserialized[1]);
    }

    // ─── Runtime polymorphism modifier ───────────────────────────────────────

    [Fact]
    public void DefaultOptions_RegistersAgentDerivedTypes_OnDurableSessionEntry()
    {
        // Verify that TemporalAgentJsonUtilities.DefaultOptions correctly registers the
        // MAF subclasses on DurableSessionEntry's polymorphism options at runtime.
        var typeInfo = s_opts.GetTypeInfo(typeof(DurableSessionEntry));

        Assert.NotNull(typeInfo.PolymorphismOptions);

        var derivedTypes = typeInfo.PolymorphismOptions.DerivedTypes;
        Assert.Contains(derivedTypes,
            d => d.DerivedType == typeof(AgentSessionRequest)
                && string.Equals(d.TypeDiscriminator?.ToString(), "agent_request", StringComparison.Ordinal));
        Assert.Contains(derivedTypes,
            d => d.DerivedType == typeof(AgentSessionResponse)
                && string.Equals(d.TypeDiscriminator?.ToString(), "agent_response", StringComparison.Ordinal));
    }
}
