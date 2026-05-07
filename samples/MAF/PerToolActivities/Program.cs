// PerToolActivities — demonstrates per-tool Temporal activity granularity.
//
// Each LLM call is a RunDurableAgentStep activity; each tool call is an
// InvokeAgentTool:<tool_name> activity. Write tools (apply_refund, send_email)
// register opts.NoRetry() so a transient failure cannot double-fire them.
//
// Run:  dotnet run --project samples/MAF/PerToolActivities/PerToolActivities.csproj
//
// Two scenarios run back-to-back:
//   1. Happy path — all tools succeed exactly once.
//   2. Transient lookup failure — the read tool fails on attempt 1 and retries; the
//      write tools still fire exactly once. This is the per-tool retry guarantee.

using System.ClientModel;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenAI;
using OpenAI.Chat;
using PerToolActivities;
using Temporalio.Client;
using Temporalio.Extensions.Agents;
using Temporalio.Extensions.Hosting;
using Temporalio.Workflows;

// ── Step 1: Build the application host ───────────────────────────────────────
var builder = Host.CreateApplicationBuilder(args);
builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.Logging.AddFilter("Temporalio.Extensions.Agents", LogLevel.Information);

// ── Step 2: Load configuration ───────────────────────────────────────────────
var apiKey = builder.Configuration.GetValue<string>("OPENAI_API_KEY");
var apiBaseUrl = builder.Configuration.GetValue<string>("OPENAI_API_BASE_URL");

if (string.IsNullOrEmpty(apiBaseUrl))
    throw new InvalidOperationException("OPENAI_API_BASE_URL is not configured in appsettings.json.");

if (string.IsNullOrEmpty(apiKey))
    throw new InvalidOperationException(
        "OPENAI_API_KEY is not configured. Set it with: " +
        "dotnet user-secrets set \"OPENAI_API_KEY\" \"sk-...\" --project samples/MAF/PerToolActivities");

const string model = "gpt-4o-mini";
const string taskQueue = "per-tool-activities";
var temporalAddress = builder.Configuration.GetValue<string>("TEMPORAL_ADDRESS") ?? "localhost:7233";

var openAiClient = new OpenAIClient(
    new ApiKeyCredential(apiKey),
    new OpenAIClientOptions { Endpoint = new Uri(apiBaseUrl) });

// ── Step 3: Register tool services as singletons ─────────────────────────────
// The same instance the AIFunction factories bind to is the instance the demo
// driver inspects after each scenario to print LookupCalls / RefundCalls / SendCalls.
builder.Services.AddSingleton<OrderService>();
builder.Services.AddSingleton<RefundService>();
builder.Services.AddSingleton<EmailService>();

// ── Step 4: Register the IChatClient in DI ───────────────────────────────────
// Register a bare IChatClient. The durable-agent path composes its pipeline
// internally with UseProvidedChatClientAsIs = true, so MAF's auto-injected
// FunctionInvokingChatClient cannot intercept tool calls — the workflow owns
// the tool-dispatch loop and dispatches each tool as a separate Temporal activity.
builder.Services.AddChatClient(openAiClient.GetChatClient(model).AsIChatClient());

// ── Step 5: Register Temporal client + worker pipeline ───────────────────────
// AddDurableAgent is the single registration entry point in v0.3:
//   • agent.AddTool(...)                       — read tool, inherits worker default retry
//   • agent.AddTool(..., opts => opts.NoRetry()) — write tool, MaximumAttempts = 1
// Per-tool retry is bound to the AIFunction reference via the configure callback —
// no string-keyed PerToolActivityOptions dictionary, no name-typo footgun.
builder.Services.AddTemporalClient(temporalAddress, "default");

builder.Services
    .AddHostedTemporalWorker(taskQueue)
    .AddTemporalAgents(opts =>
    {
        opts.AddDurableAgent("RefundAgent", agent =>
        {
            agent.Description = "Customer refund specialist with read and write tools.";
            agent.Instructions =
                "You are a customer refund specialist. Use lookup_order to verify the order, " +
                "apply_refund to issue the refund, and send_email to confirm with the customer. " +
                "Always look up the order before applying a refund. After applying the refund, " +
                "send a single confirmation email and produce a brief final summary for the user.";
            agent.ChatClient = sp => sp.GetRequiredService<IChatClient>();
            agent.MaxToolCallsPerTurn = 10;

            // Read tool — inherits worker default retry. A transient failure retries.
            agent.AddTool("lookup_order", sp => AIFunctionFactory.Create(
                sp.GetRequiredService<OrderService>().LookupOrder,
                name: "lookup_order",
                description: "Look up the current status of a customer order by order ID."));

            // Write tools — opts.NoRetry() sets MaximumAttempts = 1, bound to the
            // AIFunction reference at registration. A transient activity failure
            // surfaces to the workflow rather than re-firing the side effect.
            agent.AddTool(
                "apply_refund",
                sp => AIFunctionFactory.Create(
                    sp.GetRequiredService<RefundService>().ApplyRefund,
                    name: "apply_refund",
                    description: "Apply a refund of the specified amount to the order. WRITE — non-idempotent."),
                opts => opts.NoRetry().WithTimeout(TimeSpan.FromSeconds(30)));

            agent.AddTool(
                "send_email",
                sp => AIFunctionFactory.Create(
                    sp.GetRequiredService<EmailService>().SendEmail,
                    name: "send_email",
                    description: "Send an email to the customer. WRITE — non-idempotent."),
                opts => opts.NoRetry().WithTimeout(TimeSpan.FromSeconds(30)));
        });
    })
    .AddWorkflow<RefundWorkflow>();

