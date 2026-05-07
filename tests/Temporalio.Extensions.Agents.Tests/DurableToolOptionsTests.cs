using Xunit;

namespace Temporalio.Extensions.Agents.Tests;

public class DurableToolOptionsTests
{
    [Fact]
    public void Defaults_AreNull()
    {
        var opts = new DurableToolOptions();
        Assert.Null(opts.StartToCloseTimeout);
        Assert.Null(opts.HeartbeatTimeout);
        Assert.Null(opts.RetryPolicy);
    }

    [Fact]
    public void NoRetry_SetsMaximumAttemptsToOne()
    {
        var opts = new DurableToolOptions();
        opts.NoRetry();
        Assert.NotNull(opts.RetryPolicy);
        Assert.Equal(1, opts.RetryPolicy!.MaximumAttempts);
    }

    [Fact]
    public void NoRetry_ReturnsSameInstance()
    {
        var opts = new DurableToolOptions();
        Assert.Same(opts, opts.NoRetry());
    }

    [Fact]
    public void WithMaxAttempts_SetsMaximumAttempts()
    {
        var opts = new DurableToolOptions();
        opts.WithMaxAttempts(5);
        Assert.NotNull(opts.RetryPolicy);
        Assert.Equal(5, opts.RetryPolicy!.MaximumAttempts);
    }

    [Fact]
    public void WithMaxAttempts_ReturnsSameInstance()
    {
        var opts = new DurableToolOptions();
        Assert.Same(opts, opts.WithMaxAttempts(3));
    }

    [Fact]
    public void WithMaxAttempts_Zero_Throws()
    {
        var opts = new DurableToolOptions();
        Assert.Throws<ArgumentOutOfRangeException>(() => opts.WithMaxAttempts(0));
    }

    [Fact]
    public void WithMaxAttempts_Negative_Throws()
    {
        var opts = new DurableToolOptions();
        Assert.Throws<ArgumentOutOfRangeException>(() => opts.WithMaxAttempts(-1));
    }

    [Fact]
    public void WithTimeout_SetsStartToCloseTimeout()
    {
        var opts = new DurableToolOptions();
        opts.WithTimeout(TimeSpan.FromSeconds(30));
        Assert.Equal(TimeSpan.FromSeconds(30), opts.StartToCloseTimeout);
    }

    [Fact]
    public void WithTimeout_ReturnsSameInstance()
    {
        var opts = new DurableToolOptions();
        Assert.Same(opts, opts.WithTimeout(TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void WithTimeout_Zero_Throws()
    {
        var opts = new DurableToolOptions();
        Assert.Throws<ArgumentOutOfRangeException>(() => opts.WithTimeout(TimeSpan.Zero));
    }

    [Fact]
    public void WithTimeout_Negative_Throws()
    {
        var opts = new DurableToolOptions();
        Assert.Throws<ArgumentOutOfRangeException>(() => opts.WithTimeout(TimeSpan.FromSeconds(-1)));
    }

    [Fact]
    public void Sugar_Chains_Fluently()
    {
        var opts = new DurableToolOptions()
            .WithTimeout(TimeSpan.FromSeconds(10))
            .NoRetry();

        Assert.Equal(TimeSpan.FromSeconds(10), opts.StartToCloseTimeout);
        Assert.Equal(1, opts.RetryPolicy!.MaximumAttempts);
    }
}
