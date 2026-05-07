// BasicAgent — single durable agent session via Temporalio.Extensions.Agents (v0.3).
//
// Demonstrates the canonical AddDurableAgent registration: an IChatClient registered
// in DI, an agent registered via opts.AddDurableAgent("name", agent => { ... }), and
// a tool added via agent.AddTool(...). The library composes the chat pipeline
// internally — do NOT call .UseFunctionInvocation() on the IChatClient.
//
// Run:  dotnet run --project samples/MAF/BasicAgent/BasicAgent.csproj

using System.ClientModel;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenAI;
using Temporalio.Extensions.Agents;
using Temporalio.Extensions.Hosting;

// ── Step 1: Build the application host ───────────────────────────────────────
var builder = Host.CreateApplicationBuilder(args);
builder.Logging.SetMinimumLevel(LogLevel.Warning); // suppress Temporal SDK noise in the sample

// ── Step 2: Load configuration ───────────────────────────────────────────────
var apiKey = builder.Configuration.GetValue<string>("OPENAI_API_KEY");
var apiBaseUrl = builder.Configuration.GetValue<string>("OPENAI_API_BASE_URL");

if (string.IsNullOrEmpty(apiKey))
    throw new InvalidOperationException(
        "OPENAI_API_KEY is not configured. Set it with: " +
        "dotnet user-secrets set \"OPENAI_API_KEY\" \"sk-...\" --project samples/MAF/BasicAgent");

if (string.IsNullOrEmpty(apiBaseUrl))
    throw new InvalidOperationException("OPENAI_API_BASE_URL is not configured in appsettings.json.");

const string model = "gpt-4o-mini";
var temporalAddress = builder.Configuration.GetValue<string>("TEMPORAL_ADDRESS") ?? "localhost:7233";

var openAiClient = new OpenAIClient(
    new ApiKeyCredential(apiKey),
    new OpenAIClientOptions { Endpoint = new Uri(apiBaseUrl) });

// ── Step 3: Define the agent's tool ──────────────────────────────────────────
static string GetCurrentWeather() => Random.Shared.NextDouble() > 0.5 ? "It's sunny" : "It's raining";
var weatherTool = AIFunctionFactory.Create(
    GetCurrentWeather,
    name: "get_weather",
    description: "Returns the current weather conditions.");

// ── Step 4: Register the IChatClient in DI ───────────────────────────────────
// Register a bare IChatClient — the durable-agent path composes its own pipeline
// internally. Calling .UseFunctionInvocation() here would short-circuit Temporal's
// per-tool activity dispatch.
builder.Services.AddChatClient(openAiClient.GetChatClient(model).AsIChatClient());

// ── Step 5: Register the durable agent ───────────────────────────────────────
// AddDurableAgent is the single registration entry point in v0.3:
//   • agent.ChatClient   — DI factory for the agent's IChatClient (required)
//   • agent.AddTool(...) — registers a tool against this agent's local registry
//   • agent.TimeToLive   — per-agent override of opts.DefaultTimeToLive (14 days)
builder.Services.AddTemporalClient(temporalAddress, "default");
builder.Services
    .AddHostedTemporalWorker("agents")
    .AddTemporalAgents(opts =>
    {
        opts.AddDurableAgent("Assistant", agent =>
        {
            agent.Instructions =
                "You are a helpful geography and weather assistant. " +
                "When asked about weather, always use the get_weather tool.";
            agent.ChatClient   = sp => sp.GetRequiredService<IChatClient>();
            agent.AddTool(weatherTool, opts => opts.NoRetry());
            agent.TimeToLive   = TimeSpan.FromHours(1); // shortened for demo; production default is 14 days
        });
    });

// ── Step 6: Start the host (worker runs as IHostedService) ───────────────────
var host = builder.Build();
await host.StartAsync();

Console.WriteLine("Worker started. Sending messages...\n");

// ── Step 7: Resolve the proxy and open a session ─────────────────────────────
// GetTemporalAgentProxy returns the keyed TemporalAIAgentProxy registered for "Assistant".
// Under the hood it will start (or resume) an AgentWorkflow in Temporal.
var proxy = host.Services.GetTemporalAgentProxy("Assistant");
var session = await proxy.CreateSessionAsync();

Console.WriteLine($"Session workflow ID: {session}\n");

// ── Step 8: Multi-turn conversation ──────────────────────────────────────────
// Each RunAsync call is a Temporal WorkflowUpdate — a durable, acknowledged
// request/response round-trip. Conversation history is preserved in the workflow.
Console.WriteLine("User : What is the capital of France?");
var r1 = await proxy.RunAsync("What is the capital of France?", session);
Console.WriteLine($"Agent: {r1.Text ?? "(no response)"}\n");

Console.WriteLine("User : What is its population?");
var r2 = await proxy.RunAsync("What is its population?", session);
Console.WriteLine($"Agent: {r2.Text ?? "(no response)"}\n");

Console.WriteLine("User : What is the weather like there right now?");
var r3 = await proxy.RunAsync("What is the weather like there right now?", session);
Console.WriteLine($"Agent: {r3.Text ?? "(no response)"}\n");

// ── Step 9: Graceful shutdown ────────────────────────────────────────────────
// The Temporal hosted worker cancels its poll loop on shutdown, which may surface as
// OperationCanceledException through BackgroundService. Swallow it: it is expected.
try
{
    await host.StopAsync();
}
catch (OperationCanceledException)
{
}

Console.WriteLine("Done.");
