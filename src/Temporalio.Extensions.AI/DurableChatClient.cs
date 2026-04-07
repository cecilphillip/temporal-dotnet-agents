using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Temporalio.Activities;
using Temporalio.Workflows;

namespace Temporalio.Extensions.AI;

/// <summary>
/// A <see cref="DelegatingChatClient"/> middleware that wraps <see cref="IChatClient.GetResponseAsync"/>
/// as a Temporal activity when running inside a Temporal workflow.
/// </summary>
/// <remarks>
/// <para>
/// Context-aware behavior:
/// <list type="bullet">
///   <item>Inside a Temporal workflow → dispatches via <c>Workflow.ExecuteActivityAsync</c></item>
///   <item>Inside a Temporal activity → passes through to inner client (avoids double-wrapping)</item>
///   <item>External (neither) → passes through to inner client</item>
/// </list>
/// </para>
/// <para>
/// <b>Limitation:</b> <see cref="ChatOptions.RawRepresentationFactory"/> is not serializable and
/// will not be available on the worker side when invoked as an activity.
/// </para>
/// </remarks>
public sealed class DurableChatClient : DelegatingChatClient
{
    private readonly DurableExecutionOptions _options;
    private int _turnCounter;

    /// <summary>
    /// Initializes a new instance of the <see cref="DurableChatClient"/> class.
    /// </summary>
    /// <param name="innerClient">The inner chat client to delegate to.</param>
    /// <param name="options">Durable execution configuration.</param>
    public DurableChatClient(IChatClient innerClient, DurableExecutionOptions options)
        : base(innerClient)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    /// <inheritdoc/>
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (!Workflow.InWorkflow)
        {
            // Outside a workflow — pass through directly, stripping Temporal-internal keys
            // that the inner client does not understand.
            return await base.GetResponseAsync(messages, StripTemporalOptions(options), cancellationToken)
                .ConfigureAwait(false);
        }

        // Inside a workflow — dispatch as an activity.
        var input = CreateInput(messages, options);

        var output = await Workflow.ExecuteActivityAsync(
            (DurableChatActivities a) => a.GetResponseAsync(input),
            CreateActivityOptions(options)).ConfigureAwait(false);

