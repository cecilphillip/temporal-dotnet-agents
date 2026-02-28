using System.Diagnostics;
using Xunit;

namespace Temporalio.Extensions.Agents.Tests;

public class TemporalAgentTelemetryTests
{
    [Fact]
    public void ActivitySourceName_IsExpected()
    {
        Assert.Equal("Temporalio.Extensions.Agents", TemporalAgentTelemetry.ActivitySourceName);
    }

    [Fact]
    public void ActivitySource_HasCorrectName()
    {
        Assert.Equal(TemporalAgentTelemetry.ActivitySourceName, TemporalAgentTelemetry.ActivitySource.Name);
    }

    [Fact]
    public void SpanNames_AreExpected()
    {
        Assert.Equal("agent.turn", TemporalAgentTelemetry.AgentTurnSpanName);
        Assert.Equal("agent.client.send", TemporalAgentTelemetry.AgentClientSendSpanName);
    }

    [Fact]
    public void AttributeNames_AreExpected()
    {
        Assert.Equal("agent.name", TemporalAgentTelemetry.AgentNameAttribute);
        Assert.Equal("agent.session_id", TemporalAgentTelemetry.AgentSessionIdAttribute);
        Assert.Equal("agent.correlation_id", TemporalAgentTelemetry.AgentCorrelationIdAttribute);
        Assert.Equal("agent.input_tokens", TemporalAgentTelemetry.InputTokensAttribute);
        Assert.Equal("agent.output_tokens", TemporalAgentTelemetry.OutputTokensAttribute);
        Assert.Equal("agent.total_tokens", TemporalAgentTelemetry.TotalTokensAttribute);
    }

    [Fact]
    public void ActivitySource_EmitsSpanWhenListened()
    {
        // Arrange: subscribe to the ActivitySource to capture spans.
        Activity? captured = null;

        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == TemporalAgentTelemetry.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity => captured = activity
        };
        ActivitySource.AddActivityListener(listener);

        // Act: start + stop a span as AgentActivities would.
        using (var span = TemporalAgentTelemetry.ActivitySource.StartActivity("agent.turn"))
        {
            span?.SetTag(TemporalAgentTelemetry.AgentNameAttribute, "TestAgent");
            span?.SetTag(TemporalAgentTelemetry.AgentSessionIdAttribute, "ta-testagent-abc123");
        }

        // Assert
        Assert.NotNull(captured);
        Assert.Equal("agent.turn", captured.OperationName);
        Assert.Equal("TestAgent", captured.GetTagItem(TemporalAgentTelemetry.AgentNameAttribute));
        Assert.Equal("ta-testagent-abc123", captured.GetTagItem(TemporalAgentTelemetry.AgentSessionIdAttribute));
    }
}
