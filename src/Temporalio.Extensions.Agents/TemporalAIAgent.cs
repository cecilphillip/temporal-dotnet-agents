using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Temporalio.Extensions.Agents.Session;
using Temporalio.Extensions.Agents.State;
using Temporalio.Extensions.Agents.Workflows;
using Temporalio.Workflows;

namespace Temporalio.Extensions.Agents;

/// <summary>
/// An <see cref="AIAgent"/> for use inside orchestrating Temporal workflows.
/// Calls <see cref="AgentActivities.ExecuteAgentAsync"/> directly via
/// <see cref="Workflow.ExecuteActivityAsync{TActivityInstance, TResult}"/>.
/// Maintains conversation history as workflow state (replayed from event history).
/// </summary>
/// <remarks>
/// Use this type only from inside a Temporal workflow (e.g., via
/// <see cref="TemporalWorkflowExtensions.GetAgent"/>). For external/host code
/// (API servers, CLIs, console apps), resolve a Temporal agent proxy via
/// <see cref="ServiceCollectionExtensions.GetTemporalAgentProxy"/>.
/// </remarks>
public sealed class TemporalAIAgent : AIAgent
{
    private readonly string _agentName;
    private readonly List<TemporalAgentStateEntry> _history = [];
    private readonly ActivityOptions _activityOptions;
    private int _requestCount;

    internal TemporalAIAgent(string agentName, ActivityOptions? activityOptions = null)
    {
        _agentName = agentName;
        _activityOptions = activityOptions ?? new ActivityOptions
        {
            StartToCloseTimeout = TimeSpan.FromMinutes(30),
            HeartbeatTimeout = TimeSpan.FromMinutes(5),
            Summary = AgentActivities.BuildActivitySummary(_agentName),
        };
    }

    /// <inheritdoc/>
    public override string? Name => _agentName;

    protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken = default)
    {
        var sessionId = TemporalAgentSessionId.WithDeterministicKey(_agentName, Workflow.NewGuid());
        return new ValueTask<AgentSession>(new TemporalAgentSession(sessionId));
    }

    protected override ValueTask<JsonElement> SerializeSessionCoreAsync(
        AgentSession session,
        JsonSerializerOptions? jsonSerializerOptions = null,
        CancellationToken cancellationToken = default)
    {
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

    protected override async Task<AgentResponse> RunCoreAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session = null,
        AgentRunOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        session ??= await CreateSessionAsync(cancellationToken).ConfigureAwait(false);

        IList<string>? enableToolNames = null;
        bool enableToolCalls = true;
        ChatResponseFormat? responseFormat = null;

        if (options is TemporalAgentRunOptions temporalOptions)
        {
            enableToolCalls = temporalOptions.EnableToolCalls;
            enableToolNames = temporalOptions.EnableToolNames;
        }
        else if (options is ChatClientAgentRunOptions chatOptions)
        {
            responseFormat = chatOptions.ChatOptions?.ResponseFormat;
        }

        if (options?.ResponseFormat is { } format)
        {
            responseFormat = format;
        }

        var request = new RunRequest([.. messages], responseFormat, enableToolCalls, enableToolNames)
        {
            OrchestrationId = Workflow.Info.WorkflowId,
            CorrelationId = Workflow.NewGuid().ToString("N"),
        };

        _history.Add(TemporalAgentStateRequest.FromRunRequest(request, Workflow.UtcNow));
        _requestCount++;

        // TemporalAIAgent lives inside a workflow and creates sessions in-process,
        // so StateBag persistence across turns is handled by the workflow history itself.
        // We pass null for the StateBag (no cross-activity-call state needed here).
        // We pass the session ID explicitly because the orchestrating workflow's ID
        // may not follow the ta-{agentName}-{key} convention.
        var sessionId = session is TemporalAgentSession ts ? ts.SessionId : (TemporalAgentSessionId?)null;
        var activityInput = new ExecuteAgentInput(_agentName, request, _history, sessionId: sessionId);

        Workflow.Logger.LogInWorkflowAgentDispatching(_agentName, _requestCount);

        var result = await Workflow.ExecuteActivityAsync(
            (AgentActivities a) => a.ExecuteAgentAsync(activityInput),
            _activityOptions);

        _history.Add(TemporalAgentStateResponse.FromResponse(request.CorrelationId!, result.Response, Workflow.UtcNow));
        return result.Response;
    }

    protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session = null,
        AgentRunOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Streaming is not supported; return the full response as a single update.
        var response = await RunCoreAsync(messages, session, options, cancellationToken);
        foreach (var update in response.ToAgentResponseUpdates())
        {
            yield return update;
        }
    }
}
