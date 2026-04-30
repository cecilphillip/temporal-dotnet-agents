// WeatherReportWorkflow — invokes a durable tool via AsDurable(), dispatching it as
// a separate Temporal activity rather than executing the lambda inline.

using Microsoft.Extensions.AI;
using Temporalio.Extensions.AI;
using Temporalio.Workflows;

// ── Input ─────────────────────────────────────────────────────────────────────
internal record WeatherReportInput(string City);

// ── Workflow ──────────────────────────────────────────────────────────────────
[Workflow]
internal sealed class WeatherReportWorkflow
{
    [WorkflowRun]
    public async Task<string> RunAsync(WeatherReportInput input)
    {
        // Wrap the function with AsDurable() so that calling InvokeAsync inside
        // this workflow dispatches to DurableFunctionActivities as a separate
        // Temporal activity. The activity resolves "get_current_weather" from
        // the DurableFunctionRegistry populated via AddDurableTools() at startup.
        //
        // The inner lambda below is a stub — it is only reached when
        // Workflow.InWorkflow is false (i.e., outside a workflow context).
        // Inside the workflow, AsDurable() intercepts the call and routes it
        // to DurableFunctionActivities instead of executing the lambda.
        var durableWeather = AIFunctionFactory.Create(
            (string city) => $"[stub — not invoked in workflow context]",
            name: "get_current_weather",
            description: "Returns the current weather conditions for a given city."
        ).AsDurable();

        // This call dispatches to DurableFunctionActivities as its own activity.
        // Each tool invocation gets independent retry, timeout, and event history
        // entry — giving full Temporal durability guarantees per tool call.
        var result = await durableWeather.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?> { ["city"] = input.City }));

        return result?.ToString() ?? "No result.";
    }
}
