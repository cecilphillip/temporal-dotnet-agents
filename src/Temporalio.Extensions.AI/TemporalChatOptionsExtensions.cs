using Microsoft.Extensions.AI;

namespace Temporalio.Extensions.AI;

/// <summary>
/// Extension methods for setting Temporal-specific metadata on <see cref="ChatOptions"/>
/// via <see cref="ChatOptions.AdditionalProperties"/>.
/// </summary>
public static class TemporalChatOptionsExtensions
{
    /// <summary>Key for per-request activity timeout override.</summary>
    public const string ActivityTimeoutKey = "temporal.activity.timeout";

    /// <summary>Key for per-request maximum retry attempts override.</summary>
    public const string MaxRetryAttemptsKey = "temporal.retry.max_attempts";

    /// <summary>Key for per-request heartbeat timeout override.</summary>
    public const string HeartbeatTimeoutKey = "temporal.heartbeat.timeout";

    /// <summary>
    /// Sets a per-request activity timeout that overrides <see cref="DurableExecutionOptions.ActivityTimeout"/>.
    /// </summary>
    public static ChatOptions WithActivityTimeout(this ChatOptions options, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.AdditionalProperties ??= [];
        options.AdditionalProperties[ActivityTimeoutKey] = timeout;
        return options;
    }

    /// <summary>
    /// Sets a per-request maximum retry attempts that overrides the default retry policy.
    /// </summary>
    public static ChatOptions WithMaxRetryAttempts(this ChatOptions options, int maxAttempts)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.AdditionalProperties ??= [];
        options.AdditionalProperties[MaxRetryAttemptsKey] = maxAttempts;
        return options;
    }

    /// <summary>
    /// Sets a per-request heartbeat timeout that overrides <see cref="DurableExecutionOptions.HeartbeatTimeout"/>.
    /// </summary>
    public static ChatOptions WithHeartbeatTimeout(this ChatOptions options, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.AdditionalProperties ??= [];
        options.AdditionalProperties[HeartbeatTimeoutKey] = timeout;
        return options;
    }

    /// <summary>
    /// Tries to read a per-request activity timeout from <see cref="ChatOptions.AdditionalProperties"/>.
    /// </summary>
    internal static TimeSpan? GetActivityTimeout(this ChatOptions? options) =>
        GetTimeSpanProperty(options, ActivityTimeoutKey);

    /// <summary>
    /// Tries to read a per-request heartbeat timeout from <see cref="ChatOptions.AdditionalProperties"/>.
    /// </summary>
    internal static TimeSpan? GetHeartbeatTimeout(this ChatOptions? options) =>
        GetTimeSpanProperty(options, HeartbeatTimeoutKey);

    /// <summary>
    /// Tries to read a per-request max retry attempts from <see cref="ChatOptions.AdditionalProperties"/>.
    /// </summary>
    internal static int? GetMaxRetryAttempts(this ChatOptions? options)
    {
        if (options?.AdditionalProperties?.TryGetValue(MaxRetryAttemptsKey, out var value) == true
            && value is int intVal)
        {
            return intVal;
        }

        return null;
    }

    private static TimeSpan? GetTimeSpanProperty(ChatOptions? options, string key)
    {
        if (options?.AdditionalProperties?.TryGetValue(key, out var value) == true
            && value is TimeSpan ts)
        {
            return ts;
        }

        return null;
    }
}
