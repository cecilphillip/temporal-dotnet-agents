using System.Diagnostics;

namespace Temporalio.Extensions.Agents;

/// <summary>
/// OpenTelemetry instrumentation constants for TemporalAgents.
/// <para>
/// Use an OTel SDK (e.g. <c>OpenTelemetry.Extensions.Hosting</c>) with
/// <c>.AddSource(TemporalAgentTelemetry.ActivitySourceName)</c> to receive these spans.
/// </para>
/// </summary>
/// <remarks>
/// <para>
/// Attribute names follow the OpenTelemetry GenAI semantic conventions
/// (<see href="https://opentelemetry.io/docs/specs/semconv/gen-ai/"/>). The GenAI conventions
/// are at <b>Development</b> stability tier — names may change before they reach Stable, and
/// consumers (dashboards, alerts) should be prepared to update accordingly.
/// </para>
/// <para>
/// Two attributes are intentionally non-canonical:
/// <list type="bullet">
///   <item>
///     <description>
///     <see cref="AgentCorrelationIdAttribute"/> uses the <c>temporal.agent.*</c> namespace
///     because OTel does not define a request-side correlation attribute. Naming it
///     under our own namespace is more honest than co-opting an unrelated <c>gen_ai.*</c> field.
///     </description>
///   </item>
///   <item>
///     <description>
///     <see cref="TotalTokensAttribute"/> extends the <c>gen_ai.usage.*</c> namespace —
///     OTel defines <c>input_tokens</c> and <c>output_tokens</c> but not a total. Using
///     <c>gen_ai.usage.total_tokens</c> keeps related token attributes grouped under one prefix.
///     </description>
///   </item>
/// </list>
/// </para>
/// </remarks>
public static class TemporalAgentTelemetry
{
    /// <summary>The name of the <see cref="ActivitySource"/> used by this library.</summary>
    public const string ActivitySourceName = "Temporalio.Extensions.Agents";

    internal static readonly ActivitySource ActivitySource = new(ActivitySourceName);

    // ── Span names ─────────────────────────────────────────────────────────────

    /// <summary>Span emitted by <c>AgentActivities.ExecuteAgentAsync</c> for each agent turn.</summary>
    public const string AgentTurnSpanName = "agent.turn";

    /// <summary>Span emitted by <c>DefaultTemporalAgentClient.RunAgentAsync</c> when sending an update.</summary>
    public const string AgentClientSendSpanName = "agent.client.send";

    /// <summary>Span emitted by <c>DefaultTemporalAgentClient.ScheduleAgentAsync</c> when creating a recurring schedule.</summary>
    public const string AgentScheduleCreateSpanName = "agent.schedule.create";

    /// <summary>Span emitted by <c>DefaultTemporalAgentClient.RunAgentDelayedAsync</c> when starting a delayed workflow.</summary>
    public const string AgentScheduleDelayedSpanName = "agent.schedule.delayed";

    /// <summary>Span emitted by <c>ScheduleActivities.ScheduleOneTimeAgentRunAsync</c> when scheduling a one-time run.</summary>
    public const string AgentScheduleOneTimeSpanName = "agent.schedule.one_time";

    // ── Attribute names ────────────────────────────────────────────────────────
    // Aligned with OpenTelemetry GenAI semantic conventions (Development tier).
    // See <remarks> on the type for the rationale behind the two non-canonical names.

    /// <summary>The registered name of the agent being invoked. OTel: <c>gen_ai.agent.name</c>.</summary>
    public const string AgentNameAttribute = "gen_ai.agent.name";

    /// <summary>The Temporal workflow ID that backs the agent session. OTel: <c>gen_ai.conversation.id</c>.</summary>
    public const string AgentSessionIdAttribute = "gen_ai.conversation.id";

    /// <summary>
    /// The correlation ID linking the request to its response. Temporal-namespaced because
    /// OTel does not define a request-side correlation attribute.
    /// </summary>
    public const string AgentCorrelationIdAttribute = "temporal.agent.correlation_id";

    /// <summary>Number of input (prompt) tokens consumed by the LLM. OTel: <c>gen_ai.usage.input_tokens</c>.</summary>
    public const string InputTokensAttribute = "gen_ai.usage.input_tokens";

    /// <summary>Number of output (completion) tokens produced by the LLM. OTel: <c>gen_ai.usage.output_tokens</c>.</summary>
    public const string OutputTokensAttribute = "gen_ai.usage.output_tokens";

    /// <summary>
    /// Total tokens (input + output). Extends the <c>gen_ai.usage.*</c> namespace; no canonical
    /// OTel attribute exists for this aggregate.
    /// </summary>
    public const string TotalTokensAttribute = "gen_ai.usage.total_tokens";

    /// <summary>The ID of the recurring schedule being created.</summary>
    public const string ScheduleIdAttribute = "schedule.id";

    /// <summary>The delay before a deferred run starts, as <see cref="TimeSpan.ToString()"/>.</summary>
    public const string ScheduleDelayAttribute = "schedule.delay";

    /// <summary>The run ID of a one-time scheduled job.</summary>
    public const string ScheduleJobIdAttribute = "schedule.job_id";
}
