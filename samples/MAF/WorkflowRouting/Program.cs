// WorkflowRouting — routes user requests to specialist agents entirely inside a Temporal
// workflow, keeping every routing decision durable and visible in event history.
//
// Demonstrates two patterns:
//   • CustomerServiceWorkflow  — static routing via a classifier agent + switch
//   • DynamicRoutingWorkflow   — dynamic routing via an activity that calls
//                                opts.GetAgentDescriptors() (introspection API)
//
// Run:  dotnet run --project samples/MAF/WorkflowRouting/WorkflowRouting.csproj

using System.ClientModel;
using Microsoft.Extensions.AI;
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

// ── Step 2: Load configuration ───────────────────────────────────────────────
var apiKey = builder.Configuration.GetValue<string>("OPENAI_API_KEY");
var apiBaseUrl = builder.Configuration.GetValue<string>("OPENAI_API_BASE_URL");

if (string.IsNullOrEmpty(apiBaseUrl))
    throw new InvalidOperationException("OPENAI_API_BASE_URL is not configured in appsettings.json.");

if (string.IsNullOrEmpty(apiKey))
    throw new InvalidOperationException(
        "OPENAI_API_KEY is not configured. Set it with: " +
        "dotnet user-secrets set \"OPENAI_API_KEY\" \"sk-...\" --project samples/MAF/WorkflowRouting");

const string model = "gpt-4o-mini";
var temporalAddress = builder.Configuration.GetValue<string>("TEMPORAL_ADDRESS") ?? "localhost:7233";

var openAiClient = new OpenAIClient(
    new ApiKeyCredential(apiKey),
    new OpenAIClientOptions { Endpoint = new Uri(apiBaseUrl) });

// ── Step 3: Register the IChatClient in DI ───────────────────────────────────
builder.Services.AddChatClient(openAiClient.GetChatClient(model).AsIChatClient());

// ── Step 4: Register the worker, classifier, and three specialist agents ─────
// Specialist agents carry a `Description` — DynamicRoutingWorkflow's classification
// activity calls opts.GetAgentDescriptors() to discover them at runtime.
// Classifier deliberately omits Description: it is not a routable specialist.
builder.Services.AddTemporalClient(temporalAddress, "default");
builder.Services
    .AddHostedTemporalWorker("workflow-routing")
    .AddTemporalAgents(opts =>
    {
        opts.AddDurableAgent("Classifier", agent =>
        {
            agent.Instructions =
                "You are an intent classifier for a customer service system. " +
                "Given a user message, respond with ONLY one of the following categories:\n" +
                "  ORDERS       — for order tracking, returns, shipping, or purchase questions\n" +
                "  TECH_SUPPORT — for technical issues, troubleshooting, bugs, or app problems\n" +
                "  GENERAL      — for everything else (greetings, general info, company questions)\n\n" +
                "Respond with the single category keyword only. No explanation, no punctuation.";
            agent.ChatClient = sp => sp.GetRequiredService<IChatClient>();
        });

        opts.AddDurableAgent("OrdersAgent", agent =>
        {
            agent.Description = "Handles order tracking, returns, shipping status, and purchase questions.";
            agent.Instructions =
                "You are an orders and shipping specialist. Help customers with order tracking, " +
                "returns, shipping status, delivery estimates, and purchase-related questions. " +
                "Be helpful and concise.";
            agent.ChatClient = sp => sp.GetRequiredService<IChatClient>();
        });

        opts.AddDurableAgent("TechSupportAgent", agent =>
        {
            agent.Description = "Handles technical issues, app crashes, error messages, and troubleshooting.";
            agent.Instructions =
                "You are a technical support specialist. Help customers troubleshoot software issues, " +
                "app crashes, error messages, connectivity problems, and other technical difficulties. " +
                "Provide clear step-by-step guidance.";
            agent.ChatClient = sp => sp.GetRequiredService<IChatClient>();
        });

        opts.AddDurableAgent("GeneralAgent", agent =>
        {
            agent.Description = "Handles greetings, general inquiries, and anything else.";
            agent.Instructions =
                "You are a friendly general customer service agent. Handle greetings, general " +
                "inquiries about available services, company information, and anything that doesn't " +
                "fall into orders or technical support. Be warm and helpful.";
            agent.ChatClient = sp => sp.GetRequiredService<IChatClient>();
        });
    })
    .AddWorkflow<CustomerServiceWorkflow>()
    .AddWorkflow<DynamicRoutingWorkflow>()
    .AddSingletonActivities<RoutingActivities>();

// ── Step 5: Start the host ───────────────────────────────────────────────────
var host = builder.Build();
await host.StartAsync();

Console.WriteLine("Worker started. Submitting customer service workflows...\n");

// ── Step 6: Submit three workflows with different questions ──────────────────
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

// ── Step 7: Demonstrate dynamic routing ──────────────────────────────────────
// DynamicRoutingWorkflow resolves agents via an activity that calls
// opts.GetAgentDescriptors() at runtime — no hardcoded specialist list in workflow code.
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

// ── Step 8: Graceful shutdown ────────────────────────────────────────────────
try { await host.StopAsync(); } catch (OperationCanceledException) { }
Console.WriteLine("Done.");
