using System.Text.Json;
using Temporalio.Extensions.Agents.Session;
using Temporalio.Extensions.Agents.Workflows;
using Xunit;

namespace Temporalio.Extensions.Agents.Tests;

/// <summary>
/// Tests that the StateBag round-trips correctly through the v0.3 durable-agent activity inputs
/// (<see cref="AgentStepInput"/>) — verifying GAP 6.
/// </summary>
public class StateBagPersistenceTests
{
    [Fact]
    public void AgentStepInput_WithNullStateBag_SerializesWithoutStateBagProperty()
    {
        var input = new AgentStepInput
        {
            AgentName = "Agent",
            Request = new RunRequest("hello"),
            AccumulatedMessages = new List<Microsoft.Extensions.AI.ChatMessage>(),
            SerializedStateBag = null,
        };

        var json = JsonSerializer.Serialize(input);
        using var doc = JsonDocument.Parse(json);

        Assert.False(doc.RootElement.TryGetProperty("serializedStateBag", out _));
    }

    [Fact]
    public void AgentStepInput_WithStateBag_RoundTrips()
    {
        var bagJson = JsonDocument.Parse("""{"userId":"user-001","threadId":"t-abc"}""").RootElement;
        var input = new AgentStepInput
        {
            AgentName = "Agent",
            Request = new RunRequest("test"),
            AccumulatedMessages = new List<Microsoft.Extensions.AI.ChatMessage>(),
            SerializedStateBag = bagJson,
        };

        var json = JsonSerializer.Serialize(input);
        var deserialized = JsonSerializer.Deserialize<AgentStepInput>(json);

        Assert.NotNull(deserialized);
        Assert.NotNull(deserialized!.SerializedStateBag);
        Assert.Equal(JsonValueKind.Object, deserialized.SerializedStateBag!.Value.ValueKind);
        Assert.Equal("user-001", deserialized.SerializedStateBag.Value.GetProperty("userId").GetString());
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

        Assert.Null(bag);
    }
}
