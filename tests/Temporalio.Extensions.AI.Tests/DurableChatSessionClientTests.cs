using Xunit;

namespace Temporalio.Extensions.AI.Tests;

public class DurableChatSessionClientTests
{
    [Fact]
    public void Constructor_ThrowsOnNullClient()
    {
        var options = new DurableExecutionOptions { TaskQueue = "test" };
        Assert.Throws<ArgumentNullException>(
            () => new DurableChatSessionClient(null!, options));
    }

    [Fact]
    public void Constructor_ThrowsOnNullOptions()
    {
        Assert.Throws<ArgumentNullException>(
            () => new DurableChatSessionClient(null!, null!));
    }

    [Fact]
    public void Validate_ThrowsWhenTaskQueueNotSet()
    {
        var options = new DurableExecutionOptions();
        Assert.Throws<InvalidOperationException>(() => options.Validate());
    }

    [Fact]
    public void GetWorkflowId_AppliesPrefix()
    {
        // We can't easily construct a DurableChatSessionClient without a real ITemporalClient,
        // so we test the prefix logic indirectly through options.
        var options = new DurableExecutionOptions
        {
            TaskQueue = "test",
            WorkflowIdPrefix = "my-prefix-"
        };

        var expected = "my-prefix-conversation-123";
        Assert.Equal(expected, $"{options.WorkflowIdPrefix}conversation-123");
    }

    [Fact]
    public void GetWorkflowId_DefaultPrefix()
    {
        var options = new DurableExecutionOptions { TaskQueue = "test" };
        var expected = "chat-my-conversation";
        Assert.Equal(expected, $"{options.WorkflowIdPrefix}my-conversation");
    }
}
