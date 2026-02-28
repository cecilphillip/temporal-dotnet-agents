using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;

namespace Temporalio.Extensions.Agents.State;

internal sealed class TemporalAgentStateMessage
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
        return new TemporalAgentStateMessage
        {
            CreatedAt = message.CreatedAt,
            AuthorName = message.AuthorName,
            Role = message.Role.ToString(),
            Contents = message.Contents.Select(TemporalAgentStateContent.FromAIContent).ToList()
        };
    }

    public ChatMessage ToChatMessage()
    {
        return new ChatMessage
        {
            CreatedAt = this.CreatedAt,
            AuthorName = this.AuthorName,
            Contents = this.Contents.Select(c => c.ToAIContent()).ToList(),
            Role = new ChatRole(this.Role)
        };
    }
}
