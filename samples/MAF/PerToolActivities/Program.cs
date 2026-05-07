// PerToolActivities — demonstrates per-tool Temporal activity granularity.
//
// Each LLM call is a RunAgentStep activity; each tool call is an InvokeFunction
// activity. Write-style tools (apply_refund, send_email) carry MaximumAttempts = 1
// in PerToolActivityOptions so a transient failure does not double-fire them.
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
using PerToolActivities;
using Temporalio.Client;
using Temporalio.Common;
using Temporalio.Extensions.AI;
using Temporalio.Extensions.Agents;
using Temporalio.Extensions.Hosting;
using Temporalio.Workflows;

// ── Step 1: Build the application host ───────────────────────────────────────
var builder = Host.CreateApplicationBuilder(args);
builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.Logging.AddFilter("Temporalio.Extensions.Agents", LogLevel.Information);

// ── Step 2: Load configuration ────────────────────────────────────────────────
var apiKey = builder.Configuration.GetValue<string>("OPENAI_API_KEY");
var apiBaseUrl = builder.Configuration.GetValue<string>("OPENAI_API_BASE_URL");

if (string.IsNullOrEmpty(apiBaseUrl))
    throw new InvalidOperationException("OPENAI_API_BASE_URL is not configured in appsettings.json.");

if (string.IsNullOrEmpty(apiKey))
    throw new InvalidOperationException(
        "OPENAI_API_KEY is not configured. Set it with: " +
        "dotnet user-secrets set \"OPENAI_API_KEY\" \"sk-...\" --project samples/MAF/PerToolActivities");

var endpoint = new Uri(apiBaseUrl);
var openAiOptions = new OpenAIClientOptions { Endpoint = endpoint };
var model = "gpt-4o-mini";
var temporalAddress = builder.Configuration.GetValue<string>("TEMPORAL_ADDRESS") ?? "localhost:7233";
const string taskQueue = "per-tool-activities";

OpenAIClient openAiClient = new(new ApiKeyCredential(apiKey), openAiOptions);

// ── Step 3: Register tool services as singletons ─────────────────────────────
// The same instance the AIFunctions bind to here is the instance the demo driver
// inspects after each scenario to print LookupCalls / RefundCalls / SendCalls.
builder.Services.AddSingleton<OrderService>();
builder.Services.AddSingleton<RefundService>();
builder.Services.AddSingleton<EmailService>();

// ── Step 4: Build the AIFunctions ────────────────────────────────────────────
// We need the constructed services to bind the AIFunctions, but the agent factory
// and AddDurableTools both need the AIFunction instances at registration time.
// Building a temporary provider here is the clean pattern: the singletons are
// registered once and resolved once; the factory below uses sp.GetRequiredService
// to get the same instance back from the real provider.
using var bootstrap = builder.Services.BuildServiceProvider();
var orderService = bootstrap.GetRequiredService<OrderService>();
var refundService = bootstrap.GetRequiredService<RefundService>();
var emailService = bootstrap.GetRequiredService<EmailService>();

var lookupTool = AIFunctionFactory.Create(
    orderService.LookupOrder,
    name: "lookup_order",
    description: "Look up the current status of a customer order by order ID.");
var refundTool = AIFunctionFactory.Create(
    refundService.ApplyRefund,
    name: "apply_refund",
    description: "Apply a refund of the specified amount to the order. WRITE — non-idempotent.");
var emailTool = AIFunctionFactory.Create(
    emailService.SendEmail,
    name: "send_email",
    description: "Send an email to the customer. WRITE — non-idempotent.");

// ── Step 5: Register IChatClient WITHOUT UseFunctionInvocation ───────────────
// CRITICAL: in step mode the workflow owns the tool-dispatch loop. If the chat
// client pipeline included UseFunctionInvocation, tool calls would be auto-executed
// inside the RunAgentStep activity and the workflow would never see FunctionCallContent
// to dispatch as separate InvokeFunction activities. The agent factory below
// resolves this same IChatClient from DI via GetRequiredService<IChatClient>().
IChatClient innerChatClient = openAiClient.GetChatClient(model).AsIChatClient();
builder.Services
    .AddChatClient(innerChatClient)
    .Build();

// ── Step 6: Register Temporal client + worker pipeline ───────────────────────
// AddDurableAI registers DurableFunctionRegistry/Activities (required so the workflow
// can resolve tools by name). RegisterDefaultWorkflow=false skips DurableChatWorkflow —
// we don't use it; AgentWorkflow from the Agents library is the workflow that runs.
//
// AddDurableTools registers the three AIFunctions in the registry by name.
//
// AddTemporalAgents enables step mode and configures per-tool retry policies. Note the
// agent factory uses .AsAIAgent(...) WITHOUT a clientFactory parameter — the default
// pipeline does not include UseFunctionInvocation, which is exactly what step mode
// requires.
builder.Services.AddTemporalClient(temporalAddress, "default");

builder.Services
    .AddHostedTemporalWorker(taskQueue)
    .AddTemporalAgents(opts =>
    {
        // Phase 5 placeholder — Phase 7 rewrites the sample to v0.3 idiomatic shape.
        opts.AddDurableAgent("RefundAgent", a =>
        {
            a.ChatClient = sp => sp.GetRequiredService<IChatClient>();
            a.MaxToolCallsPerTurn = 10;
            a.Instructions =
                "You are a customer refund specialist. Use lookup_order to verify the order, " +
                "apply_refund to issue the refund, and send_email to confirm with the customer. " +
                "Always look up the order before applying a refund. After applying the refund, " +
                "send a single confirmation email and produce a brief final summary for the user.";
            a.AddTool(lookupTool);
            a.AddTool(refundTool, t => t.NoRetry().WithTimeout(TimeSpan.FromSeconds(30)));
            a.AddTool(emailTool, t => t.NoRetry().WithTimeout(TimeSpan.FromSeconds(30)));
        });
    })
    .AddWorkflow<RefundWorkflow>();

// ── Step 7: Start the host ───────────────────────────────────────────────────
var host = builder.Build();
await host.StartAsync();

Console.WriteLine("Worker started. Running per-tool activity scenarios...\n");

var client = host.Services.GetRequiredService<ITemporalClient>();

// ── Step 7a: Scenario 1 — happy path ─────────────────────────────────────────
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

// ── Step 7b: Scenario 2 — transient lookup failure ───────────────────────────
// Reset counters by replacing the call counts mentally — we only print deltas.
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

// ── Step 8: Operator guidance + graceful shutdown ────────────────────────────
Console.WriteLine("─── View the activity timeline ──────────────────────────────");
Console.WriteLine("  Open http://localhost:8233 in the Temporal Web UI.");
Console.WriteLine("  Each workflow above shows distinct activity rows:");
Console.WriteLine("    • RunAgentStep                — one row per LLM call");
Console.WriteLine("    • InvokeFunction:lookup_order — read tool, retries on transient failure");
Console.WriteLine("    • InvokeFunction:apply_refund — write tool, MaximumAttempts = 1");
Console.WriteLine("    • InvokeFunction:send_email   — write tool, MaximumAttempts = 1");
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
