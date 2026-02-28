using System.Diagnostics;
using Microsoft.Agents.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Temporalio.Activities;

namespace Temporalio.Extensions.Agents;

/// <summary>
/// Temporal activities that perform the actual AI inference for agent sessions.
/// All AI inference must run inside an activity to preserve workflow determinism.
/// </summary>
internal class AgentActivities
{
    private readonly IReadOnlyDictionary<string, Func<IServiceProvider, AIAgent>> _factories;
    private readonly IServiceProvider _services;
    private readonly IAgentResponseHandler? _responseHandler;
    private readonly ILogger _logger;

    public AgentActivities(
        IReadOnlyDictionary<string, Func<IServiceProvider, AIAgent>> factories,
        IServiceProvider services,
        IAgentResponseHandler? responseHandler = null,
        ILoggerFactory? loggerFactory = null)
    {
        _factories = factories;
        _services = services;
        _responseHandler = responseHandler;
        _logger = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<AgentActivities>();
    }

    /// <summary>
    /// Executes the agent with the given input and returns the response plus updated StateBag.
    /// </summary>
    [Activity("Temporalio.Extensions.Agents.ExecuteAgent")]
    public async Task<ExecuteAgentResult> ExecuteAgentAsync(ExecuteAgentInput input)
    {
        var ctx = ActivityExecutionContext.Current;
        var ct = ctx.CancellationToken;

        if (!_factories.TryGetValue(input.AgentName, out var factory))
        {
            throw new AgentNotRegisteredException(input.AgentName);
        }

        var realAgent = factory(_services);
        var sessionId = TemporalAgentSessionId.Parse(ctx.Info.WorkflowId!);

        // GAP 6: restore StateBag from the previous turn so providers skip re-initialization.
        var session = TemporalAgentSession.FromStateBag(sessionId, input.SerializedStateBag);

        var wrapper = new AgentWorkflowWrapper(realAgent, input.Request, session, _services);

        // Rebuild the full conversation from the serialized history.
        var allMessages = input.ConversationHistory
            .SelectMany(e => e.Messages)
            .Select(m => m.ToChatMessage())
            .ToList();

        Logs.LogActivityHistoryRebuilt(_logger, input.AgentName, sessionId.WorkflowId,
            input.ConversationHistory.Count, allMessages.Count);

        var agentSession = await wrapper.CreateSessionAsync(ct).ConfigureAwait(false);
        var temporalContext = new TemporalAgentContext(ctx.TemporalClient, session, _services);
        TemporalAgentContext.SetCurrent(temporalContext);

        // GAP 4: emit an OpenTelemetry span for this agent turn.
        using var span = TemporalAgentTelemetry.ActivitySource.StartActivity(
            TemporalAgentTelemetry.AgentTurnSpanName,
            ActivityKind.Internal);

        span?.SetTag(TemporalAgentTelemetry.AgentNameAttribute, input.AgentName);
        span?.SetTag(TemporalAgentTelemetry.AgentSessionIdAttribute, sessionId.WorkflowId);
        span?.SetTag(TemporalAgentTelemetry.AgentCorrelationIdAttribute, input.Request.CorrelationId);

        try
        {
            Logs.LogAgentActivityStarted(_logger, input.AgentName, sessionId.WorkflowId);

            IAsyncEnumerable<AgentResponseUpdate> responseStream = wrapper.RunStreamingAsync(
                allMessages,
                agentSession,
                options: null,
                ct);

            AgentResponse response;
            if (_responseHandler is null)
            {
                response = await responseStream.ToAgentResponseAsync(ct);
            }
            else
            {
                List<AgentResponseUpdate> updates = [];

                async IAsyncEnumerable<AgentResponseUpdate> StreamWithHeartbeat()
                {
                    await foreach (var update in responseStream)
                    {
                        updates.Add(update);
                        ctx.Heartbeat(update.Text);
                        yield return update;
                    }
                }

                await _responseHandler.OnStreamingResponseUpdateAsync(StreamWithHeartbeat(), ct);
                response = updates.ToAgentResponse();
            }

            // GAP 4: tag token usage onto the span.
            span?.SetTag(TemporalAgentTelemetry.InputTokensAttribute, response.Usage?.InputTokenCount);
            span?.SetTag(TemporalAgentTelemetry.OutputTokensAttribute, response.Usage?.OutputTokenCount);
            span?.SetTag(TemporalAgentTelemetry.TotalTokensAttribute, response.Usage?.TotalTokenCount);

            Logs.LogAgentActivityCompleted(_logger, input.AgentName, sessionId.WorkflowId,
                response.Usage?.InputTokenCount, response.Usage?.OutputTokenCount, response.Usage?.TotalTokenCount);

            // GAP 6: capture the updated StateBag so the workflow can persist it.
            var serializedStateBag = session.SerializeStateBag();

            return new ExecuteAgentResult(response, serializedStateBag);
        }
        catch (Exception ex)
        {
            span?.SetStatus(ActivityStatusCode.Error, ex.Message);
            Logs.LogAgentActivityFailed(_logger, input.AgentName, sessionId.WorkflowId, ex);
            throw;
        }
        finally
        {
            TemporalAgentContext.SetCurrent(null);
        }
    }
}
