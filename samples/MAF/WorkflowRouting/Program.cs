// WorkflowRouting sample — demonstrates routing entirely inside a Temporal workflow.
//
// Unlike the MultiAgentRouting sample (which uses IAgentRouter + RouteAsync), this sample
// keeps all routing logic in the workflow itself:
//   1. A lightweight Classifier agent determines the user's intent category.
//   2. A switch statement dispatches to the correct specialist agent.
//   3. The specialist's response is returned as the workflow result.
//
// This gives you full programmatic control — if/else, fallback chains, multi-step
// classification — while Temporal guarantees durability of every routing decision.
//
// Prerequisites
// ─────────────
// • A local Temporal server:  temporal server start-dev
// • OPENAI_API_KEY: dotnet user-secrets set "OPENAI_API_KEY" "sk-..." --project samples/MAF/WorkflowRouting
//
// Run:  dotnet run --project samples/WorkflowRouting/WorkflowRouting.csproj

using System.ClientModel;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenAI;
using OpenAI.Chat;
using Temporalio.Client;
using Temporalio.Extensions.Agents;
using Temporalio.Extensions.Hosting;
using WorkflowRouting;

// ── Step 1: Build the application host ───────────────────────────────────────
var builder = Host.CreateApplicationBuilder(args);
builder.Logging.SetMinimumLevel(LogLevel.Warning);

// ── Step 2: Load configuration ──────────────────────────────────────────────
var apiKey = builder.Configuration.GetValue<string>("OPENAI_API_KEY");
var apiBaseUrl = builder.Configuration.GetValue<string>("OPENAI_API_BASE_URL");

if (string.IsNullOrEmpty(apiBaseUrl))
{
    throw new InvalidOperationException("OPENAI_API_BASE_URL is not configured in appsettings.json.");
}

if (string.IsNullOrEmpty(apiKey))
{
    throw new InvalidOperationException("OPENAI_API_KEY is not configured. Set it with: dotnet user-secrets set \"OPENAI_API_KEY\" \"sk-...\" --project samples/MAF/WorkflowRouting");
}

var endpoint = new Uri(apiBaseUrl);
var openAiOptions = new OpenAIClientOptions() { Endpoint = endpoint };
var model = "gpt-4o-mini";
var temporalAddress = builder.Configuration.GetValue<string>("TEMPORAL_ADDRESS") ?? "localhost:7233";

ApiKeyCredential credential = new(apiKey!);
OpenAIClient openAiClient = new(credential, openAiOptions);

// ── Step 3: Create the Classifier agent ─────────────────────────────────────
// The classifier's only job is to categorize the user's question into one of
// three intent buckets. It returns a single keyword — no prose, no punctuation.
var classifier = openAiClient
    .GetChatClient(model)
    .AsAIAgent(
        name: "Classifier",
        instructions:
            "You are an intent classifier for a customer service system. " +
            "Given a user message, respond with ONLY one of the following categories:\n" +
            "  ORDERS       — for order tracking, returns, shipping, or purchase questions\n" +
            "  TECH_SUPPORT — for technical issues, troubleshooting, bugs, or app problems\n" +
            "  GENERAL      — for everything else (greetings, general info, company questions)\n\n" +
            "Respond with the single category keyword only. No explanation, no punctuation.");

// ── Step 4: Create the three specialist agents ──────────────────────────────
var ordersAgent = openAiClient
    .GetChatClient(model)
    .AsAIAgent(
        name: "OrdersAgent",
        description: "Handles order tracking, returns, shipping status, and purchase questions.",
        instructions:
            "You are an orders and shipping specialist. Help customers with order tracking, " +
            "returns, shipping status, delivery estimates, and purchase-related questions. " +
            "Be helpful and concise.");

var techSupportAgent = openAiClient
    .GetChatClient(model)
    .AsAIAgent(
        name: "TechSupportAgent",
        description: "Handles technical issues, app crashes, error messages, and troubleshooting.",
        instructions:
            "You are a technical support specialist. Help customers troubleshoot software issues, " +
            "app crashes, error messages, connectivity problems, and other technical difficulties. " +
            "Provide clear step-by-step guidance.");

