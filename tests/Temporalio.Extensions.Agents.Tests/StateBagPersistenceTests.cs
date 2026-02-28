using System.Text.Json;
using Xunit;

namespace Temporalio.Extensions.Agents.Tests;

/// <summary>
/// Tests that the StateBag round-trips correctly through <see cref="ExecuteAgentInput"/>
/// and <see cref="ExecuteAgentResult"/> serialization — verifying GAP 6.
/// </summary>
public class StateBagPersistenceTests
{
    [Fact]
    public void ExecuteAgentInput_WithNullStateBag_SerializesWithoutStateBagProperty()
    {
        var sessionId = new TemporalAgentSessionId("Agent", "key123");
        var request = new RunRequest("hello");
        var input = new ExecuteAgentInput("Agent", request, [], null);

        // Serialize via System.Text.Json
        var json = JsonSerializer.Serialize(input);
        using var doc = JsonDocument.Parse(json);

        // SerializedStateBag should be omitted (JsonIgnore WhenWritingNull)
        Assert.False(doc.RootElement.TryGetProperty("serializedStateBag", out _));
    }

    [Fact]
    public void ExecuteAgentInput_WithStateBag_RoundTrips()
    {
        var bagJson = JsonDocument.Parse("""{"userId":"user-001","threadId":"t-abc"}""").RootElement;
        var input = new ExecuteAgentInput("Agent", new RunRequest("test"), [], bagJson);

        var json = JsonSerializer.Serialize(input);
        var deserialized = JsonSerializer.Deserialize<ExecuteAgentInput>(json);

        Assert.NotNull(deserialized);
        Assert.NotNull(deserialized.SerializedStateBag);
        Assert.Equal(JsonValueKind.Object, deserialized.SerializedStateBag.Value.ValueKind);
        Assert.Equal("user-001", deserialized.SerializedStateBag.Value.GetProperty("userId").GetString());
    }

    [Fact]
    public void ExecuteAgentResult_WithNullStateBag_Serializes()
    {
        var response = new Microsoft.Agents.AI.AgentResponse
        {
            Messages = [new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.Assistant, "hi")],
            CreatedAt = DateTimeOffset.UtcNow
        };
        var result = new ExecuteAgentResult(response, null);

        var json = JsonSerializer.Serialize(result);
        using var doc = JsonDocument.Parse(json);

        Assert.False(doc.RootElement.TryGetProperty("serializedStateBag", out _));
    }

    [Fact]
    public void ExecuteAgentResult_WithStateBag_RoundTrips()
    {
        var bagJson = JsonDocument.Parse("""{"key":"val"}""").RootElement;
        var response = new Microsoft.Agents.AI.AgentResponse
        {
            Messages = [new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.Assistant, "hi")],
            CreatedAt = DateTimeOffset.UtcNow
        };
        var result = new ExecuteAgentResult(response, bagJson);

        var json = JsonSerializer.Serialize(result);
        var deserialized = JsonSerializer.Deserialize<ExecuteAgentResult>(json);

        Assert.NotNull(deserialized);
        Assert.NotNull(deserialized.SerializedStateBag);
        Assert.Equal("val", deserialized.SerializedStateBag.Value.GetProperty("key").GetString());
    }

    [Fact]
    public void TemporalAgentSession_FromStateBag_WithNull_ReturnsEmptySession()
    {
        var sessionId = new TemporalAgentSessionId("Agent", "abc");
        var session = TemporalAgentSession.FromStateBag(sessionId, null);

        Assert.NotNull(session);
        Assert.Equal(sessionId, session.SessionId);
    }

    [Fact]
    public void TemporalAgentSession_SerializeStateBag_EmptyBag_ReturnsNull()
    {
        var sessionId = new TemporalAgentSessionId("Agent", "abc");
        var session = new TemporalAgentSession(sessionId);

        var bag = session.SerializeStateBag();

        // An empty StateBag should serialize as null (no properties to persist).
        Assert.Null(bag);
    }
}
