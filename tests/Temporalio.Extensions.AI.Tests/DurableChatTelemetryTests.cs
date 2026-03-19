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
        Assert.Equal("durable_chat.send", DurableChatTelemetry.ChatSendSpanName);
        Assert.Equal("durable_chat.turn", DurableChatTelemetry.ChatTurnSpanName);
        Assert.Equal("durable_function.invoke", DurableChatTelemetry.FunctionInvokeSpanName);
    }

    [Fact]
    public void AttributeNames_AreCorrect()
    {
        Assert.Equal("conversation.id", DurableChatTelemetry.ConversationIdAttribute);
        Assert.Equal("gen_ai.request.model", DurableChatTelemetry.RequestModelAttribute);
        Assert.Equal("gen_ai.response.model", DurableChatTelemetry.ResponseModelAttribute);
        Assert.Equal("gen_ai.usage.input_tokens", DurableChatTelemetry.InputTokensAttribute);
        Assert.Equal("gen_ai.usage.output_tokens", DurableChatTelemetry.OutputTokensAttribute);
        Assert.Equal("gen_ai.tool.name", DurableChatTelemetry.ToolNameAttribute);
        Assert.Equal("gen_ai.tool.call_id", DurableChatTelemetry.ToolCallIdAttribute);
    }

    [Fact]
    public void ActivitySource_IsCreatedWithCorrectName()
    {
        Assert.Equal(DurableChatTelemetry.ActivitySourceName, DurableChatTelemetry.ActivitySource.Name);
    }
}
