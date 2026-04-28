// SplitWorkerClient — Worker
//
// This process owns the Temporal worker: it hosts AgentWorkflow + AgentActivities and
// runs the real AI inference. It has no knowledge of callers.
//
// Prerequisites
// ─────────────
// • A local Temporal server:  temporal server start-dev
// • OPENAI_API_KEY: dotnet user-secrets set "OPENAI_API_KEY" "sk-..." --project samples/MAF/SplitWorkerClient/Worker
//
// Run this first, then start the Client in a separate terminal:
//   dotnet run --project samples/SplitWorkerClient/Worker/Worker.csproj

using System.ClientModel;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenAI;
using OpenAI.Chat;
using Microsoft.Extensions.DependencyInjection;
using Temporalio.Extensions.Agents;

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.SetMinimumLevel(LogLevel.Warning);

// ── Step 1: Provide a real IChatClient ───────────────────────────────────────
// The worker is the ONLY process that needs an IChatClient. The client process
// sends messages via Temporal and never touches the AI backend directly.
var apiKey = builder.Configuration.GetValue<string>("OPENAI_API_KEY");
var apiBaseUrl = builder.Configuration.GetValue<string>("OPENAI_API_BASE_URL");

if (string.IsNullOrEmpty(apiBaseUrl))
    throw new InvalidOperationException("OPENAI_API_BASE_URL is not configured in appsettings.json.");

if (string.IsNullOrEmpty(apiKey))
    throw new InvalidOperationException("OPENAI_API_KEY is not configured. Set it with: dotnet user-secrets set \"OPENAI_API_KEY\" \"sk-...\" --project samples/MAF/SplitWorkerClient/Worker");

var endpoint = new Uri(apiBaseUrl);
var openAiOptions = new OpenAIClientOptions()
{
    Endpoint = endpoint
};
var model = "gpt-4o-mini";
var temporalAddress = builder.Configuration.GetValue<string>("TEMPORAL_ADDRESS") ?? "localhost:7233";

ApiKeyCredential credential = new(apiKey);
OpenAIClient openAiClient = new(credential, openAiOptions);

static string GetCurrentWeather() => Random.Shared.NextDouble() > 0.5 ? "It's sunny" : "It's raining";
var weatherTool = AIFunctionFactory.Create(GetCurrentWeather, "get_weather", "Returns the current weather conditions.");

var agent = openAiClient
    .GetChatClient(model)
    .AsAIAgent(
        name: "Assistant",
        instructions: "You are a helpful assistant.",
        tools: [weatherTool],
        clientFactory: client => client.AsBuilder()
            .UseFunctionInvocation().Build()
    );

// ── Step 2: Register the full Temporal Agent stack ────────────────────────────
// AddHostedTemporalWorker registers the ITemporalClient and hosted worker.
// AddTemporalAgents registers:
//   • AgentWorkflow          — long-lived session workflow (durable state)
//   • AgentActivities        — activity that runs the real AI inference
//   • ITemporalAgentClient   — handles WorkflowUpdates from client processes
//   • Keyed AIAgent proxy    — used only if this process also sends messages
//
// The real agent (ChatClientAgent + IChatClient) MUST be registered here so
// AgentActivities can resolve it when executing AI requests.
builder.Services
    .AddHostedTemporalWorker(temporalAddress, "default", "agents")
    .AddTemporalAgents(options => { options.AddAIAgent(agent, timeToLive: TimeSpan.FromHours(1)); });

// ── Step 3: Run until Ctrl+C ──────────────────────────────────────────────────
Console.WriteLine("Agent worker started. Listening on task queue 'agents'...");
Console.WriteLine("Start the Client in another terminal, then press Ctrl+C here to stop.\n");

var host = builder.Build();
await host.RunAsync(); // blocks until shutdown signal