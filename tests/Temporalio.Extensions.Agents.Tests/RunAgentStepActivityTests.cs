using System.Text.Json;
using Microsoft.Extensions.AI;
using Temporalio.Converters;
using Temporalio.Extensions.AI;
using Temporalio.Extensions.Agents.Workflows;
using Temporalio.Extensions.Agents.Tests.StepMode;
using Xunit;

namespace Temporalio.Extensions.Agents.Tests;

/// <summary>
/// Unit-level coverage for the step-mode activity input/output contracts and the supporting
/// types. Full activity execution requires a Temporal harness — that path is exercised in the
/// step-mode integration tests. The tests here pin the behavioral guarantees that do not
/// require <c>ActivityExecutionContext.Current</c>.
/// </summary>
public class RunAgentStepActivityTests
{
    [Fact]
    public void AgentStepInput_RoundTripsThroughTemporalAgentDataConverter()
    {
        // The step input is serialized into the Temporal ActivityScheduled event, so the
        // shape must round-trip cleanly through the agent data converter.
        var input = new AgentStepInput
        {
            AgentName = "MyAgent",
            Request = new RunRequest("hi") { CorrelationId = "c1" },
            AccumulatedMessages =
            [
                new ChatMessage(ChatRole.User, "hi"),
            ],
        };

        var json = JsonSerializer.Serialize(input, TemporalAgentJsonUtilities.DefaultOptions);
        var roundTripped = JsonSerializer.Deserialize<AgentStepInput>(json, TemporalAgentJsonUtilities.DefaultOptions);

        Assert.NotNull(roundTripped);
        Assert.Equal(input.AgentName, roundTripped.AgentName);
        Assert.Equal(input.Request.CorrelationId, roundTripped.Request.CorrelationId);
        Assert.Single(roundTripped.AccumulatedMessages);
    }

    [Fact]
    public void AgentStepResult_WithFunctionCallContent_RoundTripsThroughDataConverter()
    {
        // Risk-validation gate from the plan §"Risks and Validation Gates" item 1:
        // FunctionCallContent must round-trip through TemporalAgentDataConverter.
        // If [JsonDerivedType] on AIContent does not cover FunctionCallContent at this layer,
        // the activity output would deserialize as base AIContent and the workflow would
        // see no tool calls.
        var fc = new FunctionCallContent("call-1", "send_email",
            new Dictionary<string, object?> { ["to"] = "user@example.com" });

        var assistantMessage = new ChatMessage(ChatRole.Assistant, [fc]);
        var stepResult = new AgentStepResult
        {
            IsFinal = false,
            AssistantMessage = assistantMessage,
            ToolCalls = [fc],
        };

        var json = JsonSerializer.Serialize(stepResult, TemporalAgentJsonUtilities.DefaultOptions);
        var roundTripped = JsonSerializer.Deserialize<AgentStepResult>(
            json, TemporalAgentJsonUtilities.DefaultOptions);

        Assert.NotNull(roundTripped);
        Assert.False(roundTripped.IsFinal);
        Assert.NotNull(roundTripped.ToolCalls);
        Assert.Single(roundTripped.ToolCalls);
        Assert.Equal("send_email", roundTripped.ToolCalls[0].Name);
        Assert.Equal("call-1", roundTripped.ToolCalls[0].CallId);

        // Function call should also be visible inside AssistantMessage.Contents after round-trip.
        var fcAfter = Assert.Single(roundTripped.AssistantMessage.Contents.OfType<FunctionCallContent>());
        Assert.Equal("send_email", fcAfter.Name);
    }

    [Fact]
    public void AgentStepResult_FinalAnswer_HasNoToolCalls()
    {
        // Sanity check: when LLM returns a final text response, IsFinal=true and ToolCalls
        // can be omitted (so the workflow loop terminates).
        var result = new AgentStepResult
        {
            IsFinal = true,
            AssistantMessage = new ChatMessage(ChatRole.Assistant, "Done."),
        };

        Assert.True(result.IsFinal);
        Assert.Null(result.ToolCalls);
    }

