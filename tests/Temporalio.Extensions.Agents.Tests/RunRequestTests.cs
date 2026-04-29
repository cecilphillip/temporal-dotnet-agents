using System.Text.Json;
using Microsoft.Extensions.AI;
using Temporalio.Extensions.Agents.Workflows;
using Xunit;

namespace Temporalio.Extensions.Agents.Tests;

public class RunRequestTests
{
    [Fact]
    public void StringCtor_Default_SetsUserRole()
    {
        var request = new RunRequest("Hello");
        Assert.Single(request.Messages);
        Assert.Equal(ChatRole.User, request.Messages[0].Role);
    }

    [Fact]
    public void StringCtor_Default_SetsMessageText()
    {
        var request = new RunRequest("Hello, world!");
        Assert.Equal("Hello, world!", request.Messages[0].Text);
    }

    [Fact]
    public void StringCtor_WithExplicitRole_SetsRole()
    {
        var request = new RunRequest("System message", role: ChatRole.System);
        Assert.Equal(ChatRole.System, request.Messages[0].Role);
    }

    [Fact]
    public void CorrelationId_DefaultsToNull()
    {
        // CorrelationId no longer auto-generates a Guid; callers must set it explicitly so
        // workflow callers can assign Workflow.NewGuid() (deterministic) and external callers
        // can assign Guid.NewGuid() (non-deterministic, fine outside workflow context).
        var request = new RunRequest("test");
        Assert.Null(request.CorrelationId);
    }

    [Fact]
    public void CorrelationId_IsPreservedWhenSet()
    {
        var request = new RunRequest("test") { CorrelationId = "abc-123" };
        Assert.Equal("abc-123", request.CorrelationId);
    }

    [Fact]
    public void EnableToolCalls_DefaultsToTrue()
    {
        var request = new RunRequest("test");
        Assert.True(request.EnableToolCalls);
    }

    [Fact]
    public void EnableToolNames_DefaultsToNull()
    {
        var request = new RunRequest("test");
        Assert.Null(request.EnableToolNames);
    }

    [Fact]
    public void ResponseFormat_DefaultsToNull()
    {
        var request = new RunRequest("test");
        Assert.Null(request.ResponseFormat);
    }

    [Fact]
    public void ListCtor_PreservesMessages()
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, "System prompt"),
            new(ChatRole.User, "User message"),
        };
        var request = new RunRequest(messages);
        Assert.Equal(2, request.Messages.Count);
        Assert.Equal(ChatRole.System, request.Messages[0].Role);
        Assert.Equal(ChatRole.User, request.Messages[1].Role);
    }

    [Fact]
    public void ListCtor_WithOptions_PreservesOptions()
    {
        var request = new RunRequest(
            [new ChatMessage(ChatRole.User, "test")],
            responseFormat: ChatResponseFormat.Json,
            enableToolCalls: false,
            enableToolNames: ["myTool"]);

        Assert.Equal(ChatResponseFormat.Json, request.ResponseFormat);
        Assert.False(request.EnableToolCalls);
        Assert.Contains("myTool", request.EnableToolNames!);
    }

    [Fact]
    public void JsonRoundTrip_PreservesCorrelationId()
    {
        var original = new RunRequest("test") { CorrelationId = "round-trip-corr" };
        var json = JsonSerializer.Serialize(original);
        var deserialized = JsonSerializer.Deserialize<RunRequest>(json);
        Assert.Equal(original.CorrelationId, deserialized!.CorrelationId);
    }

    [Fact]
    public void JsonRoundTrip_PreservesEnableToolCalls()
    {
        var original = new RunRequest("test", enableToolCalls: false);
        var json = JsonSerializer.Serialize(original);
        var deserialized = JsonSerializer.Deserialize<RunRequest>(json);
        Assert.False(deserialized!.EnableToolCalls);
    }
}
