using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Temporalio.Extensions.Agents.Tests.Helpers;

/// <summary>
/// A minimal concrete <see cref="AIAgent"/> for use in unit tests.
/// </summary>
internal sealed class StubAIAgent(string? name, AgentResponse? fixedResponse = null) : AIAgent
{
    private readonly AgentResponse _fixedResponse = fixedResponse ?? new AgentResponse();

    public override string? Name { get; } = name;

    protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken = default) =>
        new(new TemporalAgentSession(TemporalAgentSessionId.WithRandomKey(Name ?? "stub")));

    protected override ValueTask<JsonElement> SerializeSessionCoreAsync(
        AgentSession session,
        JsonSerializerOptions? jsonSerializerOptions = null,
        CancellationToken cancellationToken = default) =>
        throw new NotImplementedException();

    protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
        JsonElement serializedState,
        JsonSerializerOptions? jsonSerializerOptions = null,
        CancellationToken cancellationToken = default) =>
        throw new NotImplementedException();

    protected override Task<AgentResponse> RunCoreAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session = null,
        AgentRunOptions? options = null,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(_fixedResponse);

    protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session = null,
        AgentRunOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Emit a single empty update for streaming tests
        yield return new AgentResponseUpdate();
        await Task.CompletedTask;
    }
}
