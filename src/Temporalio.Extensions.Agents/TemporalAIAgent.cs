using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Temporalio.Extensions.AI;
using Temporalio.Extensions.Agents.Session;
using Temporalio.Extensions.Agents.State;
using Temporalio.Extensions.Agents.Workflows;
using Temporalio.Workflows;

namespace Temporalio.Extensions.Agents;

/// <summary>
/// An <see cref="AIAgent"/> for use inside orchestrating Temporal workflows.
/// Drives the durable-agent dispatch loop (<c>RunDurableAgentStep</c> + <c>InvokeAgentTool</c>)
/// directly via <see cref="Workflow.ExecuteActivityAsync{TActivityInstance, TResult}"/>.
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
    private readonly List<DurableSessionEntry> _history = [];
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
        string? callerCorrelationId = null;
        ChatResponseFormat? responseFormat = null;

        if (options is TemporalAgentRunOptions temporalOptions)
        {
            enableToolCalls = temporalOptions.EnableToolCalls;
            enableToolNames = temporalOptions.EnableToolNames;
            callerCorrelationId = temporalOptions.CorrelationId;
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
            CorrelationId = string.IsNullOrEmpty(callerCorrelationId)
                ? Workflow.NewGuid().ToString("N")
                : callerCorrelationId,
        };

        _history.Add(AgentSessionRequest.FromRunRequest(request, Workflow.UtcNow));
        _requestCount++;

        var sessionId = session is TemporalAgentSession ts ? ts.SessionId : (TemporalAgentSessionId?)null;

        Workflow.Logger.LogInWorkflowAgentDispatching(_agentName, _requestCount);

        // Drive the durable-agent dispatch loop for sub-agents inside an orchestrating workflow.
        // Mirrors the AgentWorkflow main loop but without continue-as-new / search attributes /
        // history reduction (the orchestrating workflow owns those concerns).
        var accumulated = new List<ChatMessage>();
        foreach (var entry in _history)
        {
            foreach (var m in entry.Messages)
            {
                accumulated.Add(m);
            }
        }

        var allTurnMessages = new List<ChatMessage>();
        UsageDetails? totalUsage = null;
        const int maxIterations = 20;

        for (var iteration = 0; iteration < maxIterations; iteration++)
        {
            var stepInput = new AgentStepInput
            {
                AgentName = _agentName,
                Request = request,
                AccumulatedMessages = accumulated,
                SerializedStateBag = null,
                SessionId = sessionId,
                IsFirstStep = iteration == 0,
            };

            var stepResult = await Workflow.ExecuteActivityAsync(
                (AgentActivities a) => a.RunDurableAgentStepAsync(stepInput),
                _activityOptions);

            if (stepResult.Usage is not null)
            {
                totalUsage ??= new UsageDetails();
                totalUsage.InputTokenCount = (totalUsage.InputTokenCount ?? 0) + (stepResult.Usage.InputTokenCount ?? 0);
                totalUsage.OutputTokenCount = (totalUsage.OutputTokenCount ?? 0) + (stepResult.Usage.OutputTokenCount ?? 0);
                totalUsage.TotalTokenCount = (totalUsage.TotalTokenCount ?? 0) + (stepResult.Usage.TotalTokenCount ?? 0);
            }

            accumulated.Add(stepResult.AssistantMessage);
            allTurnMessages.Add(stepResult.AssistantMessage);

            if (stepResult.IsFinal || stepResult.ToolCalls is null || stepResult.ToolCalls.Count == 0)
            {
                var response = new AgentResponse
                {
                    Messages = allTurnMessages,
                    Usage = totalUsage,
                    CreatedAt = Workflow.UtcNow,
                };

                _history.Add(AgentSessionResponse.FromAgentResponse(
                    request.CorrelationId!, response, Workflow.UtcNow));
                return response;
            }

            var toolCalls = stepResult.ToolCalls;
            var toolTasks = new List<Task<InvokeAgentToolResult>>(toolCalls.Count);
            foreach (var tc in toolCalls)
            {
                var toolInput = new InvokeAgentToolInput
                {
                    AgentName = _agentName,
                    ToolName = tc.Name,
                    Arguments = tc.Arguments is null
                        ? null
                        : new Dictionary<string, object?>(tc.Arguments),
                    CallId = tc.CallId,
                };

                toolTasks.Add(Workflow.ExecuteActivityAsync(
                    (AgentActivities a) => a.InvokeAgentToolAsync(toolInput),
                    _activityOptions));
            }

            var toolResults = await Workflow.WhenAllAsync(toolTasks);

            var functionResultContents = new List<AIContent>(toolCalls.Count);
            for (var i = 0; i < toolCalls.Count; i++)
            {
                functionResultContents.Add(new FunctionResultContent(
                    callId: toolCalls[i].CallId,
                    result: toolResults[i].Result));
            }

            var toolResultMessage = new ChatMessage(ChatRole.Tool, functionResultContents);
            accumulated.Add(toolResultMessage);
            allTurnMessages.Add(toolResultMessage);
        }

        var iterCapResponse = new AgentResponse
        {
            Messages = allTurnMessages,
            Usage = totalUsage,
            CreatedAt = Workflow.UtcNow,
        };
        _history.Add(AgentSessionResponse.FromAgentResponse(
            request.CorrelationId!, iterCapResponse, Workflow.UtcNow));
        return iterCapResponse;
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
