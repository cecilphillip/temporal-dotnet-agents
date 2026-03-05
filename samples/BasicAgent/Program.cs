// BasicAgent sample — demonstrates how to configure a Temporal worker and send messages
// to a durable agent session backed by Temporalio.Extensions.Agents.
//
// Prerequisites
// ─────────────
// • A local Temporal server:  temporal server start-dev
//   (The dev server starts on localhost:7233 with the "default" namespace.)
//
// Run:  dotnet run --project samples/BasicAgent/BasicAgent.csproj

using System.ClientModel;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenAI;
using OpenAI.Chat;
using Temporalio.Extensions.Agents;

// ── Step 1: Build the application host ───────────────────────────────────────
var builder = Host.CreateApplicationBuilder(args);
builder.Configuration.AddJsonFile("appsettings.local.json", optional: true, reloadOnChange: false);
builder.Logging.SetMinimumLevel(LogLevel.Warning); // suppress Temporal SDK noise in the sample

// ── Step 2: Provide an IChatClient ───────────────────────────────────────────
// This sample uses a local stub that echoes back every message so you can run it
// without any API credentials.  Swap it for a real client when you're ready:
var apiKey = builder.Configuration.GetValue<string>("OPENAI_API_KEY");
var apiBaseUrl = builder.Configuration.GetValue<string>("OPENAI_API_BASE_URL");

if (string.IsNullOrEmpty(apiBaseUrl))
    throw new InvalidOperationException("OPENAI_API_BASE_URL is not configured in appsettings.json.");

if (string.IsNullOrEmpty(apiKey))
    throw new InvalidOperationException("OPENAI_API_KEY is not configured in appsettings.json.");

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

// ── Step 3: Create the agent ──────────────────────────────────────────────────
// ChatClientAgent wraps any IChatClient as a full Microsoft Agent Framework agent.
var agent = openAiClient
    .GetChatClient(model)
    .AsAIAgent(
        name: "Assistant",
        instructions: "You are a helpful assistant.",
        tools: [weatherTool],
        clientFactory: client => client.AsBuilder()
            .UseFunctionInvocation().Build()
    );

// ── Step 4: Register Temporal Agents ─────────────────────────────────────────
// ConfigureTemporalAgents registers everything needed in one call:
//   • ITemporalClient  — connection to the Temporal server
//   • AgentWorkflow    — long-lived workflow that is the durable agent session
//   • AgentActivities  — activity that calls the real IChatClient (preserves determinism)
//   • ITemporalAgentClient — sends messages via Temporal Update (no polling required)
//   • Keyed AIAgent proxy — the object your code actually calls
builder.Services.ConfigureTemporalAgents(
    configure: options => { options.AddAIAgent(agent, timeToLive: TimeSpan.FromHours(1)); },
    taskQueue: "agents",
    targetHost: temporalAddress,
    @namespace: "default");

// ── Step 5: Start the host (worker runs as IHostedService) ────────────────────
var host = builder.Build();
await host.StartAsync();

Console.WriteLine("Worker started. Sending messages...\n");

// ── Step 6: Resolve the proxy and open a session ─────────────────────────────
// GetTemporalAgentProxy returns the keyed TemporalAIAgentProxy registered for "Assistant".
// Under the hood it will start (or resume) an AgentWorkflow in Temporal.
var proxy = host.Services.GetTemporalAgentProxy("Assistant");
var session = await proxy.CreateSessionAsync();

Console.WriteLine($"Session workflow ID: {session}\n");

// ── Step 7: Multi-turn conversation ──────────────────────────────────────────
// Each RunAsync call is a Temporal WorkflowUpdate — a durable, acknowledged
// request/response round-trip.  Conversation history is preserved in the workflow.
var r1 = await proxy.RunAsync("What is the capital of France?", session);
Console.WriteLine($"User : What is the capital of France?");
Console.WriteLine($"Agent: {r1.Text}\n");

var r2 = await proxy.RunAsync("What is its population?", session);
Console.WriteLine($"User : What is its population?");
Console.WriteLine($"Agent: {r2.Text}\n");

// ── Step 8: Graceful shutdown ─────────────────────────────────────────────────
// TemporalWorker.ExecuteAsync intentionally throws TaskCanceledException when its
// stoppingToken is cancelled — that exception propagates through BackgroundService and
// may surface here depending on the .NET hosting version. Swallow it: it is expected.
try
{
    await host.StopAsync();
}
catch (OperationCanceledException)
{
}

Console.WriteLine("Done.");