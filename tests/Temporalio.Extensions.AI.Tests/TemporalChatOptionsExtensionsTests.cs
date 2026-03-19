using Microsoft.Extensions.AI;
using Xunit;

namespace Temporalio.Extensions.AI.Tests;

public class TemporalChatOptionsExtensionsTests
{
    [Fact]
    public void WithActivityTimeout_SetsProperty()
    {
        var options = new ChatOptions();
        options.WithActivityTimeout(TimeSpan.FromMinutes(10));

        Assert.NotNull(options.AdditionalProperties);
        Assert.Equal(TimeSpan.FromMinutes(10),
            options.AdditionalProperties![TemporalChatOptionsExtensions.ActivityTimeoutKey]);
    }

    [Fact]
    public void WithMaxRetryAttempts_SetsProperty()
    {
        var options = new ChatOptions();
        options.WithMaxRetryAttempts(5);

        Assert.NotNull(options.AdditionalProperties);
        Assert.Equal(5,
            options.AdditionalProperties![TemporalChatOptionsExtensions.MaxRetryAttemptsKey]);
    }

    [Fact]
    public void WithHeartbeatTimeout_SetsProperty()
    {
        var options = new ChatOptions();
        options.WithHeartbeatTimeout(TimeSpan.FromMinutes(3));

        Assert.NotNull(options.AdditionalProperties);
        Assert.Equal(TimeSpan.FromMinutes(3),
            options.AdditionalProperties![TemporalChatOptionsExtensions.HeartbeatTimeoutKey]);
    }

    [Fact]
    public void GetActivityTimeout_ReturnsNullWhenNotSet()
    {
        var options = new ChatOptions();
        Assert.Null(options.GetActivityTimeout());
    }

    [Fact]
    public void GetActivityTimeout_ReturnsNullForNullOptions()
    {
        ChatOptions? options = null;
        Assert.Null(options.GetActivityTimeout());
    }

    [Fact]
    public void GetActivityTimeout_ReturnsValueWhenSet()
    {
        var options = new ChatOptions();
        options.WithActivityTimeout(TimeSpan.FromMinutes(15));
        Assert.Equal(TimeSpan.FromMinutes(15), options.GetActivityTimeout());
    }

    [Fact]
    public void GetMaxRetryAttempts_ReturnsValueWhenSet()
    {
        var options = new ChatOptions();
        options.WithMaxRetryAttempts(3);
        Assert.Equal(3, options.GetMaxRetryAttempts());
    }

    [Fact]
    public void GetMaxRetryAttempts_ReturnsNullWhenNotSet()
    {
        var options = new ChatOptions();
        Assert.Null(options.GetMaxRetryAttempts());
    }

    [Fact]
    public void GetHeartbeatTimeout_ReturnsValueWhenSet()
    {
        var options = new ChatOptions();
        options.WithHeartbeatTimeout(TimeSpan.FromSeconds(30));
        Assert.Equal(TimeSpan.FromSeconds(30), options.GetHeartbeatTimeout());
    }

    [Fact]
    public void FluentChaining_Works()
    {
        var options = new ChatOptions()
            .WithActivityTimeout(TimeSpan.FromMinutes(10))
            .WithMaxRetryAttempts(3)
            .WithHeartbeatTimeout(TimeSpan.FromMinutes(2));

        Assert.Equal(TimeSpan.FromMinutes(10), options.GetActivityTimeout());
        Assert.Equal(3, options.GetMaxRetryAttempts());
        Assert.Equal(TimeSpan.FromMinutes(2), options.GetHeartbeatTimeout());
    }

    [Fact]
    public void WithActivityTimeout_ThrowsOnNull()
    {
        Assert.Throws<ArgumentNullException>(
            () => TemporalChatOptionsExtensions.WithActivityTimeout(null!, TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public void Constants_AreCorrect()
    {
        Assert.Equal("temporal.activity.timeout", TemporalChatOptionsExtensions.ActivityTimeoutKey);
        Assert.Equal("temporal.retry.max_attempts", TemporalChatOptionsExtensions.MaxRetryAttemptsKey);
        Assert.Equal("temporal.heartbeat.timeout", TemporalChatOptionsExtensions.HeartbeatTimeoutKey);
    }
}
