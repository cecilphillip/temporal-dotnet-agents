using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;

namespace Temporalio.Extensions.Agents.State;

/// <summary>Wire shape for agent conversation history entries. Additive changes only.</summary>
public sealed class TemporalAgentStateMessage
{
    [JsonPropertyName("authorName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AuthorName { get; init; }

    [JsonPropertyName("createdAt")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? CreatedAt { get; init; }

    [JsonPropertyName("contents")]
    public IReadOnlyList<TemporalAgentStateContent> Contents { get; init; } = [];

    [JsonPropertyName("role")]
    public required string Role { get; init; }

    [JsonExtensionData]
    public IDictionary<string, JsonElement>? ExtensionData { get; set; }

    public static TemporalAgentStateMessage FromChatMessage(ChatMessage message)
    {
        var contents = new List<TemporalAgentStateContent>(message.Contents.Count);
        foreach (var c in message.Contents)
            contents.Add(TemporalAgentStateContent.FromAIContent(c));

        return new TemporalAgentStateMessage
        {
            CreatedAt = message.CreatedAt,
            AuthorName = message.AuthorName,
            Role = message.Role.ToString(),
            Contents = contents
        };
    }

    public ChatMessage ToChatMessage()
    {
        var contents = new List<AIContent>(this.Contents.Count);
        foreach (var c in this.Contents)
            contents.Add(c.ToAIContent());

        return new ChatMessage
        {
            CreatedAt = this.CreatedAt,
            AuthorName = this.AuthorName,
            Contents = contents,
            Role = new ChatRole(this.Role)
        };
    }
}
