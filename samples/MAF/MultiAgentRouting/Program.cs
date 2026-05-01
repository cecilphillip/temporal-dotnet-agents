// MultiAgentRouting — demonstrates workflow-based routing (durable routing decision via
// activity) and parallel agent fan-out via ExecuteAgentsInParallelAsync, with OTel tracing.
//
// Run:  dotnet run --project samples/MAF/MultiAgentRouting/MultiAgentRouting.csproj

using System.ClientModel;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MultiAgentRouting;
using OpenAI;
using OpenAI.Chat;
using OpenTelemetry;
using OpenTelemetry.Trace;
using Temporalio.Client;
using Temporalio.Extensions.Agents;
using Temporalio.Extensions.Hosting;
using Temporalio.Extensions.OpenTelemetry;

// ── Step 1: Configure OpenTelemetry ─────────────────────────────────────────
// Register all four activity sources:
//   • TracingInterceptor.ClientSource      — client outbound spans
//   • TracingInterceptor.WorkflowsSource   — workflow inbound/outbound spans
//   • TracingInterceptor.ActivitiesSource  — activity inbound spans
//   • TemporalAgentTelemetry.ActivitySourceName — agent turn + client send spans
using var tracerProvider = Sdk.CreateTracerProviderBuilder()
    .AddSource(TracingInterceptor.ClientSource.Name)
    .AddSource(TracingInterceptor.WorkflowsSource.Name)
    .AddSource(TracingInterceptor.ActivitiesSource.Name)
    .AddSource(TemporalAgentTelemetry.ActivitySourceName)
    .AddConsoleExporter()
    .Build();

// ── Step 2: Build the application host ───────────────────────────────────────
var builder = Host.CreateApplicationBuilder(args);
builder.Logging.SetMinimumLevel(LogLevel.Warning);

// ── Step 3: Load configuration ────────────────────────────────────────────────
var apiKey = builder.Configuration.GetValue<string>("OPENAI_API_KEY");
var apiBaseUrl = builder.Configuration.GetValue<string>("OPENAI_API_BASE_URL");

if (string.IsNullOrEmpty(apiBaseUrl))
{
    throw new InvalidOperationException("OPENAI_API_BASE_URL is not configured in appsettings.json.");
}

if (string.IsNullOrEmpty(apiKey))
{
    throw new InvalidOperationException("OPENAI_API_KEY is not configured. Set it with: dotnet user-secrets set \"OPENAI_API_KEY\" \"sk-...\" --project samples/MAF/MultiAgentRouting");
}

var endpoint = new Uri(apiBaseUrl);
var openAiOptions = new OpenAIClientOptions() { Endpoint = endpoint };
var model = "gpt-4o-mini";
var temporalAddress = builder.Configuration.GetValue<string>("TEMPORAL_ADDRESS") ?? "localhost:7233";

ApiKeyCredential credential = new(apiKey!);
OpenAIClient openAiClient = new(credential, openAiOptions);

// ── Step 4: Create the three specialist agents ────────────────────────────────
var weatherAgent = openAiClient
    .GetChatClient(model)
    .AsAIAgent(
        name: "WeatherAgent",
        instructions:
            "You are a weather specialist. Answer questions about weather conditions, forecasts, " +
            "climate patterns, and meteorological phenomena. Keep responses concise and informative.");

var billingAgent = openAiClient
    .GetChatClient(model)
    .AsAIAgent(
        name: "BillingAgent",
        instructions:
            "You are a billing and payments specialist. Answer questions about invoices, charges, " +
            "payment methods, refunds, and account billing. Keep responses concise and informative.");

var techSupportAgent = openAiClient
    .GetChatClient(model)
    .AsAIAgent(
        name: "TechSupportAgent",
        instructions:
            "You are a technical support specialist. Answer questions about software issues, " +
            "hardware problems, troubleshooting steps, and technical configurations. " +
            "Keep responses concise and informative.");

// ── Step 5: Register ITemporalClient with TracingInterceptor ─────────────────
// The TracingInterceptor propagates OTel context across Temporal calls.
builder.Services.AddTemporalClient(opts =>
{
    opts.TargetHost = temporalAddress;
    opts.Namespace = "default";
    opts.Interceptors = new[] { new TracingInterceptor() };
});

