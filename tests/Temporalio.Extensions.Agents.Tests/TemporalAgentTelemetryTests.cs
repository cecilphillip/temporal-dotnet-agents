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
        Assert.Equal("temporal.agent.schedule.create", TemporalAgentTelemetry.AgentScheduleCreateSpanName);
        Assert.Equal("temporal.agent.schedule.delayed", TemporalAgentTelemetry.AgentScheduleDelayedSpanName);
        Assert.Equal("temporal.agent.schedule.one_time", TemporalAgentTelemetry.AgentScheduleOneTimeSpanName);
    }

    [Fact]
    public void SpanNames_ScheduleSpansUseTemporalPrefix()
    {
        Assert.Equal("temporal.agent.schedule.create", TemporalAgentTelemetry.AgentScheduleCreateSpanName);
        Assert.Equal("temporal.agent.schedule.delayed", TemporalAgentTelemetry.AgentScheduleDelayedSpanName);
        Assert.Equal("temporal.agent.schedule.one_time", TemporalAgentTelemetry.AgentScheduleOneTimeSpanName);
    }

    [Fact]
    public void AttributeNames_AreExpected()
    {
        // Aligned with OpenTelemetry GenAI semantic conventions (Development tier).
        // See https://opentelemetry.io/docs/specs/semconv/gen-ai/.
        Assert.Equal("gen_ai.agent.name", TemporalAgentTelemetry.AgentNameAttribute);
        Assert.Equal("gen_ai.conversation.id", TemporalAgentTelemetry.AgentSessionIdAttribute);
        // Temporal-namespaced: OTel has no canonical request-correlation attribute.
        Assert.Equal("temporal.agent.correlation_id", TemporalAgentTelemetry.AgentCorrelationIdAttribute);
        Assert.Equal("gen_ai.usage.input_tokens", TemporalAgentTelemetry.InputTokensAttribute);
        Assert.Equal("gen_ai.usage.output_tokens", TemporalAgentTelemetry.OutputTokensAttribute);
        // Extends gen_ai.usage namespace — no canonical OTel attribute for total tokens.
        Assert.Equal("gen_ai.usage.total_tokens", TemporalAgentTelemetry.TotalTokensAttribute);
        Assert.Equal("schedule.id", TemporalAgentTelemetry.ScheduleIdAttribute);
        Assert.Equal("schedule.delay", TemporalAgentTelemetry.ScheduleDelayAttribute);
        Assert.Equal("schedule.job_id", TemporalAgentTelemetry.ScheduleJobIdAttribute);
    }

    [Fact]
    public void ActivitySource_EmitsSpanWhenListened()
    {
        // Arrange: subscribe to the ActivitySource to capture spans.
        // Use a unique run-ID tag as a fingerprint so that ActivityStopped only captures
        // *our* span — not any other activity that happens to stop on the same source
        // concurrently (framework, xUnit, or parallel tests on Linux).
        Activity? captured = null;
        var runId = Guid.NewGuid().ToString("N");

        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == TemporalAgentTelemetry.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity =>
            {
                if ((string?)activity.GetTagItem("test.run_id") == runId)
                    captured = activity;
            }
        };
        ActivitySource.AddActivityListener(listener);

        // Act: start + stop a span as AgentActivities would.
        using (var span = TemporalAgentTelemetry.ActivitySource.StartActivity("agent.turn"))
        {
            span?.SetTag("test.run_id", runId);
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
