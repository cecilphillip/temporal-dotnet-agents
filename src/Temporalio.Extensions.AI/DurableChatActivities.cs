using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Temporalio.Activities;

namespace Temporalio.Extensions.AI;

/// <summary>
/// Temporal activities that perform actual LLM inference.
/// The <see cref="IChatClient"/> is resolved from DI on the worker side,
/// optionally by keyed service key carried in <see cref="DurableChatInput.ClientKey"/>.
/// </summary>
internal sealed class DurableChatActivities(
    IServiceProvider services,
    ILoggerFactory? loggerFactory = null)
{
    private readonly ILogger _logger = (loggerFactory ?? NullLoggerFactory.Instance)
        .CreateLogger<DurableChatActivities>();

    /// <summary>
    /// Executes a chat completion by calling the inner <see cref="IChatClient"/>.
    /// </summary>
    [Activity("Temporalio.Extensions.AI.GetResponse")]
    public async Task<ChatResponse> GetResponseAsync(DurableChatInput input)
    {
        var ctx = ActivityExecutionContext.HasCurrent ? ActivityExecutionContext.Current : null;
        var ct = ctx?.CancellationToken ?? CancellationToken.None;

        _logger.LogDebug(
            "Executing durable chat activity for conversation {ConversationId}, turn {TurnNumber}",
            input.ConversationId, input.TurnNumber);

        var modelId = input.Options?.ModelId;
        using var span = DurableChatTelemetry.ActivitySource.StartActivity(
            $"{DurableChatTelemetry.ChatOperationName} {modelId ?? "unknown"}",
            System.Diagnostics.ActivityKind.Client);

        span?.SetTag(DurableChatTelemetry.OperationNameAttribute, DurableChatTelemetry.ChatOperationName);
        span?.SetTag(DurableChatTelemetry.ConversationIdAttribute, input.ConversationId);
        span?.SetTag(DurableChatTelemetry.RequestModelAttribute, modelId);

        var chatClient = string.IsNullOrEmpty(input.ClientKey)
            ? services.GetRequiredService<IChatClient>()
            : services.GetRequiredKeyedService<IChatClient>(input.ClientKey);

        try
        {
            var collected = new List<ChatResponseUpdate>();
            await foreach (var update in chatClient.GetStreamingResponseAsync(
                    input.Messages, input.Options, ct)
                .WithCancellation(ct)
                .ConfigureAwait(false))
            {
                collected.Add(update);
                ctx?.Heartbeat(update.Text);
            }
            var response = collected.ToChatResponse();

            span?.SetTag(DurableChatTelemetry.InputTokensAttribute, response.Usage?.InputTokenCount);
            span?.SetTag(DurableChatTelemetry.OutputTokensAttribute, response.Usage?.OutputTokenCount);
            span?.SetTag(DurableChatTelemetry.ResponseModelAttribute, response.ModelId);

            _logger.LogDebug(
                "Durable chat activity completed for conversation {ConversationId}, turn {TurnNumber}",
                input.ConversationId, input.TurnNumber);

            return response;
        }
        catch (Exception ex)
        {
            span?.SetStatus(System.Diagnostics.ActivityStatusCode.Error, ex.Message);
            _logger.LogError(ex,
                "Durable chat activity failed for conversation {ConversationId}, turn {TurnNumber}",
                input.ConversationId, input.TurnNumber);
            throw;
        }
    }
}
