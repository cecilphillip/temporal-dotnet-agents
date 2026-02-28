using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Temporalio.Extensions.Agents.IntegrationTests.Helpers;

/// <summary>
/// Base class for integration test agents. Provides session management, serialization,
/// and a streaming-to-sync adapter so subclasses only need to override <see cref="RunCoreAsync"/>.
/// </summary>
internal abstract class TestAgentBase : AIAgent
{
    protected TestAgentBase(string name) => AgentName = name;

    protected string AgentName { get; }

    public override string? Name => AgentName;

    protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session = null,
        AgentRunOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await RunCoreAsync(messages, session, options, cancellationToken);
        foreach (var update in response.ToAgentResponseUpdates())
        {
            yield return update;
        }
    }

    protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken = default)
    {
        var sessionId = TemporalAgentSessionId.WithRandomKey(AgentName);
        return new ValueTask<AgentSession>(new TemporalAgentSession(sessionId));
    }

    protected override ValueTask<JsonElement> SerializeSessionCoreAsync(
        AgentSession session,
        JsonSerializerOptions? jsonSerializerOptions = null,
        CancellationToken cancellationToken = default)
    {
        var workflowId = session.ToString()
            ?? throw new InvalidOperationException("Session.ToString() returned null.");
        var json = JsonSerializer.SerializeToElement(workflowId, jsonSerializerOptions);
        return new ValueTask<JsonElement>(json);
    }

    protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
        JsonElement serializedState,
        JsonSerializerOptions? jsonSerializerOptions = null,
        CancellationToken cancellationToken = default)
    {
        var workflowId = serializedState.GetString()
            ?? throw new InvalidOperationException("Serialized state is not a string.");
        var sessionId = TemporalAgentSessionId.Parse(workflowId);
        return new ValueTask<AgentSession>(new TemporalAgentSession(sessionId));
    }

    /// <summary>
    /// Creates a standard echo-style response from the message history.
    /// Response text: "Echo [{turnCount}]: {lastUserMessage}".
    /// </summary>
    protected static AgentResponse CreateEchoResponse(IEnumerable<ChatMessage> messages)
    {
        var userMessages = messages.Where(m => m.Role == ChatRole.User).ToList();
        int turnCount = userMessages.Count;
        string lastMessage = userMessages.LastOrDefault()?.Text ?? "(empty)";

        return new AgentResponse
        {
            Messages = [new ChatMessage(ChatRole.Assistant, $"Echo [{turnCount}]: {lastMessage}")],
            CreatedAt = DateTimeOffset.UtcNow
        };
    }
}
