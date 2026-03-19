using System.Text.Json;
using Microsoft.Extensions.AI;
using Xunit;

namespace Temporalio.Extensions.AI.Tests;

public class SerializationTests
{
    private static readonly JsonSerializerOptions Options = AIJsonUtilities.DefaultOptions;

    [Fact]
    public void DurableChatInput_RoundTrips()
    {
        var input = new DurableChatInput
        {
            Messages = [
                new ChatMessage(ChatRole.User, "Hello"),
                new ChatMessage(ChatRole.Assistant, "Hi there!"),
            ],
            ConversationId = "conv-123",
            TurnNumber = 1,
        };

        var json = JsonSerializer.Serialize(input, Options);
        var deserialized = JsonSerializer.Deserialize<DurableChatInput>(json, Options);

        Assert.NotNull(deserialized);
        Assert.Equal(2, deserialized!.Messages.Count);
        Assert.Equal("conv-123", deserialized.ConversationId);
        Assert.Equal(1, deserialized.TurnNumber);
    }

    [Fact]
    public void DurableChatInput_WithTextContent_RoundTrips()
    {
        var input = new DurableChatInput
        {
            Messages = [
                new ChatMessage(ChatRole.User, [new TextContent("What is 2+2?")]),
            ],
            TurnNumber = 1,
        };

        var json = JsonSerializer.Serialize(input, Options);
        var deserialized = JsonSerializer.Deserialize<DurableChatInput>(json, Options);

        Assert.NotNull(deserialized);
        Assert.Single(deserialized!.Messages);
        var content = deserialized.Messages[0].Contents[0];
        Assert.IsType<TextContent>(content);
        Assert.Equal("What is 2+2?", ((TextContent)content).Text);
    }

    [Fact]
    public void DurableChatInput_WithFunctionCallContent_RoundTrips()
    {
        var functionCall = new FunctionCallContent("call-1", "get_weather",
            new Dictionary<string, object?> { ["city"] = "Seattle" });

        var input = new DurableChatInput
        {
            Messages = [
                new ChatMessage(ChatRole.Assistant, [functionCall]),
            ],
            TurnNumber = 1,
        };

        var json = JsonSerializer.Serialize(input, Options);
        var deserialized = JsonSerializer.Deserialize<DurableChatInput>(json, Options);

        Assert.NotNull(deserialized);
        var content = deserialized!.Messages[0].Contents[0];
        Assert.IsType<FunctionCallContent>(content);
        var fc = (FunctionCallContent)content;
        Assert.Equal("call-1", fc.CallId);
        Assert.Equal("get_weather", fc.Name);
    }

    [Fact]
    public void DurableChatInput_WithFunctionResultContent_RoundTrips()
    {
        var functionResult = new FunctionResultContent("call-1", "72°F");

        var input = new DurableChatInput
        {
            Messages = [
                new ChatMessage(ChatRole.Tool, [functionResult]),
            ],
            TurnNumber = 1,
        };

        var json = JsonSerializer.Serialize(input, Options);
        var deserialized = JsonSerializer.Deserialize<DurableChatInput>(json, Options);

        Assert.NotNull(deserialized);
        var content = deserialized!.Messages[0].Contents[0];
        Assert.IsType<FunctionResultContent>(content);
    }

    [Fact]
    public void DurableFunctionInput_RoundTrips()
    {
        var input = new DurableFunctionInput
        {
            FunctionName = "get_weather",
            Arguments = new Dictionary<string, object?> { ["city"] = "Seattle" },
            CallId = "call-42",
        };

        var json = JsonSerializer.Serialize(input, Options);
        var deserialized = JsonSerializer.Deserialize<DurableFunctionInput>(json, Options);

        Assert.NotNull(deserialized);
        Assert.Equal("get_weather", deserialized!.FunctionName);
        Assert.Equal("call-42", deserialized.CallId);
        Assert.NotNull(deserialized.Arguments);
    }

    [Fact]
    public void DurableFunctionOutput_RoundTrips()
    {
        var output = new DurableFunctionOutput { Result = "72°F and sunny" };

        var json = JsonSerializer.Serialize(output, Options);
        var deserialized = JsonSerializer.Deserialize<DurableFunctionOutput>(json, Options);

        Assert.NotNull(deserialized);
        Assert.NotNull(deserialized!.Result);
    }

    [Fact]
    public void DurableChatOutput_RoundTrips()
    {
        var response = new ChatResponse([new ChatMessage(ChatRole.Assistant, "Hello!")])
        {
            ModelId = "gpt-4o",
        };
        var output = new DurableChatOutput { Response = response };

        var json = JsonSerializer.Serialize(output, Options);
        var deserialized = JsonSerializer.Deserialize<DurableChatOutput>(json, Options);

        Assert.NotNull(deserialized);
        Assert.NotNull(deserialized!.Response);
        Assert.Equal("gpt-4o", deserialized.Response.ModelId);
        Assert.Single(deserialized.Response.Messages);
    }

    [Fact]
    public void DurableChatWorkflowInput_RoundTrips()
    {
        var input = new DurableChatWorkflowInput
        {
            TimeToLive = TimeSpan.FromHours(1),
            ActivityTimeout = TimeSpan.FromMinutes(10),
            HeartbeatTimeout = TimeSpan.FromMinutes(3),
        };

        var json = JsonSerializer.Serialize(input, Options);
        var deserialized = JsonSerializer.Deserialize<DurableChatWorkflowInput>(json, Options);

        Assert.NotNull(deserialized);
        Assert.Equal(TimeSpan.FromHours(1), deserialized!.TimeToLive);
        Assert.Equal(TimeSpan.FromMinutes(10), deserialized.ActivityTimeout);
    }

    [Fact]
    public void ChatOptions_SerializableFields_Preserved()
    {
        var chatOptions = new ChatOptions
        {
            Temperature = 0.7f,
            MaxOutputTokens = 1000,
            ModelId = "gpt-4o",
            TopP = 0.9f,
            Seed = 42,
        };

        var json = JsonSerializer.Serialize(chatOptions, Options);
        var deserialized = JsonSerializer.Deserialize<ChatOptions>(json, Options);

        Assert.NotNull(deserialized);
        Assert.Equal(0.7f, deserialized!.Temperature);
        Assert.Equal(1000, deserialized.MaxOutputTokens);
        Assert.Equal("gpt-4o", deserialized.ModelId);
        Assert.Equal(0.9f, deserialized.TopP);
        Assert.Equal(42, deserialized.Seed);
    }
}
