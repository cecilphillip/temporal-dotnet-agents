using Temporalio.Common;

namespace Temporalio.Extensions.Agents;

/// <summary>
/// Per-tool Temporal activity overrides applied when a <see cref="DurableAgentBuilder"/>-registered
/// agent dispatches a tool call as a Temporal activity (<c>Temporalio.Extensions.Agents.InvokeAgentTool</c>).
/// </summary>
/// <remarks>
/// <para>
/// Each property is independent of the others — leave a field <see langword="null"/> to inherit the
/// worker-level default from <see cref="TemporalAgentsOptions"/> (<c>DefaultActivityTimeout</c>,
/// <c>DefaultHeartbeatTimeout</c>, <c>DefaultRetryPolicy</c>).
/// </para>
/// <para>
/// Use <see cref="NoRetry"/> on write-style tools (send email, persist a record, charge a card) so
/// non-idempotent re-execution does not occur on activity retry. Read-style tools generally inherit
/// the default retry policy.
/// </para>
/// </remarks>
public sealed class DurableToolOptions
{
    /// <summary>
    /// Gets or sets the Temporal <c>StartToCloseTimeout</c> applied to this tool's activity dispatch.
    /// When <see langword="null"/>, the worker-level default is used.
    /// </summary>
    public TimeSpan? StartToCloseTimeout { get; set; }

    /// <summary>
    /// Gets or sets the Temporal heartbeat timeout applied to this tool's activity dispatch.
    /// When <see langword="null"/>, the worker-level default is used.
    /// </summary>
    public TimeSpan? HeartbeatTimeout { get; set; }

    /// <summary>
    /// Gets or sets the retry policy applied to this tool's activity dispatch.
    /// When <see langword="null"/>, the worker-level default is used.
    /// </summary>
    public RetryPolicy? RetryPolicy { get; set; }

    /// <summary>
    /// Disables retries for this tool by setting <see cref="RetryPolicy"/> to a policy with
    /// <see cref="RetryPolicy.MaximumAttempts"/> equal to <c>1</c>.
    /// </summary>
    /// <returns>This instance, for fluent chaining.</returns>
    /// <remarks>
    /// Use for non-idempotent / write-style tools so a transient activity failure does not cause
    /// double-execution (e.g. sending the same email twice).
    /// </remarks>
    public DurableToolOptions NoRetry()
    {
        RetryPolicy = new RetryPolicy { MaximumAttempts = 1 };
        return this;
    }

    /// <summary>
    /// Sets <see cref="RetryPolicy"/> to a policy with <see cref="RetryPolicy.MaximumAttempts"/>
    /// equal to <paramref name="maxAttempts"/>.
    /// </summary>
    /// <param name="maxAttempts">Maximum number of attempts; must be greater than zero.</param>
    /// <returns>This instance, for fluent chaining.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="maxAttempts"/> is less than or equal to zero.
    /// </exception>
    public DurableToolOptions WithMaxAttempts(int maxAttempts)
    {
        if (maxAttempts <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxAttempts),
                maxAttempts,
                "Maximum attempts must be greater than zero.");
        }

        RetryPolicy = new RetryPolicy { MaximumAttempts = maxAttempts };
        return this;
    }

    /// <summary>
    /// Sets <see cref="StartToCloseTimeout"/> to <paramref name="timeout"/>.
    /// </summary>
    /// <param name="timeout">The activity start-to-close timeout; must be greater than <see cref="TimeSpan.Zero"/>.</param>
    /// <returns>This instance, for fluent chaining.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="timeout"/> is less than or equal to <see cref="TimeSpan.Zero"/>.
    /// </exception>
    public DurableToolOptions WithTimeout(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(timeout),
                timeout,
                "Timeout must be greater than zero.");
        }

        StartToCloseTimeout = timeout;
        return this;
    }
}
