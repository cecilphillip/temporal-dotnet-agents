using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Temporalio.Workflows;

namespace Temporalio.Extensions.Agents.Workflows;

/// <summary>
/// A simple, fire-and-forget Temporal workflow for scheduled or deferred agent runs.
/// Unlike <see cref="AgentWorkflow"/>, this workflow carries no persisted history,
/// no StateBag, no TTL loop, and no <c>[WorkflowUpdate]</c> handlers — it executes
/// the durable-agent dispatch loop in-place and exits.
/// </summary>
/// <remarks>
/// Workflow ID convention: <c>ta-{agentName}-scheduled-{scheduleId}</c>.
/// </remarks>
[Workflow("Temporalio.Extensions.Agents.AgentJobWorkflow")]
internal sealed class AgentJobWorkflow
{
    [WorkflowRun]
    public async Task RunAsync(AgentJobInput input)
    {
        var stepActivityOptions = new ActivityOptions
        {
            StartToCloseTimeout = input.ActivityTimeout,
            HeartbeatTimeout = input.HeartbeatTimeout,
            Summary = AgentActivities.BuildActivitySummary(input.AgentName),
            RetryPolicy = input.RetryPolicy,
        };

        var accumulated = new List<ChatMessage>(input.Request.Messages);
        var maxIterations = input.MaxToolCallsPerTurn;

        for (var iteration = 0; iteration < maxIterations; iteration++)
        {
            var stepInput = new AgentStepInput
            {
                AgentName = input.AgentName,
                Request = input.Request,
                AccumulatedMessages = accumulated,
                SerializedStateBag = null,
                SessionId = null,
                IsFirstStep = iteration == 0,
            };

            var stepResult = await Workflow.ExecuteActivityAsync(
                (AgentActivities a) => a.RunDurableAgentStepAsync(stepInput),
                stepActivityOptions).ConfigureAwait(true);

            accumulated.Add(stepResult.AssistantMessage);

            if (stepResult.IsFinal || stepResult.ToolCalls is null || stepResult.ToolCalls.Count == 0)
            {
                return;
            }

            var toolCalls = stepResult.ToolCalls;
            var toolTasks = new List<Task<InvokeAgentToolResult>>(toolCalls.Count);
            foreach (var tc in toolCalls)
            {
                var toolOptions = ResolveDurableToolActivityOptions(input, tc.Name);

                var toolInput = new InvokeAgentToolInput
                {
                    AgentName = input.AgentName,
                    ToolName = tc.Name,
                    Arguments = tc.Arguments is null
                        ? null
                        : new Dictionary<string, object?>(tc.Arguments),
                    CallId = tc.CallId,
                };

                toolTasks.Add(Workflow.ExecuteActivityAsync(
                    (AgentActivities a) => a.InvokeAgentToolAsync(toolInput),
                    toolOptions));
            }

            var toolResults = await Workflow.WhenAllAsync(toolTasks).ConfigureAwait(true);


            var functionResultContents = new List<AIContent>(toolCalls.Count);
            for (var i = 0; i < toolCalls.Count; i++)
            {
                functionResultContents.Add(new FunctionResultContent(
                    callId: toolCalls[i].CallId,
                    result: toolResults[i].Result));
            }

            accumulated.Add(new ChatMessage(ChatRole.Tool, functionResultContents));
        }
    }

    /// <summary>
    /// Resolves the <see cref="ActivityOptions"/> for a per-tool dispatch. When
    /// <see cref="AgentJobInput.DurableAgentToolActivityOptions"/> contains an entry for
    /// <paramref name="toolName"/>, those options (with their per-tool retry policy and timeouts)
    /// are used; otherwise a default is built from the flat job-level settings.
    /// </summary>
    private static ActivityOptions ResolveDurableToolActivityOptions(AgentJobInput input, string toolName)
    {
        if (input.DurableAgentToolActivityOptions is not null
            && input.DurableAgentToolActivityOptions.TryGetValue(toolName, out var perTool))
        {
            return perTool;
        }

        return new ActivityOptions
        {
            StartToCloseTimeout = input.ActivityTimeout,
            HeartbeatTimeout = input.HeartbeatTimeout,
            Summary = toolName,
            RetryPolicy = input.RetryPolicy,
        };
    }
}