// ── Step 6: Start the host ───────────────────────────────────────────────────
var host = builder.Build();
await host.StartAsync();

Console.WriteLine("Worker started. Running per-tool activity scenarios...\n");

var client = host.Services.GetRequiredService<ITemporalClient>();
var orderService = host.Services.GetRequiredService<OrderService>();
var refundService = host.Services.GetRequiredService<RefundService>();
var emailService = host.Services.GetRequiredService<EmailService>();

// ── Step 6a: Scenario 1 — happy path ─────────────────────────────────────────
Console.WriteLine("─── Scenario 1: Happy path (all tools succeed) ──────────────");
orderService.FailOnceEnabled = false;

await RunScenarioAsync(
    client,
    taskQueue,
    workflowId: $"refund-happy-{Guid.NewGuid():N}",
    complaint: "I never received my order ORD-002 and want a full $49.99 refund. " +
               "Please email me at acme@example.com once it's processed.");

PrintToolStats(orderService, refundService, emailService);
Console.WriteLine();

// ── Step 6b: Scenario 2 — transient lookup failure ───────────────────────────
var lookupBefore = orderService.LookupCalls;
var refundBefore = refundService.RefundCalls;
var emailBefore = emailService.SendCalls;

Console.WriteLine("─── Scenario 2: Transient lookup failure (per-tool retry) ───");
orderService.FailOnceEnabled = true;

await RunScenarioAsync(
    client,
    taskQueue,
    workflowId: $"refund-retry-{Guid.NewGuid():N}",
    complaint: "Refund my order ORD-001 for $19.99. Email confirmation to acme@example.com.");

var lookupDelta = orderService.LookupCalls - lookupBefore;
var refundDelta = refundService.RefundCalls - refundBefore;
var emailDelta = emailService.SendCalls - emailBefore;

Console.WriteLine($"  This run    → LookupCalls = {lookupDelta}, RefundCalls = {refundDelta}, SendCalls = {emailDelta}");
Console.WriteLine($"  Cumulative  → LookupCalls = {orderService.LookupCalls}, " +
                  $"RefundCalls = {refundService.RefundCalls}, SendCalls = {emailService.SendCalls}");

if (lookupDelta >= 2 && refundDelta == 1 && emailDelta == 1)
{
    Console.WriteLine("  ✓ Per-tool retry granularity confirmed: lookup retried, writes fired exactly once.");
}
Console.WriteLine();

// ── Step 7: Operator guidance + graceful shutdown ────────────────────────────
Console.WriteLine("─── View the activity timeline ──────────────────────────────");
Console.WriteLine("  Open http://localhost:8233 in the Temporal Web UI.");
Console.WriteLine("  Each workflow above shows distinct activity rows:");
Console.WriteLine("    • RunDurableAgentStep                        — one row per LLM call");
Console.WriteLine("    • InvokeAgentTool:RefundAgent:lookup_order   — read, retries on transient failure");
Console.WriteLine("    • InvokeAgentTool:RefundAgent:apply_refund   — write, MaximumAttempts = 1");
Console.WriteLine("    • InvokeAgentTool:RefundAgent:send_email     — write, MaximumAttempts = 1");
Console.WriteLine();

try { await host.StopAsync(); } catch (OperationCanceledException) { }
Console.WriteLine("Done.");

// ─────────────────────────────────────────────────────────────────────────────
// LOCAL HELPERS
// ─────────────────────────────────────────────────────────────────────────────

static async Task RunScenarioAsync(ITemporalClient client, string taskQueue, string workflowId, string complaint)
{
    Console.WriteLine($"  Workflow:  {workflowId}");
    Console.WriteLine($"  Complaint: {complaint}");

    try
    {
        var result = await client.ExecuteWorkflowAsync(
            (RefundWorkflow wf) => wf.RunAsync(complaint),
            new WorkflowOptions
            {
                Id = workflowId,
                TaskQueue = taskQueue,
            });

        Console.WriteLine($"  Agent:     {result}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  Workflow failed: {ex.Message}");
    }
}

static void PrintToolStats(OrderService orders, RefundService refunds, EmailService emails)
{
    Console.WriteLine($"  Final state → LookupCalls = {orders.LookupCalls}, " +
                      $"RefundCalls = {refunds.RefundCalls}, SendCalls = {emails.SendCalls}");
}
