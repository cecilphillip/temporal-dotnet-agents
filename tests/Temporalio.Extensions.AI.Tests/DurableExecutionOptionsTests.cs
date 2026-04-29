using Microsoft.Extensions.AI;
using Xunit;

namespace Temporalio.Extensions.AI.Tests;

public class DurableExecutionOptionsTests
{
    [Fact]
    public void Defaults_AreCorrect()
    {
        var options = new DurableExecutionOptions();

        Assert.Null(options.TaskQueue);
        Assert.Equal(TimeSpan.FromMinutes(5), options.ActivityTimeout);
        Assert.Null(options.RetryPolicy);
        Assert.Equal("chat-", options.WorkflowIdPrefix);
        Assert.Equal(TimeSpan.FromDays(14), options.SessionTimeToLive);
        Assert.False(options.EnableSessionManagement);
        Assert.Equal(TimeSpan.FromMinutes(2), options.HeartbeatTimeout);
    }

    [Fact]
    public void Validate_ThrowsWhenTaskQueueIsNull()
    {
        var options = new DurableExecutionOptions();

        var ex = Assert.Throws<InvalidOperationException>(() => options.Validate());
        Assert.Contains("TaskQueue", ex.Message);
    }

    [Fact]
    public void Validate_ThrowsWhenTaskQueueIsEmpty()
    {
        var options = new DurableExecutionOptions { TaskQueue = "" };

        var ex = Assert.Throws<InvalidOperationException>(() => options.Validate());
        Assert.Contains("TaskQueue", ex.Message);
    }

    [Fact]
    public void Validate_SucceedsWhenTaskQueueIsSet()
    {
        var options = new DurableExecutionOptions { TaskQueue = "my-queue" };

        options.Validate(); // Should not throw
    }

    [Fact]
    public void Properties_CanBeSet()
    {
        var retryPolicy = new Temporalio.Common.RetryPolicy { MaximumAttempts = 3 };
        var options = new DurableExecutionOptions
        {
            TaskQueue = "test-queue",
            ActivityTimeout = TimeSpan.FromMinutes(10),
            RetryPolicy = retryPolicy,
            WorkflowIdPrefix = "custom-",
            SessionTimeToLive = TimeSpan.FromDays(7),
            EnableSessionManagement = true,
            HeartbeatTimeout = TimeSpan.FromMinutes(5),
        };

        Assert.Equal("test-queue", options.TaskQueue);
        Assert.Equal(TimeSpan.FromMinutes(10), options.ActivityTimeout);
        Assert.Same(retryPolicy, options.RetryPolicy);
        Assert.Equal("custom-", options.WorkflowIdPrefix);
        Assert.Equal(TimeSpan.FromDays(7), options.SessionTimeToLive);
        Assert.True(options.EnableSessionManagement);
        Assert.Equal(TimeSpan.FromMinutes(5), options.HeartbeatTimeout);
    }

    [Fact]
    public void DefaultChatClientKey_IsNullByDefault()
    {
        var options = new DurableExecutionOptions();
        Assert.Null(options.DefaultChatClientKey);
    }

    [Fact]
    public void DefaultChatClientKey_CanBeSet()
    {
        var options = new DurableExecutionOptions { DefaultChatClientKey = "gpt-4o" };
        Assert.Equal("gpt-4o", options.DefaultChatClientKey);
    }

    [Fact]
    public void HistoryReducer_IsNullByDefault()
    {
        var options = new DurableExecutionOptions();
        Assert.Null(options.HistoryReducer);
    }

    [Fact]
    public void HistoryReducer_CanBeSetWithFuncChatReducer()
    {
        var reducer = new FuncChatReducer(msgs => msgs.TakeLast(10).ToList());
        var options = new DurableExecutionOptions { HistoryReducer = reducer };

        Assert.Same(reducer, options.HistoryReducer);
        Assert.IsType<FuncChatReducer>(options.HistoryReducer);
    }

    [Fact]
    public async Task FuncChatReducer_AppliesDelegateToMessages()
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "msg1"),
            new(ChatRole.User, "msg2"),
            new(ChatRole.User, "msg3"),
        };
        var reducer = new FuncChatReducer(msgs => msgs.TakeLast(2).ToList());

        var result = (await reducer.ReduceAsync(messages, CancellationToken.None)).ToList();

        Assert.Equal(2, result.Count);
        Assert.Equal("msg2", result[0].Text);
        Assert.Equal("msg3", result[1].Text);
    }

    [Fact]
    public async Task FuncChatReducer_CompletesInline()
    {
        var reducer = new FuncChatReducer(msgs => msgs.ToList());
        var messages = new List<ChatMessage> { new(ChatRole.User, "hello") };

        var task = reducer.ReduceAsync(messages, CancellationToken.None);

        Assert.True(task.IsCompletedSuccessfully);
        var result = await task;
        Assert.Single(result);
    }
}
