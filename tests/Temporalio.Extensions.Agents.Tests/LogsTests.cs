using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Temporalio.Extensions.Agents.Tests;

/// <summary>
/// Tests for the source-generated <see cref="Logs"/> methods.
/// Verifies null parameter handling and that error paths capture exception details.
/// </summary>
public class LogsTests
{
    // ── Null parameter handling (6.3) ──────────────────────────────────────────

    [Fact]
    public void LogAgentActivityStarted_NullAgentName_DoesNotThrow()
    {
        var logger = NullLogger.Instance;
        var ex = Record.Exception(() => Logs.LogAgentActivityStarted(logger, null!, null!));
        Assert.Null(ex);
    }

    [Fact]
    public void LogAgentActivityCompleted_NullParameters_DoesNotThrow()
    {
        var logger = NullLogger.Instance;
        var ex = Record.Exception(() => Logs.LogAgentActivityCompleted(
            logger, null!, null!, null, null, null));
        Assert.Null(ex);
    }

    [Fact]
    public void LogAgentActivityFailed_NullAgentName_DoesNotThrow()
    {
        var logger = NullLogger.Instance;
        var ex = Record.Exception(() => Logs.LogAgentActivityFailed(
            logger, null!, null!, new InvalidOperationException("test")));
        Assert.Null(ex);
    }

    [Fact]
    public void LogWorkflowStarted_NullAgentName_DoesNotThrow()
    {
        var logger = NullLogger.Instance;
        var ex = Record.Exception(() => Logs.LogWorkflowStarted(
            logger, null!, null!, TimeSpan.FromMinutes(5)));
        Assert.Null(ex);
    }

    [Fact]
    public void LogWorkflowTTLExpired_NullParameters_DoesNotThrow()
    {
        var logger = NullLogger.Instance;
        var ex = Record.Exception(() => Logs.LogWorkflowTTLExpired(logger, null!, null!));
        Assert.Null(ex);
    }

    [Fact]
    public void LogWorkflowShutdownRequested_NullParameters_DoesNotThrow()
    {
        var logger = NullLogger.Instance;
        var ex = Record.Exception(() => Logs.LogWorkflowShutdownRequested(logger, null!, null!));
        Assert.Null(ex);
    }

    [Fact]
    public void LogWorkflowUpdateReceived_NullParameters_DoesNotThrow()
    {
        var logger = NullLogger.Instance;
        var ex = Record.Exception(() => Logs.LogWorkflowUpdateReceived(logger, null!, null!, null!));
        Assert.Null(ex);
    }

    [Fact]
    public void LogClientSendingUpdate_NullParameters_DoesNotThrow()
    {
        var logger = NullLogger.Instance;
        var ex = Record.Exception(() => Logs.LogClientSendingUpdate(logger, null!, null!));
        Assert.Null(ex);
    }

    [Fact]
    public void LogActivityHistoryRebuilt_NullParameters_DoesNotThrow()
    {
        var logger = NullLogger.Instance;
        var ex = Record.Exception(() => Logs.LogActivityHistoryRebuilt(logger, null!, null!, 0, 0));
        Assert.Null(ex);
    }

    // ── Error path logging (6.2) ──────────────────────────────────────────────

    [Fact]
    public void LogAgentActivityFailed_CapturesExceptionDetails()
    {
        // Use a capturing logger to verify the exception is included in the log entry.
        var entries = new List<LogEntry>();
        var logger = new CapturingLogger(entries);

        var testException = new InvalidOperationException("Simulated agent failure");
        Logs.LogAgentActivityFailed(logger, "TestAgent", "ta-testagent-abc123", testException);

        Assert.Single(entries);
        var entry = entries[0];
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Equal(3, entry.EventId.Id);
        Assert.Same(testException, entry.Exception);
        Assert.Contains("TestAgent", entry.Message);
        Assert.Contains("ta-testagent-abc123", entry.Message);
    }

    [Fact]
    public void LogAgentActivityStarted_EmitsInformationLevel()
    {
        var entries = new List<LogEntry>();
        var logger = new CapturingLogger(entries);

        Logs.LogAgentActivityStarted(logger, "TestAgent", "ta-testagent-abc123");

        Assert.Single(entries);
        var entry = entries[0];
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Equal(1, entry.EventId.Id);
        Assert.Contains("TestAgent", entry.Message);
    }

    [Fact]
    public void LogAgentActivityCompleted_EmitsTokenCounts()
    {
        var entries = new List<LogEntry>();
        var logger = new CapturingLogger(entries);

        Logs.LogAgentActivityCompleted(logger, "TestAgent", "ta-testagent-abc123", 100, 50, 150);

        Assert.Single(entries);
        var entry = entries[0];
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Equal(2, entry.EventId.Id);
        Assert.Contains("100", entry.Message);
        Assert.Contains("50", entry.Message);
        Assert.Contains("150", entry.Message);
    }

    [Fact]
    public void LogAgentActivityCompleted_NullTokenCounts_FormatsGracefully()
    {
        var entries = new List<LogEntry>();
        var logger = new CapturingLogger(entries);

        Logs.LogAgentActivityCompleted(logger, "TestAgent", "ta-testagent-abc123", null, null, null);

        Assert.Single(entries);
        var entry = entries[0];
        Assert.Equal(LogLevel.Information, entry.Level);
        // Null token counts should appear as "(null)" or empty — not throw.
        Assert.Contains("TestAgent", entry.Message);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    internal record LogEntry(LogLevel Level, EventId EventId, string Message, Exception? Exception);

    /// <summary>
    /// A simple logger that captures log entries for assertion.
    /// Enabled for all log levels.
    /// </summary>
    private sealed class CapturingLogger(List<LogEntry> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            entries.Add(new LogEntry(logLevel, eventId, formatter(state, exception), exception));
        }
    }
}
