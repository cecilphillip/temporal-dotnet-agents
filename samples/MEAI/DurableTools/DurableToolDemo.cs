// DurableToolDemo — starts WeatherReportWorkflow and prints the durable tool result.

using Temporalio.Client;

// ── Demo runner ───────────────────────────────────────────────────────────────
internal static class DurableToolDemo
{
    public static async Task RunAsync(ITemporalClient client, string taskQueue, string city)
    {
        Console.WriteLine("════════════════════════════════════════════════════════");
        Console.WriteLine(" AsDurable() — Per-Tool Activity Dispatch");
        Console.WriteLine("════════════════════════════════════════════════════════");
        Console.WriteLine(" Each tool call is a separate Temporal activity with its");
        Console.WriteLine(" own retry policy, timeout, and event history entry.\n");

        var workflowId = $"weather-report-{Guid.NewGuid():N}";
        Console.WriteLine($" Workflow ID: {workflowId}");
        Console.WriteLine($" City      : {city}\n");

        // Start the workflow — it will call durableWeather.InvokeAsync() internally,
        // which dispatches to DurableFunctionActivities as a separate activity.
        var handle = await client.StartWorkflowAsync(
            (WeatherReportWorkflow wf) => wf.RunAsync(new WeatherReportInput(city)),
            new WorkflowOptions(workflowId, taskQueue));

        var result = await handle.GetResultAsync();

        Console.WriteLine($" Result: {result}");
        Console.WriteLine("════════════════════════════════════════════════════════\n");
    }
}
