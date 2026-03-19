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
}
