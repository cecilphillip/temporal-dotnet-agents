// CustomWorkflow sample — demonstrates subclassing DurableChatWorkflowBase<TOutput>
// to return domain-specific data from a Temporal workflow Update handler.
//
// What this sample shows
// ──────────────────────
// • DurableChatWorkflowBase<TOutput> lets you define a custom typed output for
//   each Update — here ShoppingTurnOutput carries both the ChatResponse and a
//   list of CartAction records collected from tool calls during the LLM turn.
// • ShoppingAssistantWorkflow extends the base class and overrides three abstract
//   members to dispatch to ShoppingActivities (its own activity class).
// • The session loop, history persistence, HITL support, continue-as-new, and
//   search attribute upserts are all inherited from the base class.
//
// How it differs from DurableChatSessionClient
// ─────────────────────────────────────────────
// • DurableChatSessionClient wraps DurableChatWorkflow and returns ChatResponse.
//   It cannot return custom types from the Update without a custom workflow.
// • Here we bypass DurableChatSessionClient and call ExecuteUpdateAsync directly
//   on the workflow handle, using the typed ShoppingTurnOutput as the result.
//
// Prerequisites
// ─────────────
// • A local Temporal server:  temporal server start-dev
// • OPENAI_API_KEY: dotnet user-secrets set "OPENAI_API_KEY" "sk-..." --project samples/MEAI/CustomWorkflow
//
// Run:  dotnet run --project samples/MEAI/CustomWorkflow/CustomWorkflow.csproj

using System.ClientModel;
using CustomWorkflow;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenAI;
using Temporalio.Api.Enums.V1;
using Temporalio.Client;
using Temporalio.Extensions.AI;
using Temporalio.Extensions.Hosting;
using Temporalio.Workflows;

// ── Setup: Build the application host ────────────────────────────────────────
var builder = Host.CreateApplicationBuilder(args);
builder.Logging.SetMinimumLevel(LogLevel.Warning);

var apiKey = builder.Configuration.GetValue<string>("OPENAI_API_KEY");
var apiBaseUrl = builder.Configuration.GetValue<string>("OPENAI_API_BASE_URL");
var model = builder.Configuration.GetValue<string>("OPENAI_MODEL") ?? "gpt-4o-mini";
var temporalAddress = builder.Configuration.GetValue<string>("TEMPORAL_ADDRESS") ?? "localhost:7233";

if (string.IsNullOrEmpty(apiBaseUrl))
    throw new InvalidOperationException("OPENAI_API_BASE_URL is not configured in appsettings.json.");
if (string.IsNullOrEmpty(apiKey))
    throw new InvalidOperationException("OPENAI_API_KEY is not configured. Set it with: dotnet user-secrets set \"OPENAI_API_KEY\" \"sk-...\" --project samples/MEAI/CustomWorkflow");

const string taskQueue = "custom-workflow";
const string systemPrompt =
    "You are a helpful shopping assistant. When the user asks to add or remove items, " +
    "use the add_to_cart and remove_from_cart tools. Always confirm what you did.";

// ── Setup: Connect Temporal client with DurableAIDataConverter ────────────────
var temporalClient = await TemporalClient.ConnectAsync(new TemporalClientConnectOptions(temporalAddress)
{
    DataConverter = DurableAIDataConverter.Instance,
    Namespace = "default",
});
builder.Services.AddSingleton<ITemporalClient>(temporalClient);

// ── Setup: Register IChatClient ───────────────────────────────────────────────
// UseFunctionInvocation handles the tool call loop inside the activity.
IChatClient openAiChatClient = (IChatClient)new OpenAIClient(
    new ApiKeyCredential(apiKey),
    new OpenAIClientOptions { Endpoint = new Uri(apiBaseUrl) }
).GetChatClient(model);

builder.Services
    .AddChatClient(openAiChatClient)
    .UseFunctionInvocation()
    .Build();

