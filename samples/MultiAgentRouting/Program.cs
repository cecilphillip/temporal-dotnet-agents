// MultiAgentRouting sample — demonstrates:
//   1. LLM-powered routing via SetRouterAgent + ITemporalAgentClient.RouteAsync
//   2. Parallel agent execution via TemporalWorkflowExtensions.ExecuteAgentsInParallelAsync
//   3. OpenTelemetry configured with TracingInterceptor (Temporalio.Extensions.OpenTelemetry)
//      AND TemporalAgentTelemetry.ActivitySourceName
//
// Prerequisites
// ─────────────
// • A local Temporal server:  temporal server start-dev
//   (The dev server starts on localhost:7233 with the "default" namespace.)
// • An OpenAI API key in appsettings.json
//
// Run:  dotnet run --project samples/MultiAgentRouting/MultiAgentRouting.csproj

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
    throw new InvalidOperationException("OPENAI_API_KEY is not configured in appsettings.json.");
}

var endpoint = new Uri(apiBaseUrl);
var openAiOptions = new OpenAIClientOptions() { Endpoint = endpoint };
var model = "gpt-4o-mini";

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

// ── Step 5: Create the router agent ──────────────────────────────────────────
// The router is a lightweight LLM agent whose sole job is to classify each incoming
// request and respond with exactly the name of the best-matching specialist agent.
var routerAgent = openAiClient
    .GetChatClient(model)
    .AsAIAgent(
        name: "__router__",
        instructions:
            "You are a routing agent. Given a user query, respond with ONLY the name of the " +
            "most appropriate specialist agent from the available options. Do not include any " +
            "explanation or punctuation — only the agent name.");

// ── Step 6: Register ITemporalClient with TracingInterceptor ─────────────────
// The TracingInterceptor propagates OTel context across Temporal calls.
builder.Services.AddTemporalClient(opts =>
{
    opts.TargetHost = "localhost:7233";
    opts.Namespace = "default";
    opts.Interceptors = new[] { new TracingInterceptor() };
});

// ── Step 7: Register the hosted worker with all agents ────────────────────────
builder.Services
    .AddHostedTemporalWorker("agents")
    .AddTemporalAgents(opts =>
    {
        // Register the three specialist agents
        opts.AddAIAgent(weatherAgent, timeToLive: TimeSpan.FromHours(1));
        opts.AddAIAgent(billingAgent, timeToLive: TimeSpan.FromHours(1));
        opts.AddAIAgent(techSupportAgent, timeToLive: TimeSpan.FromHours(1));

        // Register descriptors — used by LlmAgentRouter to build the routing prompt
        opts.AddAgentDescriptor("WeatherAgent",
            "Handles questions about weather conditions, forecasts, climate, and meteorology.");
        opts.AddAgentDescriptor("BillingAgent",
            "Handles questions about invoices, charges, payment methods, refunds, and account billing.");
        opts.AddAgentDescriptor("TechSupportAgent",
            "Handles questions about software issues, hardware problems, troubleshooting, and technical configurations.");

        // SetRouterAgent registers an LlmAgentRouter that automatically picks the right agent
        opts.SetRouterAgent(routerAgent);
    })
    .AddWorkflow<RoutingWorkflow>();

// ── Step 8: Start the host ────────────────────────────────────────────────────
var host = builder.Build();
await host.StartAsync();

Console.WriteLine("Worker started.\n");

// ── Step 9: Demonstrate LLM-powered routing ───────────────────────────────────
// Resolve ITemporalAgentClient from DI and call RouteAsync.
// The router automatically picks the best specialist for each question.
var agentClient = host.Services.GetRequiredService<ITemporalAgentClient>();

Console.WriteLine("── Demonstrating LLM-powered routing ───────────────────────");

var routingExamples = new[]
{
    (Key: "session-weather-001", Question: "Will it rain in Seattle tomorrow?"),
    (Key: "session-billing-001", Question: "Why was I charged twice on my last invoice?"),
    (Key: "session-tech-001",    Question: "My application keeps crashing with a null reference exception."),
};

foreach (var (sessionKey, question) in routingExamples)
{
    Console.WriteLine($"\nUser: {question}");
    var routedResponse = await agentClient.RouteAsync(
        sessionKey,
        new RunRequest(question));
    Console.WriteLine($"Agent: {routedResponse.Text}");
}

// ── Step 10: Demonstrate parallel execution via workflow ──────────────────────
Console.WriteLine("\n── Demonstrating parallel agent execution ──────────────────");

var parallelQuery = "Briefly introduce yourself and what you can help with.";
Console.WriteLine($"\nFan-out query (sent to all 3 agents simultaneously): \"{parallelQuery}\"\n");

var client = host.Services.GetRequiredService<ITemporalClient>();
var workflowId = $"multi-agent-routing-{Guid.NewGuid():N}";

var handle = await client.StartWorkflowAsync(
    (RoutingWorkflow wf) => wf.RunAsync(parallelQuery),
    new WorkflowOptions
    {
        Id = workflowId,
        TaskQueue = "agents"
    });

Console.WriteLine($"Parallel workflow started: {workflowId}");

var parallelResults = await handle.GetResultAsync();

Console.WriteLine("\nParallel responses:");
var agentNames = new[] { "WeatherAgent", "BillingAgent", "TechSupportAgent" };
for (var i = 0; i < parallelResults.Length; i++)
{
    Console.WriteLine($"\n[{agentNames[i]}]: {parallelResults[i]}");
}

// ── Step 11: Graceful shutdown ────────────────────────────────────────────────
// TemporalWorker.ExecuteAsync intentionally throws TaskCanceledException on shutdown.
try { await host.StopAsync(); } catch (OperationCanceledException) { }
Console.WriteLine("\nDone.");
