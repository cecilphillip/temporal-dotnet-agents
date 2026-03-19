using Microsoft.Extensions.AI;
using Temporalio.Converters;
using Xunit;

namespace Temporalio.Extensions.AI.Tests;

public class DurableAIDataConverterTests
{
    [Fact]
    public void Instance_IsNotNull()
    {
        Assert.NotNull(DurableAIDataConverter.Instance);
    }

    [Fact]
    public void Instance_HasPayloadConverter()
    {
        Assert.NotNull(DurableAIDataConverter.Instance.PayloadConverter);
    }

    [Fact]
    public void Instance_HasFailureConverter()
    {
        Assert.NotNull(DurableAIDataConverter.Instance.FailureConverter);
    }

    [Fact]
    public void PayloadConverter_CanSerializeChatMessage()
    {
        var converter = DurableAIDataConverter.Instance.PayloadConverter;
        var message = new ChatMessage(ChatRole.User, "Hello!");

        var payload = converter.ToPayload(message);
        Assert.NotNull(payload);

        var deserialized = (ChatMessage)converter.ToValue(payload, typeof(ChatMessage));
        Assert.NotNull(deserialized);
        Assert.Equal(ChatRole.User, deserialized.Role);
        Assert.Equal("Hello!", deserialized.Text);
    }

    [Fact]
    public void PayloadConverter_PreservesAIContentPolymorphism()
    {
        var converter = DurableAIDataConverter.Instance.PayloadConverter;

        var functionCall = new FunctionCallContent("call-1", "get_weather",
            new Dictionary<string, object?> { ["city"] = "Seattle" });
        var message = new ChatMessage(ChatRole.Assistant, [functionCall]);

        var payload = converter.ToPayload(message);
        var deserialized = (ChatMessage)converter.ToValue(payload, typeof(ChatMessage));

        Assert.NotNull(deserialized);
        Assert.Single(deserialized.Contents);
        var content = deserialized.Contents[0];
        Assert.IsType<FunctionCallContent>(content);

        var fc = (FunctionCallContent)content;
        Assert.Equal("call-1", fc.CallId);
        Assert.Equal("get_weather", fc.Name);
    }

    [Fact]
    public void PayloadConverter_CanSerializeChatResponse()
    {
        var converter = DurableAIDataConverter.Instance.PayloadConverter;

        var response = new ChatResponse([new ChatMessage(ChatRole.Assistant, "World!")])
        {
            ModelId = "test-model",
        };

        var payload = converter.ToPayload(response);
        var deserialized = (ChatResponse)converter.ToValue(payload, typeof(ChatResponse));

        Assert.NotNull(deserialized);
        Assert.Equal("test-model", deserialized.ModelId);
        Assert.Single(deserialized.Messages);
    }
}