var generalAgent = openAiClient
    .GetChatClient(model)
    .AsAIAgent(
        name: "GeneralAgent",
        description: "Handles greetings, general inquiries, and anything else.",
        instructions:
            "You are a friendly general customer service agent. Handle greetings, general " +
            "inquiries about available services, company information, and anything that doesn't " +
            "fall into orders or technical support. Be warm and helpful.");

// ── Step 5: Register the worker ─────────────────────────────────────────────
// CustomerServiceWorkflow uses hardcoded agent names (static routing).
// DynamicRoutingWorkflow discovers agents via descriptors at runtime.
// Both patterns are demonstrated — descriptors don't require SetRouterAgent.
builder.Services
    .AddHostedTemporalWorker(temporalAddress, "default", "workflow-routing")
    .AddTemporalAgents(opts =>
    {
        // Classifier doesn't need a description — it's not a routable specialist.
        opts.AddAIAgent(classifier);

        // Specialist agents carry descriptions via AsAIAgent(description: ...),
        // which are auto-extracted into the descriptor registry for DynamicRoutingWorkflow.
        // Note: NO SetRouterAgent — we use descriptors without the IAgentRouter abstraction.
        opts.AddAIAgent(ordersAgent);
        opts.AddAIAgent(techSupportAgent);
        opts.AddAIAgent(generalAgent);
    })
    .AddWorkflow<CustomerServiceWorkflow>()
    .AddWorkflow<DynamicRoutingWorkflow>()
    .AddSingletonActivities<RoutingActivities>();

// ── Step 6: Start the host ──────────────────────────────────────────────────
var host = builder.Build();
await host.StartAsync();

Console.WriteLine("Worker started. Submitting customer service workflows...\n");

// ── Step 7: Submit three workflows with different questions ─────────────────
var client = host.Services.GetRequiredService<ITemporalClient>();

var questions = new[]
{
    (Id: "cs-orders",       Question: "Where is my order #12345?"),
    (Id: "cs-tech-support", Question: "My app keeps crashing on startup"),
    (Id: "cs-general",      Question: "What services do you offer?"),
};

foreach (var (id, question) in questions)
{
    var workflowId = $"{id}-{Guid.NewGuid():N}";

    Console.WriteLine($"Starting workflow {workflowId}");

    var handle = await client.StartWorkflowAsync(
        (CustomerServiceWorkflow wf) => wf.RunAsync(question),
        new WorkflowOptions
        {
            Id = workflowId,
            TaskQueue = "workflow-routing",
        });

    try
    {
        var result = await handle.GetResultAsync();

        Console.WriteLine($"\n── Question: {question}");
        Console.WriteLine($"   Workflow: {workflowId}");
        Console.WriteLine($"   Response: {result}");
        Console.WriteLine();
    }
    catch (Exception ex)
    {
        Console.WriteLine($"\n── Workflow {workflowId} failed: {ex.Message}\n");
    }
}

// ── Step 8: Demonstrate dynamic routing ──────────────────────────────────
// DynamicRoutingWorkflow resolves agents via an activity that queries the
// registry at runtime. "V2" agents aren't registered, so the activity falls
// back to the existing agents.
Console.WriteLine("── Dynamic Routing ─────────────────────────────────────\n");

var dynamicQuestion = "I need to return a defective product";
var dynamicWorkflowId = $"cs-dynamic-{Guid.NewGuid():N}";

Console.WriteLine($"Starting dynamic workflow {dynamicWorkflowId}");

var dynamicHandle = await client.StartWorkflowAsync(
    (DynamicRoutingWorkflow wf) => wf.RunAsync(dynamicQuestion),
    new WorkflowOptions
    {
        Id = dynamicWorkflowId,
        TaskQueue = "workflow-routing",
    });

try
{
    var dynamicResult = await dynamicHandle.GetResultAsync();
    Console.WriteLine($"\n── Question: {dynamicQuestion}");
    Console.WriteLine($"   Workflow: {dynamicWorkflowId}");
    Console.WriteLine($"   Response: {dynamicResult}");
    Console.WriteLine();
}
catch (Exception ex)
{
    Console.WriteLine($"\n── Dynamic workflow failed: {ex.Message}\n");
}

// ── Step 9: Graceful shutdown ───────────────────────────────────────────────
// TemporalWorker.ExecuteAsync intentionally throws TaskCanceledException on shutdown.
try { await host.StopAsync(); } catch (OperationCanceledException) { }
Console.WriteLine("Done.");
