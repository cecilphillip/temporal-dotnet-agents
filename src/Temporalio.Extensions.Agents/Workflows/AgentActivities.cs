using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Temporalio.Activities;
using Temporalio.Extensions.Agents.Session;

namespace Temporalio.Extensions.Agents.Workflows;

/// <summary>
/// Temporal activities that perform the actual AI inference for agent sessions.
/// All AI inference must run inside an activity to preserve workflow determinism.
/// </summary>
internal class AgentActivities(
    IReadOnlyDictionary<string, Func<IServiceProvider, AIAgent>> factories,
    IServiceProvider services,
    IAgentResponseHandler? responseHandler = null,
    ILoggerFactory? loggerFactory = null)
{
    private readonly ILogger _logger = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<AgentActivities>();
    private readonly ConcurrentDictionary<string, AIAgent> _agentCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Builds the activity summary value (visible in the Temporal Web UI activity list).
    /// Uses the agent name when available; returns null otherwise so the SDK omits the field.
    /// </summary>
    internal static string? BuildActivitySummary(string? agentName) =>
        string.IsNullOrWhiteSpace(agentName) ? null : agentName;

    /// <summary>
    /// Executes the agent with the given input and returns the response plus updated StateBag.
    /// </summary>
    [Activity("Temporalio.Extensions.Agents.ExecuteAgent")]
    public async Task<ExecuteAgentResult> ExecuteAgentAsync(ExecuteAgentInput input)
    {
        var ctx = ActivityExecutionContext.Current;
        var ct = ctx.CancellationToken;

        if (!factories.ContainsKey(input.AgentName))
        {
            throw new AgentNotRegisteredException(input.AgentName);
        }

        var realAgent = _agentCache.GetOrAdd(input.AgentName, name => factories[name](services));
        var sessionId = input.SessionId ?? TemporalAgentSessionId.Parse(ctx.Info.WorkflowId!);

        // Restore StateBag from the previous turn so providers skip re-initialization.
        var session = TemporalAgentSession.FromStateBag(sessionId, input.SerializedStateBag);

        var wrapper = new AgentWorkflowWrapper(realAgent, input.Request, session, services);

        // Rebuild the full conversation from the serialized history.
        int messageCount = 0;
        foreach (var entry in input.ConversationHistory)
            messageCount += entry.Messages.Count;

        var allMessages = new List<ChatMessage>(messageCount);
        foreach (var entry in input.ConversationHistory)
            foreach (var msg in entry.Messages)
                allMessages.Add(msg.ToChatMessage());

        _logger.LogActivityHistoryRebuilt(input.AgentName, sessionId.WorkflowId,
            input.ConversationHistory.Count, allMessages.Count);

        var agentSession = await wrapper.CreateSessionAsync(ct).ConfigureAwait(false);
        var temporalContext = new TemporalAgentContext(ctx.TemporalClient, session, services);
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
            _logger.LogAgentActivityStarted(input.AgentName, sessionId.WorkflowId);

            IAsyncEnumerable<AgentResponseUpdate> responseStream = wrapper.RunStreamingAsync(
                allMessages,
                agentSession,
                options: null,
                ct);

            AgentResponse response;
            if (responseHandler is null)
            {
                // Heartbeat on each streamed chunk even when no handler is registered,
                // so that long-running LLM calls don't hit the heartbeat timeout.
                List<AgentResponseUpdate> collectedUpdates = [];
                await foreach (var update in responseStream.WithCancellation(ct))
                {
                    collectedUpdates.Add(update);
                    ctx.Heartbeat(update.Text);
                }
                response = collectedUpdates.ToAgentResponse();
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

                await responseHandler.OnStreamingResponseUpdateAsync(StreamWithHeartbeat(), ct);
                response = updates.ToAgentResponse();
            }

            // GAP 4: tag token usage onto the span.
            span?.SetTag(TemporalAgentTelemetry.InputTokensAttribute, response.Usage?.InputTokenCount);
            span?.SetTag(TemporalAgentTelemetry.OutputTokensAttribute, response.Usage?.OutputTokenCount);
            span?.SetTag(TemporalAgentTelemetry.TotalTokensAttribute, response.Usage?.TotalTokenCount);

            _logger.LogAgentActivityCompleted(input.AgentName, sessionId.WorkflowId,
                response.Usage?.InputTokenCount, response.Usage?.OutputTokenCount, response.Usage?.TotalTokenCount);

            // GAP 6: capture the updated StateBag so the workflow can persist it.
            var serializedStateBag = session.SerializeStateBag();

            return new ExecuteAgentResult(response, serializedStateBag);
        }
        catch (Exception ex)
        {
            span?.SetStatus(ActivityStatusCode.Error, ex.Message);
            _logger.LogAgentActivityFailed(input.AgentName, sessionId.WorkflowId, ex);
            throw;
        }
        finally
        {
            TemporalAgentContext.SetCurrent(null);
        }
    }
}