// ── Step 6: Register the hosted worker with all agents ────────────────────────
builder.Services
    .AddHostedTemporalWorker("agents")
    .AddTemporalAgents(opts =>
    {
        opts.AddAIAgent(weatherAgent, timeToLive: TimeSpan.FromHours(1));
        opts.AddAIAgent(billingAgent, timeToLive: TimeSpan.FromHours(1));
        opts.AddAIAgent(techSupportAgent, timeToLive: TimeSpan.FromHours(1));
    })
    .AddWorkflow<RoutingWorkflow>()
    .AddWorkflow<ParallelAgentWorkflow>()
    // Singleton: RoutingActivities is stateless (keyword scoring only), so one shared
    // instance is safe and avoids unnecessary allocations on every activity execution.
    .AddSingletonActivities<RoutingActivities>();

// ── Step 7: Start the host ────────────────────────────────────────────────────
var host = builder.Build();
await host.StartAsync();

Console.WriteLine("Worker started.\n");

// ── Step 8: Demonstrate workflow-based routing ────────────────────────────────
// Each question is dispatched to a RoutingWorkflow. The routing decision runs
// inside a RoutingActivities.ClassifyRequest activity — it is recorded in the
// workflow event history and is fully durable.
var client = host.Services.GetRequiredService<ITemporalClient>();

Console.WriteLine("── Demonstrating workflow-based routing ────────────────────");

var routingExamples = new[]
{
    (Id: "session-weather-001", Question: "Will it rain in Seattle tomorrow?"),
    (Id: "session-billing-001", Question: "Why was I charged twice on my last invoice?"),
    (Id: "session-tech-001",    Question: "My application keeps crashing with a null reference exception."),
};

foreach (var (sessionId, question) in routingExamples)
{
    Console.WriteLine($"\nUser: {question}");

    var workflowId = $"routing-{sessionId}-{Guid.NewGuid():N}";
    var handle = await client.StartWorkflowAsync(
        (RoutingWorkflow wf) => wf.RunAsync(question),
        new WorkflowOptions { Id = workflowId, TaskQueue = "agents" });

    try
    {
        var result = await handle.GetResultAsync();
        Console.WriteLine($"Agent: {result}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Workflow failed: {ex.Message}");
    }
}

// ── Step 9: Demonstrate parallel execution ────────────────────────────────────
Console.WriteLine("\n── Demonstrating parallel agent execution ──────────────────");

var parallelQuery = "Briefly introduce yourself and what you can help with.";
Console.WriteLine($"\nFan-out query (sent to all 3 agents simultaneously): \"{parallelQuery}\"\n");

var parallelWorkflowId = $"multi-agent-parallel-{Guid.NewGuid():N}";

var parallelHandle = await client.StartWorkflowAsync(
    (ParallelAgentWorkflow wf) => wf.RunAsync(parallelQuery),
    new WorkflowOptions { Id = parallelWorkflowId, TaskQueue = "agents" });

Console.WriteLine($"Parallel workflow started: {parallelWorkflowId}");

string[] parallelResults;
try
{
    parallelResults = await parallelHandle.GetResultAsync();
}
catch (Exception ex)
{
    Console.WriteLine($"Parallel workflow failed: {ex.Message}\n");
    try { await host.StopAsync(); } catch (OperationCanceledException) { }
    return;
}

Console.WriteLine("\nParallel responses:");
var agentNames = new[] { "WeatherAgent", "BillingAgent", "TechSupportAgent" };
for (var i = 0; i < parallelResults.Length; i++)
{
    Console.WriteLine($"\n[{agentNames[i]}]: {parallelResults[i]}");
}

// ── Step 10: Graceful shutdown ────────────────────────────────────────────────
// TemporalWorker.ExecuteAsync intentionally throws TaskCanceledException on shutdown.
try { await host.StopAsync(); } catch (OperationCanceledException) { }
tracerProvider?.ForceFlush();
Console.WriteLine("\nDone.");
