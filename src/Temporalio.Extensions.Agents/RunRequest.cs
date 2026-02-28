using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;

namespace Temporalio.Extensions.Agents;

/// <summary>
/// Represents a request to run an agent with a specific message and configuration.
/// </summary>
public record RunRequest
{
    /// <summary>Gets the list of chat messages to send to the agent.</summary>
    public IList<ChatMessage> Messages { get; init; } = [];

    /// <summary>Gets the optional response format for the agent's response.</summary>
    public ChatResponseFormat? ResponseFormat { get; init; }

    /// <summary>Gets whether to enable tool calls. Defaults to <c>true</c>.</summary>
    public bool EnableToolCalls { get; init; } = true;

    /// <summary>Gets the tool names to enable. If <see langword="null"/>, all tools are enabled.</summary>
    public IList<string>? EnableToolNames { get; init; }

    /// <summary>Gets the correlation ID used to match this request to its response.</summary>
    [JsonInclude]
    internal string CorrelationId { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>Gets the ID of the orchestration or workflow that initiated this request (if any).</summary>
    [JsonInclude]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    internal string? OrchestrationId { get; set; }

    /// <summary>Initializes a new <see cref="RunRequest"/> for a single text message.</summary>
    public RunRequest(
        string message,
        ChatRole? role = null,
        ChatResponseFormat? responseFormat = null,
        bool enableToolCalls = true,
        IList<string>? enableToolNames = null)
        : this(
            [new ChatMessage(role ?? ChatRole.User, message) { CreatedAt = DateTimeOffset.UtcNow }],
            responseFormat,
            enableToolCalls,
            enableToolNames)
    {
    }

    /// <summary>Initializes a new <see cref="RunRequest"/> for multiple messages.</summary>
    [JsonConstructor]
    public RunRequest(
        IList<ChatMessage> messages,
        ChatResponseFormat? responseFormat = null,
        bool enableToolCalls = true,
        IList<string>? enableToolNames = null)
    {
        this.Messages = messages;
        this.ResponseFormat = responseFormat;
        this.EnableToolCalls = enableToolCalls;
        this.EnableToolNames = enableToolNames;
    }
}
