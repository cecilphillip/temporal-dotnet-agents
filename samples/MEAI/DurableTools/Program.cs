// DurableTools sample — demonstrates the AsDurable() pattern from Temporalio.Extensions.AI
//
// Each tool call inside a Temporal workflow is dispatched as its own independent
// Temporal activity via DurableFunctionActivities — no Agent Framework required.
//
// How it works
// ────────────
// • AIFunctionFactory.Create(...).AsDurable() wraps a tool so that calling
//   InvokeAsync() inside a workflow dispatches to DurableFunctionActivities
//   instead of executing the lambda directly.
// • AddDurableTools() registers the real implementation in DurableFunctionRegistry
//   so the activity can look it up by name and invoke it.
// • The workflow stub lambda (the inner function passed to AIFunctionFactory.Create)
//   is never called inside the workflow — it exists only as a passthrough for
//   non-workflow contexts (Workflow.InWorkflow == false).
//
// How this differs from UseFunctionInvocation() (DurableChat Demo 2)
// ──────────────────────────────────────────────────────────────────
//   Demo 2 path: DurableChatActivities (one activity)
//                  └─► UseFunctionInvocation handles tool loop inside that activity
//
//   This sample:  WeatherReportWorkflow (workflow)
//                   └─► durableWeather.InvokeAsync()   [dispatches when InWorkflow = true]
//                         └─► DurableFunctionActivities (separate activity per tool call)
//
// Prerequisites
// ─────────────
// • A local Temporal server:  temporal server start-dev
//   (The dev server starts on localhost:7233 with the "default" namespace.)
// • OPENAI_API_KEY: dotnet user-secrets set "OPENAI_API_KEY" "sk-..." --project samples/MEAI/DurableTools
//   Note: this sample does not call the LLM — the workflow invokes the tool
//   directly, so an API key is not strictly needed for the demo to run.
//
// Run:  dotnet run --project samples/MEAI/DurableTools/DurableTools.csproj

using System.ClientModel;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenAI;
using Temporalio.Client;
using Temporalio.Extensions.AI;
using Temporalio.Extensions.Hosting;

// ── Setup: Build the application host ────────────────────────────────────────
var builder = Host.CreateApplicationBuilder(args);
builder.Logging.SetMinimumLevel(LogLevel.Warning);

var apiKey = builder.Configuration.GetValue<string>("OPENAI_API_KEY") ?? "";
var apiBaseUrl = builder.Configuration.GetValue<string>("OPENAI_API_BASE_URL") ?? "https://api.openai.com/v1";
var model = builder.Configuration.GetValue<string>("OPENAI_MODEL") ?? "gpt-4o-mini";
var temporalAddress = builder.Configuration.GetValue<string>("TEMPORAL_ADDRESS") ?? "localhost:7233";

const string taskQueue = "durable-tools";

// ── Setup: Connect Temporal client with DurableAIDataConverter ────────────────
// DurableAIDataConverter.Instance wraps Temporal's payload converter with
// AIJsonUtilities.DefaultOptions, which handles MEAI's $type discriminator for
// polymorphic AIContent subclasses (TextContent, FunctionCallContent, etc.).
// Without this, type information is lost when types round-trip through history.
var temporalClient = await TemporalClient.ConnectAsync(new TemporalClientConnectOptions(temporalAddress)
{
    DataConverter = DurableAIDataConverter.Instance,
    Namespace = "default",
});
builder.Services.AddSingleton<ITemporalClient>(temporalClient);

// ── Setup: Weather tool (registered in DurableFunctionRegistry) ───────────────
// GetCurrentWeather is the real implementation — registered via AddDurableTools()
// so DurableFunctionActivities can resolve it by name ("get_current_weather")
// when WeatherReportWorkflow dispatches a durable tool call.
static string GetCurrentWeather(string city)
    => Random.Shared.NextDouble() > 0.5
        ? $"It's sunny and 22 °C in {city}."
        : $"It's overcast and 15 °C in {city}.";

var weatherTool = AIFunctionFactory.Create(
    GetCurrentWeather,
    name: "get_current_weather",
    description: "Returns the current weather conditions for a given city.");

// ── Setup: Register IChatClient ───────────────────────────────────────────────
// AddChatClient is the idiomatic MEAI pattern — it returns a ChatClientBuilder
// for chaining middleware, then Build() registers the final IChatClient singleton.
// DurableChatActivities constructor-injects this on the worker side.
IChatClient openAiChatClient = (IChatClient)new OpenAIClient(
    new ApiKeyCredential(apiKey),
    new OpenAIClientOptions { Endpoint = new Uri(apiBaseUrl) }
).GetChatClient(model);

builder.Services
    .AddChatClient(openAiChatClient)
    .UseFunctionInvocation()
    .Build();

// ── Setup: Register worker + durable AI ──────────────────────────────────────
// AddDurableAI registers DurableChatWorkflow, DurableChatActivities,
// DurableFunctionActivities, and DurableChatSessionClient on the worker.
// AddDurableTools registers weatherTool in the DurableFunctionRegistry so
// DurableFunctionActivities can resolve it by name at activity execution time.
// AddWorkflow<WeatherReportWorkflow> registers the workflow type with the worker.
builder.Services
    .AddHostedTemporalWorker(temporalAddress, "default", taskQueue)
    .AddDurableAI(opts =>
    {
        opts.ActivityTimeout = TimeSpan.FromMinutes(5);
        opts.SessionTimeToLive = TimeSpan.FromHours(1);
    })
    .AddDurableTools(weatherTool)
    .AddWorkflow<WeatherReportWorkflow>();

// ── Start ─────────────────────────────────────────────────────────────────────
var host = builder.Build();
await host.StartAsync();

Console.WriteLine("Worker started.\n");

// ── Run demos ─────────────────────────────────────────────────────────────────
// Run the demo for two cities sequentially to show that each workflow
// execution dispatches a separate, independently tracked activity.
await DurableToolDemo.RunAsync(temporalClient, taskQueue, "Tokyo");
await DurableToolDemo.RunAsync(temporalClient, taskQueue, "London");

// ── Shutdown ──────────────────────────────────────────────────────────────────
try { await host.StopAsync(); } catch (OperationCanceledException) { }
Console.WriteLine("Done.");
