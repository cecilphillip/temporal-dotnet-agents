// SplitWorkerClient / Worker — owns the Temporal worker (AgentWorkflow + agent activities).
// Start this before running the Client in a separate terminal.
//
// Run:  dotnet run --project samples/MAF/SplitWorkerClient/Worker/Worker.csproj

using System.ClientModel;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenAI;
using OpenAI.Chat;
using Temporalio.Extensions.Agents;
using Temporalio.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.SetMinimumLevel(LogLevel.Warning);

// ── Step 1: Load configuration ───────────────────────────────────────────────
// The worker is the ONLY process that needs an IChatClient. The client process
// sends messages via Temporal and never touches the AI backend directly.
var apiKey = builder.Configuration.GetValue<string>("OPENAI_API_KEY");
var apiBaseUrl = builder.Configuration.GetValue<string>("OPENAI_API_BASE_URL");

if (string.IsNullOrEmpty(apiBaseUrl))
    throw new InvalidOperationException("OPENAI_API_BASE_URL is not configured in appsettings.json.");

if (string.IsNullOrEmpty(apiKey))
    throw new InvalidOperationException(
        "OPENAI_API_KEY is not configured. Set it with: " +
        "dotnet user-secrets set \"OPENAI_API_KEY\" \"sk-...\" --project samples/MAF/SplitWorkerClient/Worker");

const string model = "gpt-4o-mini";
var temporalAddress = builder.Configuration.GetValue<string>("TEMPORAL_ADDRESS") ?? "localhost:7233";

var openAiClient = new OpenAIClient(
    new ApiKeyCredential(apiKey),
    new OpenAIClientOptions { Endpoint = new Uri(apiBaseUrl) });

// ── Step 2: Define the agent's tool ──────────────────────────────────────────
static string GetCurrentWeather() => Random.Shared.NextDouble() > 0.5 ? "It's sunny" : "It's raining";
var weatherTool = AIFunctionFactory.Create(
    GetCurrentWeather,
    name: "get_weather",
    description: "Returns the current weather conditions.");

// ── Step 3: Register the IChatClient in DI ───────────────────────────────────
builder.Services.AddChatClient(openAiClient.GetChatClient(model).AsIChatClient());

// ── Step 4: Register the full Temporal Agent stack ───────────────────────────
// AddTemporalClient registers ITemporalClient in DI (required by ITemporalAgentClient).
// AddHostedTemporalWorker registers the hosted worker on the given task queue.
// AddTemporalAgents + AddDurableAgent registers the durable workflow, the agent
// activities, and the per-agent tool registry.
//
// The Client process registers a TemporalAIAgentProxy with the same name via
// AddTemporalAgentProxies(...) — see Client/Program.cs.
builder.Services.AddTemporalClient(temporalAddress, "default");
builder.Services
    .AddHostedTemporalWorker("agents")
    .AddTemporalAgents(opts =>
    {
        opts.AddDurableAgent("Assistant", agent =>
        {
            agent.Instructions = "You are a helpful assistant.";
            agent.ChatClient   = sp => sp.GetRequiredService<IChatClient>();
            agent.AddTool(weatherTool);
            agent.TimeToLive   = TimeSpan.FromHours(1);
        });
    });

// ── Step 5: Run until Ctrl+C ─────────────────────────────────────────────────
Console.WriteLine("Agent worker started. Listening on task queue 'agents'...");
Console.WriteLine("Start the Client in another terminal, then press Ctrl+C here to stop.\n");

var host = builder.Build();
await host.RunAsync(); // blocks until shutdown signal
