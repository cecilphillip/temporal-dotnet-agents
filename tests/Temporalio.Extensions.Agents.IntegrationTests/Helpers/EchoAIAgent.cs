using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Temporalio.Extensions.Agents.IntegrationTests.Helpers;

/// <summary>
/// A minimal <see cref="AIAgent"/> for integration testing.
/// Returns "Echo [{turnCount}]: {lastUserMessage}" without calling any real LLM.
/// The turn count is derived from the number of user messages in the conversation history,
/// which exercises the history-rebuild path in <see cref="AgentActivities"/>.
/// </summary>
internal sealed class EchoAIAgent : AIAgent
{
    private readonly string _name;

    public EchoAIAgent(string name)
    {
        _name = name;
    }

    public override string? Name => _name;

    protected override Task<AgentResponse> RunCoreAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session = null,
        AgentRunOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var messageList = messages.ToList();
        var userMessages = messageList.Where(m => m.Role == ChatRole.User).ToList();
        int turnCount = userMessages.Count;
        string lastMessage = userMessages.LastOrDefault()?.Text ?? "(empty)";

        var responseText = $"Echo [{turnCount}]: {lastMessage}";

        var response = new AgentResponse
        {
            Messages = [new ChatMessage(ChatRole.Assistant, responseText)],
            CreatedAt = DateTimeOffset.UtcNow
        };

        return Task.FromResult(response);
    }

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
        var sessionId = TemporalAgentSessionId.WithRandomKey(_name);
        return new ValueTask<AgentSession>(new TemporalAgentSession(sessionId));
    }

    protected override ValueTask<JsonElement> SerializeSessionCoreAsync(
        AgentSession session,
        JsonSerializerOptions? jsonSerializerOptions = null,
        CancellationToken cancellationToken = default)
    {
        // Serialize as the workflow ID string — mirrors what TemporalAgentSession.Serialize does internally.
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
}
