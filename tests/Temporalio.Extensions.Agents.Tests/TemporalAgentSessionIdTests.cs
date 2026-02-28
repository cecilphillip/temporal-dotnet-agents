using System.Text.Json;
using Xunit;

namespace Temporalio.Extensions.Agents.Tests;

public class TemporalAgentSessionIdTests
{
    [Fact]
    public void WithRandomKey_ProducesWorkflowIdWithPrefix()
    {
        var id = TemporalAgentSessionId.WithRandomKey("MyAgent");
        Assert.StartsWith("ta-myagent-", id.WorkflowId);
    }

    [Fact]
    public void WithRandomKey_ProducesUniqueKeys()
    {
        var id1 = TemporalAgentSessionId.WithRandomKey("MyAgent");
        var id2 = TemporalAgentSessionId.WithRandomKey("MyAgent");
        Assert.NotEqual(id1.Key, id2.Key);
        Assert.NotEqual(id1.WorkflowId, id2.WorkflowId);
    }

    [Fact]
    public void WithDeterministicKey_ProducesSameWorkflowId_ForSameInput()
    {
        var guid = Guid.NewGuid();
        var id1 = TemporalAgentSessionId.WithDeterministicKey("MyAgent", guid);
        var id2 = TemporalAgentSessionId.WithDeterministicKey("MyAgent", guid);
        Assert.Equal(id1.WorkflowId, id2.WorkflowId);
    }

    [Fact]
    public void WorkflowId_IsLowercaseAgentName()
    {
        var id = TemporalAgentSessionId.WithDeterministicKey("MyUpperCaseAgent", Guid.Empty);
        Assert.Contains("myuppercaseagent", id.WorkflowId);
        Assert.DoesNotContain("MyUpperCaseAgent", id.WorkflowId);
    }

    [Fact]
    public void Parse_RoundTrips_WorkflowId()
    {
        var original = TemporalAgentSessionId.WithRandomKey("TestAgent");
        var parsed = TemporalAgentSessionId.Parse(original.WorkflowId);
        Assert.Equal(original.WorkflowId, parsed.WorkflowId);
    }

    [Fact]
    public void Parse_PreservesAgentNameCaseInProperty()
    {
        // Parse always stores whatever was in the workflowId (which is lowercase)
        var parsed = TemporalAgentSessionId.Parse("ta-testagent-abc123");
        Assert.Equal("testagent", parsed.AgentName);
        Assert.Equal("abc123", parsed.Key);
    }

    [Fact]
    public void Parse_MissingPrefix_ThrowsFormatException()
    {
        Assert.Throws<FormatException>(() => TemporalAgentSessionId.Parse("agent-key"));
    }

    [Fact]
    public void Parse_OnlyAgentNameNoKey_ThrowsFormatException()
    {
        // "ta-myagent" has prefix but no key segment after second dash
        Assert.Throws<FormatException>(() => TemporalAgentSessionId.Parse("ta-myagent"));
    }

    [Fact]
    public void ImplicitConversion_ToString_ReturnsWorkflowId()
    {
        var id = TemporalAgentSessionId.WithDeterministicKey("Agent", Guid.Empty);
        string workflowId = id;
        Assert.Equal(id.WorkflowId, workflowId);
    }

    [Fact]
    public void ImplicitConversion_FromString_ParsesWorkflowId()
    {
        TemporalAgentSessionId id = "ta-myagent-abc123";
        Assert.Equal("myagent", id.AgentName);
        Assert.Equal("abc123", id.Key);
    }

    [Fact]
    public void Equality_CaseInsensitiveAgentName()
    {
        var id1 = new TemporalAgentSessionId("MyAgent", "key123");
        var id2 = new TemporalAgentSessionId("myagent", "key123");
        Assert.Equal(id1, id2);
        Assert.True(id1 == id2);
    }

    [Fact]
    public void Equality_DifferentKey_NotEqual()
    {
        var id1 = new TemporalAgentSessionId("MyAgent", "key1");
        var id2 = new TemporalAgentSessionId("MyAgent", "key2");
        Assert.NotEqual(id1, id2);
    }

