using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Temporalio.Extensions.Agents.Session;
using Temporalio.Extensions.Agents.Workflows;

namespace Temporalio.Extensions.Agents;

/// <summary>
/// An <see cref="AIAgent"/> for use outside of Temporal workflows (e.g., HTTP handlers, console apps).
/// Delegates to <see cref="ITemporalAgentClient"/> which communicates with <see cref="AgentWorkflow"/>
/// via Temporal workflow updates — no polling needed.
/// </summary>
internal class TemporalAIAgentProxy(
    string name,
    ITemporalAgentClient agentClient,
    ILogger<TemporalAIAgentProxy>? logger = null) : AIAgent
{
    private readonly ITemporalAgentClient _agentClient = agentClient;
    private readonly ILogger<TemporalAIAgentProxy> _logger =
        logger ?? NullLogger<TemporalAIAgentProxy>.Instance;

    public override string? Name { get; } = name;

    protected override ValueTask<JsonElement> SerializeSessionCoreAsync(
        AgentSession session,
        JsonSerializerOptions? jsonSerializerOptions = null,
        CancellationToken cancellationToken = default)
    {
        if (session is null)
        {
            throw new ArgumentNullException(nameof(session));
        }

        if (session is not TemporalAgentSession temporalSession)
        {
            throw new InvalidOperationException(
                $"Expected a {nameof(TemporalAgentSession)} but got '{session.GetType().Name}'.");
        }

        return new ValueTask<JsonElement>(temporalSession.Serialize(jsonSerializerOptions));
    }

    protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
        JsonElement serializedState,
        JsonSerializerOptions? jsonSerializerOptions = null,
        CancellationToken cancellationToken = default)
    {
        return new ValueTask<AgentSession>(TemporalAgentSession.Deserialize(serializedState, jsonSerializerOptions));
    }

    protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken = default)
    {
        var sessionId = TemporalAgentSessionId.WithRandomKey(this.Name!);
        _logger.LogProxySessionCreated(sessionId.AgentName, sessionId.WorkflowId);
        return new ValueTask<AgentSession>(new TemporalAgentSession(sessionId));
    }

    protected override async Task<AgentResponse> RunCoreAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session = null,
        AgentRunOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        session ??= await CreateSessionAsync(cancellationToken).ConfigureAwait(false);

        if (session is not TemporalAgentSession temporalSession)
        {
            throw new ArgumentException(
                "The provided session is not valid for a Temporal agent. " +
                "Create a new session using CreateSessionAsync or provide a session previously created by this agent.",
                paramName: nameof(session));
        }

        IList<string>? enableToolNames = null;
        bool enableToolCalls = true;
        bool isFireAndForget = false;
        ChatResponseFormat? responseFormat = null;

        if (options is TemporalAgentRunOptions temporalOptions)
        {
            enableToolCalls = temporalOptions.EnableToolCalls;
            enableToolNames = temporalOptions.EnableToolNames;
            isFireAndForget = temporalOptions.IsFireAndForget;
        }
        else if (options is ChatClientAgentRunOptions chatOptions)
        {
            responseFormat = chatOptions.ChatOptions?.ResponseFormat;
        }

        if (options?.ResponseFormat is { } format)
        {
            responseFormat = format;
        }

        var request = new RunRequest([.. messages], responseFormat, enableToolCalls, enableToolNames);
        var sessionId = temporalSession.SessionId;

        _logger.LogProxyDispatchingRequest(sessionId.AgentName, sessionId.WorkflowId, isFireAndForget);

        if (isFireAndForget)
        {
            await _agentClient.RunAgentFireAndForgetAsync(sessionId, request, cancellationToken);
            return new AgentResponse();
        }

        return await _agentClient.RunAgentAsync(sessionId, request, cancellationToken);
    }

    protected override IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session = null,
        AgentRunOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException("Streaming is not supported for Temporal agent proxies.");
    }

    /// <summary>
    /// Sends a deferred request to the agent session, applying <paramref name="delay"/> before
    /// the workflow begins executing. Delegates to
    /// <see cref="ITemporalAgentClient.RunAgentDelayedAsync"/>.
    /// </summary>
    /// <remarks>
    /// The delay is only applied when starting a <em>new</em> session. If a workflow with the
    /// same session ID is already running, it is reused immediately regardless of the delay.
    /// </remarks>
    internal Task RunDelayedAsync(
        IEnumerable<ChatMessage> messages,
        TemporalAgentSession session,
        TimeSpan delay,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        var request = new RunRequest([.. messages]);
        var sessionId = session.SessionId;

        _logger.LogProxyDispatchingDelayedRequest(sessionId.AgentName, sessionId.WorkflowId, delay);
        return _agentClient.RunAgentDelayedAsync(sessionId, request, delay, cancellationToken);
    }
}
