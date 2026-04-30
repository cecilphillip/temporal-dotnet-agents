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
    public void Validate_ThrowsWhenActivityTimeoutIsZero()
    {
        var options = new DurableExecutionOptions { TaskQueue = "q", ActivityTimeout = TimeSpan.Zero };

        var ex = Assert.Throws<InvalidOperationException>(() => options.Validate());
        Assert.Contains("ActivityTimeout", ex.Message);
    }

    [Fact]
    public void Validate_ThrowsWhenActivityTimeoutIsNegative()
    {
        var options = new DurableExecutionOptions { TaskQueue = "q", ActivityTimeout = TimeSpan.FromSeconds(-1) };

        var ex = Assert.Throws<InvalidOperationException>(() => options.Validate());
        Assert.Contains("ActivityTimeout", ex.Message);
    }

    [Fact]
    public void Validate_ThrowsWhenHeartbeatTimeoutIsZero()
    {
        var options = new DurableExecutionOptions { TaskQueue = "q", HeartbeatTimeout = TimeSpan.Zero };

        var ex = Assert.Throws<InvalidOperationException>(() => options.Validate());
        Assert.Contains("HeartbeatTimeout", ex.Message);
    }

    [Fact]
    public void Validate_ThrowsWhenSessionTimeToLiveIsZero()
    {
        var options = new DurableExecutionOptions { TaskQueue = "q", SessionTimeToLive = TimeSpan.Zero };

        var ex = Assert.Throws<InvalidOperationException>(() => options.Validate());
        Assert.Contains("SessionTimeToLive", ex.Message);
    }

    [Fact]
    public void Validate_ThrowsWhenApprovalTimeoutIsZero()
    {
        var options = new DurableExecutionOptions { TaskQueue = "q", ApprovalTimeout = TimeSpan.Zero };

        var ex = Assert.Throws<InvalidOperationException>(() => options.Validate());
        Assert.Contains("ApprovalTimeout", ex.Message);
    }

    [Fact]
    public void HistoryReducer_IsNullByDefault()
    {
        var options = new DurableExecutionOptions();
        Assert.Null(options.HistoryReducer);
    }

    [Fact]
    public void HistoryReducer_CanBeSetWithEntryShapedDelegate()
    {
        Func<IList<DurableSessionEntry>, IList<DurableSessionEntry>> reducer =
            entries => entries.TakeLast(10).ToList();
        var options = new DurableExecutionOptions { HistoryReducer = reducer };

        Assert.Same(reducer, options.HistoryReducer);
    }

    [Fact]
    public void HistoryReducer_AppliesDelegateToEntries()
    {
        var entries = new List<DurableSessionEntry>
        {
            DurableSessionRequest.FromMessages([new ChatMessage(ChatRole.User, "msg1")], "id1", DateTimeOffset.UtcNow),
            DurableSessionRequest.FromMessages([new ChatMessage(ChatRole.User, "msg2")], "id2", DateTimeOffset.UtcNow),
            DurableSessionRequest.FromMessages([new ChatMessage(ChatRole.User, "msg3")], "id3", DateTimeOffset.UtcNow),
        };

        Func<IList<DurableSessionEntry>, IList<DurableSessionEntry>> reducer =
            xs => xs.TakeLast(2).ToList();
        var result = reducer(entries);

        Assert.Equal(2, result.Count);
        Assert.Equal("id2", result[0].CorrelationId);
        Assert.Equal("id3", result[1].CorrelationId);
    }

    // ── MaxEntryCount ──────────────────────────────────────────────────

    [Fact]
    public void MaxEntryCount_DefaultsTo1000()
    {
        var options = new DurableExecutionOptions();
        Assert.Equal(1000, options.MaxEntryCount);
    }

    [Fact]
    public void MaxEntryCount_CanBeSet()
    {
        var options = new DurableExecutionOptions { TaskQueue = "q", MaxEntryCount = 500 };
        Assert.Equal(500, options.MaxEntryCount);
    }

    [Fact]
    public void Validate_ThrowsWhenMaxEntryCountIsZero()
    {
        var options = new DurableExecutionOptions { TaskQueue = "q", MaxEntryCount = 0 };

        var ex = Assert.Throws<InvalidOperationException>(() => options.Validate());
        Assert.Contains("MaxEntryCount", ex.Message);
    }

    [Fact]
    public void Validate_ThrowsWhenMaxEntryCountIsNegative()
    {
        var options = new DurableExecutionOptions { TaskQueue = "q", MaxEntryCount = -5 };

        var ex = Assert.Throws<InvalidOperationException>(() => options.Validate());
        Assert.Contains("MaxEntryCount", ex.Message);
    }
}
