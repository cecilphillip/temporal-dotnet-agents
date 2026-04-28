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

    /// <summary>Key for per-request keyed DI service key for <see cref="IChatClient"/> resolution.</summary>
    public const string ChatClientKeyKey = "temporal.chatClientKey";

    /// <summary>
    /// Sets a per-request activity timeout that overrides <see cref="DurableExecutionOptions.ActivityTimeout"/>.
    /// </summary>
    public static ChatOptions WithActivityTimeout(this ChatOptions options, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.AdditionalProperties ??= [];
        options.AdditionalProperties[ActivityTimeoutKey] = timeout.ToString("c");
        return options;
    }

    /// <summary>
    /// Sets a per-request maximum retry attempts that overrides the default retry policy.
    /// </summary>
    public static ChatOptions WithMaxRetryAttempts(this ChatOptions options, int maxAttempts)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.AdditionalProperties ??= [];
        options.AdditionalProperties[MaxRetryAttemptsKey] = maxAttempts.ToString();
        return options;
    }

    /// <summary>
    /// Sets a per-request heartbeat timeout that overrides <see cref="DurableExecutionOptions.HeartbeatTimeout"/>.
    /// </summary>
    public static ChatOptions WithHeartbeatTimeout(this ChatOptions options, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.AdditionalProperties ??= [];
        options.AdditionalProperties[HeartbeatTimeoutKey] = timeout.ToString("c");
        return options;
    }

    /// <summary>
    /// Sets the keyed DI service key used to resolve <see cref="IChatClient"/> for this request.
    /// Takes precedence over <see cref="DurableExecutionOptions.DefaultChatClientKey"/>.
    /// Overriding back to the unkeyed client is not supported; omit this call to use the worker default.
    /// </summary>
    public static ChatOptions WithChatClientKey(this ChatOptions options, string key)
    {
        if (string.IsNullOrEmpty(key))
            throw new ArgumentException("Key must not be null or empty.", nameof(key));
        (options.AdditionalProperties ??= new())[ChatClientKeyKey] = key;
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
        if (options?.AdditionalProperties?.TryGetValue(MaxRetryAttemptsKey, out var value) != true)
            return null;

        if (value is string s && int.TryParse(s, out var v))
            return v;
        if (value is int direct) // backward compat
            return direct;

        return null;
    }

    /// <summary>
    /// Tries to read a per-request chat client key from <see cref="ChatOptions.AdditionalProperties"/>.
    /// </summary>
    internal static string? GetChatClientKey(this ChatOptions? options) =>
        options?.AdditionalProperties?.TryGetValue(ChatClientKeyKey, out var v) == true
            ? v as string
            : null;

    private static TimeSpan? GetTimeSpanProperty(ChatOptions? options, string key)
    {
        if (options?.AdditionalProperties?.TryGetValue(key, out var value) != true)
            return null;

        if (value is string s && TimeSpan.TryParseExact(s, "c", null, out var ts))
            return ts;
        if (value is TimeSpan direct) // backward compat
            return direct;

        return null;
    }
}