    [Fact]
    public void AgentStepResult_FunctionResultContent_RoundTripsThroughDataConverter()
    {
        // FunctionResultContent is appended to the workflow's accumulated messages between
        // step iterations — it travels through the workflow → activity payload boundary on
        // the next RunAgentStepAsync call inside AgentStepInput.AccumulatedMessages. Pin the
        // round-trip so the next LLM call sees the tool result intact.
        var frc = new FunctionResultContent("call-1", "ok");
        var toolMessage = new ChatMessage(ChatRole.Tool, [frc]);

        var input = new AgentStepInput
        {
            AgentName = "Agent",
            Request = new RunRequest("hi") { CorrelationId = "c1" },
            AccumulatedMessages = [toolMessage],
        };

        var json = JsonSerializer.Serialize(input, TemporalAgentJsonUtilities.DefaultOptions);
        var roundTripped = JsonSerializer.Deserialize<AgentStepInput>(
            json, TemporalAgentJsonUtilities.DefaultOptions);

        Assert.NotNull(roundTripped);
        var msg = Assert.Single(roundTripped.AccumulatedMessages);
        var frcAfter = Assert.Single(msg.Contents.OfType<FunctionResultContent>());
        Assert.Equal("call-1", frcAfter.CallId);
    }

    [Fact]
    public async Task ScriptedChatClient_DeliversFunctionCallContent_WhenScripted()
    {
        // Risk-validation: confirm the test fixture itself can deliver FunctionCallContent
        // through a streaming GetStreamingResponseAsync call (the path RunAgentStepAsync uses).
        // This pins that the scaffolding can drive the loop deterministically.
        var fc = new FunctionCallContent("call-1", "echo",
            new Dictionary<string, object?> { ["input"] = "hi" });
        var client = ScriptedChatClient.WithToolCallsThenFinal([fc], "Final answer.");

        var response = await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "hi")]);

        var assistantMessage = response.Messages[0];
        var actualToolCall = Assert.Single(assistantMessage.Contents.OfType<FunctionCallContent>());
        Assert.Equal("echo", actualToolCall.Name);
    }

    [Fact]
    public async Task ScriptedChatClient_DeliversFinalText_OnSecondCall()
    {
        var fc = new FunctionCallContent("call-1", "echo", null);
        var client = ScriptedChatClient.WithToolCallsThenFinal([fc], "Final answer.");

        // First call returns the tool-call response.
        _ = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]);

        // Second call (after we'd have invoked the tool) returns the final text.
        var second = await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "hi")]);
        Assert.Equal("Final answer.", second.Messages[0].Text);
    }

    [Fact]
    public void TemporalAgentsOptions_EnablePerToolActivities_DefaultsToFalse()
    {
        var options = new TemporalAgentsOptions();
        Assert.False(options.EnablePerToolActivities);
        Assert.Null(options.PerToolActivityOptions);
        Assert.Equal(20, options.MaxToolCallsPerTurn);
    }

    [Fact]
    public void AgentStepResult_FunctionCallContent_RoundTripsThroughTemporalPayloadConverter()
    {
        // Belt-and-braces: also verify against the live DefaultPayloadConverter using the
        // agent JSON options, mirroring what the Temporal data converter uses on the wire.
        var fc = new FunctionCallContent("call-1", "echo",
            new Dictionary<string, object?> { ["input"] = "hi" });
        var result = new AgentStepResult
        {
            IsFinal = false,
            AssistantMessage = new ChatMessage(ChatRole.Assistant, [fc]),
            ToolCalls = [fc],
        };

        IPayloadConverter pc = new DefaultPayloadConverter(TemporalAgentJsonUtilities.DefaultOptions);
        var payload = pc.ToPayload(result);
        var roundTripped = pc.ToValue<AgentStepResult>(payload);

        Assert.NotNull(roundTripped);
        Assert.NotNull(roundTripped.ToolCalls);
        Assert.Single(roundTripped.ToolCalls);
        Assert.Equal("echo", roundTripped.ToolCalls[0].Name);
    }
}
