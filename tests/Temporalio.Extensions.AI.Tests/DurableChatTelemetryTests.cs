using Xunit;

namespace Temporalio.Extensions.AI.Tests;

public class DurableChatTelemetryTests
{
    [Fact]
    public void ActivitySourceName_IsCorrect()
    {
        Assert.Equal("Temporalio.Extensions.AI", DurableChatTelemetry.ActivitySourceName);
    }

    [Fact]
    public void SpanNames_AreCorrect()
    {
        // ChatSendSpanName is a Temporal-protocol span (workflow update dispatch), not an LLM call,
        // so it retains its original name rather than following the GenAI convention.
        Assert.Equal("durable_chat.send", DurableChatTelemetry.ChatSendSpanName);

        // LLM inference and tool spans follow OTel GenAI semantic conventions.
        // Full span names are constructed dynamically as "{operation} {model|tool}".
        Assert.Equal("chat", DurableChatTelemetry.ChatOperationName);
        Assert.Equal("execute_tool", DurableChatTelemetry.ExecuteToolOperationName);
    }

    [Fact]
    public void AttributeNames_AreCorrect()
    {
        Assert.Equal("gen_ai.operation.name", DurableChatTelemetry.OperationNameAttribute);
        Assert.Equal("conversation.id", DurableChatTelemetry.ConversationIdAttribute);
        Assert.Equal("gen_ai.request.model", DurableChatTelemetry.RequestModelAttribute);
        Assert.Equal("gen_ai.response.model", DurableChatTelemetry.ResponseModelAttribute);
        Assert.Equal("gen_ai.usage.input_tokens", DurableChatTelemetry.InputTokensAttribute);
        Assert.Equal("gen_ai.usage.output_tokens", DurableChatTelemetry.OutputTokensAttribute);
        Assert.Equal("gen_ai.tool.name", DurableChatTelemetry.ToolNameAttribute);
        Assert.Equal("gen_ai.tool.call.id", DurableChatTelemetry.ToolCallIdAttribute);
    }

    [Fact]
    public void ActivitySource_IsCreatedWithCorrectName()
    {
        Assert.Equal(DurableChatTelemetry.ActivitySourceName, DurableChatTelemetry.ActivitySource.Name);
    }
}
