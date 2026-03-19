using System.Diagnostics;

namespace Temporalio.Extensions.AI;

/// <summary>
/// OpenTelemetry instrumentation constants for durable AI operations.
/// <para>
/// Register with:
/// <c>.AddSource(DurableChatTelemetry.ActivitySourceName)</c>
/// </para>
/// </summary>
public static class DurableChatTelemetry
{
    /// <summary>The name of the <see cref="ActivitySource"/> used by this library.</summary>
    public const string ActivitySourceName = "Temporalio.Extensions.AI";

    internal static readonly ActivitySource ActivitySource = new(ActivitySourceName);

    // ── Span names ───────────────────────────────────────────────────────────

    /// <summary>Span emitted by <see cref="DurableChatSessionClient"/> when sending a chat request.</summary>
    public const string ChatSendSpanName = "durable_chat.send";

    /// <summary>Span emitted by <see cref="DurableChatActivities"/> for each LLM call.</summary>
    public const string ChatTurnSpanName = "durable_chat.turn";

    /// <summary>Span emitted by <see cref="DurableFunctionActivities"/> for each function invocation.</summary>
    public const string FunctionInvokeSpanName = "durable_function.invoke";

    // ── Attribute names ──────────────────────────────────────────────────────

    /// <summary>The conversation/session identifier.</summary>
    public const string ConversationIdAttribute = "conversation.id";

    /// <summary>The model ID from the request.</summary>
    public const string RequestModelAttribute = "gen_ai.request.model";

    /// <summary>The model ID from the response.</summary>
    public const string ResponseModelAttribute = "gen_ai.response.model";

    /// <summary>Number of input tokens consumed.</summary>
    public const string InputTokensAttribute = "gen_ai.usage.input_tokens";

    /// <summary>Number of output tokens produced.</summary>
    public const string OutputTokensAttribute = "gen_ai.usage.output_tokens";

    /// <summary>The name of the tool being invoked.</summary>
    public const string ToolNameAttribute = "gen_ai.tool.name";

    /// <summary>The call ID of the tool invocation.</summary>
    public const string ToolCallIdAttribute = "gen_ai.tool.call_id";
}