    [Fact]
    public void ToString_ReturnsWorkflowId()
    {
        var id = new TemporalAgentSessionId("TestAgent", "mykey");
        Assert.Equal("ta-testagent-mykey", id.ToString());
    }

    [Fact]
    public void JsonSerializer_SerializesAsWorkflowId()
    {
        var id = new TemporalAgentSessionId("TestAgent", "mykey");
        string json = JsonSerializer.Serialize(id);
        // Serialized value should be the quoted workflow ID string
        Assert.Equal("\"ta-testagent-mykey\"", json);
    }

    [Fact]
    public void JsonSerializer_DeserializesFromWorkflowId()
    {
        var id = JsonSerializer.Deserialize<TemporalAgentSessionId>("\"ta-testagent-mykey\"");
        Assert.Equal("testagent", id.AgentName);
        Assert.Equal("mykey", id.Key);
    }

    [Fact]
    public void GetHashCode_SameForEqualIds()
    {
        var id1 = new TemporalAgentSessionId("MyAgent", "key");
        var id2 = new TemporalAgentSessionId("myagent", "key");
        Assert.Equal(id1.GetHashCode(), id2.GetHashCode());
    }

    // ─── Special character edge cases ────────────────────────────────────────

    [Fact]
    public void AgentNameWithDashes_ParsesCorrectly()
    {
        // Agent name "my-agent" produces "ta-my-agent-{key}".
        // Parse uses LastIndexOf('-') so it splits on the dash before the key.
        var id = new TemporalAgentSessionId("my-agent", "abc123");
        Assert.Equal("ta-my-agent-abc123", id.WorkflowId);

        var parsed = TemporalAgentSessionId.Parse(id.WorkflowId);
        Assert.Equal("my-agent", parsed.AgentName);
        Assert.Equal("abc123", parsed.Key);
    }

    [Fact]
    public void AgentNameWithUnderscores_ParsesCorrectly()
    {
        var id = new TemporalAgentSessionId("my_agent_v2", "key42");
        Assert.Equal("ta-my_agent_v2-key42", id.WorkflowId);

        var parsed = TemporalAgentSessionId.Parse(id.WorkflowId);
        Assert.Equal("my_agent_v2", parsed.AgentName);
        Assert.Equal("key42", parsed.Key);
    }

    [Fact]
    public void KeyWithDashes_ParseUsesLastDashAsDelimiter()
    {
        // When the key contains dashes, Parse splits on the LAST dash.
        // This means "ta-myagent-abc-123" parses as agent="myagent-abc" key="123".
        // This is a documented trade-off: keys with dashes shift the agent name.
        var parsed = TemporalAgentSessionId.Parse("ta-myagent-abc-123");
        Assert.Equal("myagent-abc", parsed.AgentName);
        Assert.Equal("123", parsed.Key);
    }

    [Fact]
    public void SessionId_InHashSet_DeduplicatesCorrectly()
    {
        var id1 = new TemporalAgentSessionId("Agent", "key1");
        var id2 = new TemporalAgentSessionId("AGENT", "key1"); // same, different case
        var id3 = new TemporalAgentSessionId("Agent", "key2"); // different key

        var set = new HashSet<TemporalAgentSessionId> { id1, id2, id3 };

        // id1 and id2 are equal (case-insensitive) → set contains 2 elements.
        Assert.Equal(2, set.Count);
        Assert.Contains(id1, set);
        Assert.Contains(id3, set);
    }

    [Fact]
    public void LongKey_ConstructsAndParsesWithoutTruncation()
    {
        var longKey = new string('x', 200);
        var id = new TemporalAgentSessionId("Agent", longKey);
        Assert.Equal($"ta-agent-{longKey}", id.WorkflowId);

        var parsed = TemporalAgentSessionId.Parse(id.WorkflowId);
        Assert.Equal(longKey, parsed.Key);
    }
}