// ── Setup: Register worker ────────────────────────────────────────────────────
// AddDurableAI registers supporting infrastructure (options, DataConverter, activities).
// RegisterDefaultWorkflow = false skips the default DurableChatWorkflow since we use
// ShoppingAssistantWorkflow instead.
// AddWorkflow<ShoppingAssistantWorkflow> registers the custom workflow type.
// AddSingletonActivities<ShoppingActivities> registers the shopping activity class.
builder.Services
    .AddHostedTemporalWorker(temporalAddress, "default", taskQueue)
    .AddDurableAI(opts =>
    {
        opts.ActivityTimeout = TimeSpan.FromMinutes(5);
        opts.SessionTimeToLive = TimeSpan.FromHours(1);
        opts.RegisterDefaultWorkflow = false;  // Use custom workflow instead
    })
    .AddWorkflow<ShoppingAssistantWorkflow>()
    .AddSingletonActivities<ShoppingActivities>();

// ── Start ─────────────────────────────────────────────────────────────────────
var host = builder.Build();
await host.StartAsync();

Console.WriteLine("Worker started.\n");

// ── Run demo ──────────────────────────────────────────────────────────────────
Console.WriteLine("════════════════════════════════════════════════════════");
Console.WriteLine(" Demo: Custom Workflow Output (ShoppingAssistant)");
Console.WriteLine("════════════════════════════════════════════════════════");

var workflowId = $"shopping-{Guid.NewGuid():N}";
Console.WriteLine($" Session ID: {workflowId}\n");

// Start the ShoppingAssistantWorkflow. It runs until idle TTL or Shutdown signal.
var handle = await temporalClient.StartWorkflowAsync(
    (ShoppingAssistantWorkflow wf) => wf.RunAsync(new DurableChatWorkflowInput
    {
        ActivityTimeout = TimeSpan.FromMinutes(5),
        TimeToLive = TimeSpan.FromHours(1),
    }),
    new WorkflowOptions(workflowId, taskQueue)
    {
        IdConflictPolicy = WorkflowIdConflictPolicy.UseExisting,
    });

// ── Turn 1: Add an item ───────────────────────────────────────────────────────
var turn1Messages = new List<ChatMessage>
{
    new(ChatRole.System, systemPrompt),
    new(ChatRole.User, "Please add Blue Widget (SKU-001) to my cart. Quantity: 1."),
};

var turn1 = await handle.ExecuteUpdateAsync<ShoppingTurnOutput>(
    "Shop",
    [new DurableChatInput { Messages = turn1Messages }]);

Console.WriteLine(" Turn 1 — Add to cart");
Console.WriteLine($"   Assistant: {turn1.Response.Messages.LastOrDefault()?.Text}");
if (turn1.CartActions.Count > 0)
{
    Console.WriteLine("   Cart actions:");
    foreach (var action in turn1.CartActions)
        Console.WriteLine($"     [{action.Action.ToUpperInvariant()}] {action.ProductName} (SKU: {action.ProductId}) x{action.Quantity}");
}
Console.WriteLine();

// ── Turn 2: Remove the item ───────────────────────────────────────────────────
var turn2Messages = new List<ChatMessage>
{
    new(ChatRole.User, "Actually, please remove the Blue Widget (SKU-001) from my cart."),
};

var turn2 = await handle.ExecuteUpdateAsync<ShoppingTurnOutput>(
    "Shop",
    [new DurableChatInput { Messages = turn2Messages }]);

Console.WriteLine(" Turn 2 — Remove from cart");
Console.WriteLine($"   Assistant: {turn2.Response.Messages.LastOrDefault()?.Text}");
if (turn2.CartActions.Count > 0)
{
    Console.WriteLine("   Cart actions:");
    foreach (var action in turn2.CartActions)
        Console.WriteLine($"     [{action.Action.ToUpperInvariant()}] {action.ProductName} (SKU: {action.ProductId})");
}
Console.WriteLine();

// ── Shutdown the session ──────────────────────────────────────────────────────
await handle.SignalAsync(wf => wf.RequestShutdownAsync());

Console.WriteLine("════════════════════════════════════════════════════════\n");

// ── Stop ──────────────────────────────────────────────────────────────────────
try { await host.StopAsync(); } catch (OperationCanceledException) { }
Console.WriteLine("Done.");
