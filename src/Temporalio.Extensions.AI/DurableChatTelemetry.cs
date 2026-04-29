using System.Diagnostics;

namespace Temporalio.Extensions.AI;

/// <summary>
/// OpenTelemetry instrumentation constants for durable AI operations.
/// <para>
/// Register with:
/// <c>.AddSource(DurableChatTelemetry.ActivitySourceName)</c>
/// </para>
/// </summary>
/// <remarks>
/// Span names for LLM inference and tool execution follow the OpenTelemetry GenAI semantic
/// conventions at the Development stability tier
/// (https://opentelemetry.io/docs/specs/semconv/gen-ai/).
/// Names and attributes may change before the specification reaches Stable.
/// </remarks>
public static class DurableChatTelemetry
{
    /// <summary>The name of the <see cref="ActivitySource"/> used by this library.</summary>
    public const string ActivitySourceName = "Temporalio.Extensions.AI";

    internal static readonly ActivitySource ActivitySource = new(ActivitySourceName);

    // ── Span names ───────────────────────────────────────────────────────────

    /// <summary>
    /// Span emitted by <see cref="DurableChatSessionClient"/> when sending a chat request.
    /// This is a Temporal-protocol-level span (workflow update dispatch), not an outbound
    /// LLM call, so it intentionally does not follow the GenAI inference span naming convention.
    /// </summary>
    public const string ChatSendSpanName = "durable_chat.send";

    /// <summary>
    /// GenAI operation name prefix for LLM inference spans.
    /// The full span name is <c>"chat {modelId}"</c>, constructed dynamically at call sites
    /// where the model ID is available. Follows the OTel GenAI semantic convention
    /// <c>"{operation.name} {model}"</c> format.
    /// </summary>
    public const string ChatOperationName = "chat";

    /// <summary>
    /// GenAI operation name prefix for tool execution spans.
    /// The full span name is <c>"execute_tool {toolName}"</c>, constructed dynamically at
    /// call sites. Follows the OTel GenAI semantic convention <c>"execute_tool {tool_name}"</c>
    /// format.
    /// </summary>
    public const string ExecuteToolOperationName = "execute_tool";

    // ── Attribute names ──────────────────────────────────────────────────────

    /// <summary>The GenAI operation name (e.g., <c>"chat"</c> or <c>"execute_tool"</c>).</summary>
    public const string OperationNameAttribute = "gen_ai.operation.name";

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
    public const string ToolCallIdAttribute = "gen_ai.tool.call.id";
}