        return output.Response;
    }

    /// <inheritdoc/>
    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (!Workflow.InWorkflow)
        {
            // Outside a workflow — pass through directly, stripping Temporal-internal keys
            // that the inner client does not understand.
            await foreach (var update in base.GetStreamingResponseAsync(messages, StripTemporalOptions(options), cancellationToken)
                .ConfigureAwait(false))
            {
                yield return update;
            }
            yield break;
        }

        // Inside a workflow — buffer strategy: execute as activity, then yield updates.
        // Temporal activities return a single result, so we collect the full response
        // and convert to ChatResponseUpdate sequence.
        var input = CreateInput(messages, options);

        var output = await Workflow.ExecuteActivityAsync(
            (DurableChatActivities a) => a.GetResponseAsync(input),
            CreateActivityOptions(options)).ConfigureAwait(false);

        // Convert the buffered response to streaming updates.
        foreach (var update in output.Response.ToChatResponseUpdates())
        {
            yield return update;
        }
    }

    /// <inheritdoc/>
    public override object? GetService(Type serviceType, object? serviceKey = null)
    {
        if (serviceType == typeof(DurableExecutionOptions) && serviceKey is null)
        {
            return _options;
        }

        return base.GetService(serviceType, serviceKey);
    }

    private DurableChatInput CreateInput(IEnumerable<ChatMessage> messages, ChatOptions? options)
    {
        var turnNumber = Interlocked.Increment(ref _turnCounter);

        return new DurableChatInput
        {
            Messages = messages as IList<ChatMessage> ?? messages.ToList(),
            Options = StripNonSerializableOptions(options),
            ConversationId = Workflow.Info.WorkflowId,
            TurnNumber = turnNumber,
        };
    }

    private ActivityOptions CreateActivityOptions(ChatOptions? chatOptions = null)
    {
        var activityOptions = new ActivityOptions
        {
            StartToCloseTimeout = chatOptions.GetActivityTimeout() ?? _options.ActivityTimeout,
            HeartbeatTimeout = chatOptions.GetHeartbeatTimeout() ?? _options.HeartbeatTimeout,
        };

        if (_options.RetryPolicy is not null)
        {
            activityOptions.RetryPolicy = _options.RetryPolicy;
        }

        // Per-request retry override via AdditionalProperties.
        var maxRetry = chatOptions.GetMaxRetryAttempts();
        if (maxRetry.HasValue)
        {
            activityOptions.RetryPolicy = new Temporalio.Common.RetryPolicy
            {
                MaximumAttempts = maxRetry.Value,
            };
        }

        return activityOptions;
    }

    /// <summary>
    /// Creates a serializable copy of ChatOptions, stripping non-serializable fields
    /// and Temporal-internal keys from AdditionalProperties.
    /// </summary>
    private static ChatOptions? StripNonSerializableOptions(ChatOptions? options)
    {
        if (options is null)
        {
            return null;
        }

        // Clone the options to avoid mutating the caller's instance.
        // RawRepresentationFactory is a delegate and cannot be serialized.
        return new ChatOptions
        {
            Temperature = options.Temperature,
            MaxOutputTokens = options.MaxOutputTokens,
            TopP = options.TopP,
            TopK = options.TopK,
            StopSequences = options.StopSequences,
            FrequencyPenalty = options.FrequencyPenalty,
            PresencePenalty = options.PresencePenalty,
            Seed = options.Seed,
            ModelId = options.ModelId,
            ResponseFormat = options.ResponseFormat,
            Tools = options.Tools,
            ToolMode = options.ToolMode,
            AdditionalProperties = StripTemporalKeys(options.AdditionalProperties),
            ConversationId = options.ConversationId,
        };
    }

    /// <summary>
    /// Returns a shallow copy of <paramref name="options"/> with Temporal-internal keys removed
    /// from <see cref="ChatOptions.AdditionalProperties"/>. Used for the pass-through path.
    /// Returns null when <paramref name="options"/> is null.
    /// </summary>
    private static ChatOptions? StripTemporalOptions(ChatOptions? options)
    {
        if (options is null) return null;
        if (options.AdditionalProperties is not { Count: > 0 }) return options;

        // Only allocate a stripped copy when there are Temporal keys to remove.
        bool hasTemporalKeys = options.AdditionalProperties.ContainsKey(TemporalChatOptionsExtensions.ActivityTimeoutKey)
            || options.AdditionalProperties.ContainsKey(TemporalChatOptionsExtensions.HeartbeatTimeoutKey)
            || options.AdditionalProperties.ContainsKey(TemporalChatOptionsExtensions.MaxRetryAttemptsKey);

        if (!hasTemporalKeys) return options;

        return new ChatOptions
        {
            Temperature = options.Temperature,
            MaxOutputTokens = options.MaxOutputTokens,
            TopP = options.TopP,
            TopK = options.TopK,
            StopSequences = options.StopSequences,
            FrequencyPenalty = options.FrequencyPenalty,
            PresencePenalty = options.PresencePenalty,
            Seed = options.Seed,
            ModelId = options.ModelId,
            ResponseFormat = options.ResponseFormat,
            Tools = options.Tools,
            ToolMode = options.ToolMode,
            AdditionalProperties = StripTemporalKeys(options.AdditionalProperties),
            ConversationId = options.ConversationId,
        };
    }

    /// <summary>
    /// Returns a copy of <paramref name="props"/> with Temporal-internal keys removed,
    /// or null if <paramref name="props"/> is null or all entries are Temporal keys.
    /// </summary>
    private static AdditionalPropertiesDictionary? StripTemporalKeys(AdditionalPropertiesDictionary? props)
    {
        if (props is null) return null;

        AdditionalPropertiesDictionary? result = null;
        foreach (var kvp in props)
        {
            if (kvp.Key is TemporalChatOptionsExtensions.ActivityTimeoutKey
                        or TemporalChatOptionsExtensions.HeartbeatTimeoutKey
                        or TemporalChatOptionsExtensions.MaxRetryAttemptsKey)
                continue;
            result ??= new AdditionalPropertiesDictionary();
            result[kvp.Key] = kvp.Value;
        }
        return result;
    }
}
