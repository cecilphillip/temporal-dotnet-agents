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
builder.Configuration.AddJsonFile("appsettings.local.json", optional: true, reloadOnChange: false);
builder.Logging.SetMinimumLevel(LogLevel.Warning);

// ── Step 2: Load configuration ────────────────────────────────────────────────
var apiKey = builder.Configuration.GetValue<string>("OPENAI_API_KEY");
var apiBaseUrl = builder.Configuration.GetValue<string>("OPENAI_API_BASE_URL");

if (string.IsNullOrEmpty(apiBaseUrl))
    throw new InvalidOperationException("OPENAI_API_BASE_URL is not configured in appsettings.json.");

if (string.IsNullOrEmpty(apiKey))
    throw new InvalidOperationException("OPENAI_API_KEY is not configured in appsettings.json.");

var endpoint = new Uri(apiBaseUrl);
var openAiOptions = new OpenAIClientOptions() { Endpoint = endpoint };
var model = "gpt-4o-mini";
var temporalAddress = builder.Configuration.GetValue<string>("TEMPORAL_ADDRESS") ?? "localhost:7233";

ApiKeyCredential credential = new(apiKey);
OpenAIClient openAiClient = new(credential, openAiOptions);

// ── Step 3: Create the agent ─────────────────────────────────────────────────
// The agent wraps a ChatClient that can use any MEAI middleware.
// Here we show how middleware composition works with Temporal Agents:
//
// The full conversation history is owned by AgentWorkflow; the MEAI middleware
// (like ChatReducer) can trim the context window on each turn for efficiency.
//
// NOTE: This sample demonstrates the architectural principle — MEAI middleware
// like UseChatReducer() can be composed with Temporal Agents. The middleware
// is applied implicitly through the ChatClient passed to AsAIAgent.
var chatClient = (IChatClient)openAiClient.GetChatClient(model);
var agent = chatClient
    .AsAIAgent(
        name: "DocumentAnalyzer",
        description: "Analyzes documents and summarizes their content.",
        instructions: """
        You are a document analysis specialist. Your job is to:
        1. Analyze documents provided by the user
        2. Extract key information and insights
        3. Summarize the content concisely
        4. Answer specific questions about the document

        Be thorough but efficient in your analysis.
        """
    );

// ── Step 4: Register Temporal Agents ─────────────────────────────────────────
// AddHostedTemporalWorker registers the ITemporalClient and hosted worker.
// AddTemporalAgents registers:
//   • AgentWorkflow    — long-lived workflow managing conversation history
//   • AgentActivities  — activity that calls the real IChatClient
//   • ITemporalAgentClient — sends messages via Temporal Update
//   • Keyed AIAgent proxy — what your code calls
builder.Services
    .AddHostedTemporalWorker(temporalAddress, "default", "agents")
    .AddTemporalAgents(options => options.AddAIAgent(agent));

// ── Step 5: Start the host ───────────────────────────────────────────────────
var host = builder.Build();
await host.StartAsync();

Console.WriteLine("=== Document Analyzer (MEAI Middleware Pattern Sample) ===\n");

// ── Step 6: Create a proxy and open a session ────────────────────────────────
var proxy = host.Services.GetTemporalAgentProxy("DocumentAnalyzer");
var session = await proxy.CreateSessionAsync();

Console.WriteLine($"Session workflow ID: {session}\n");

// ── Step 7: Multi-turn conversation ──────────────────────────────────────────
// Each RunAsync call is a durable Temporal WorkflowUpdate.
// Conversation history is owned by AgentWorkflow and persisted across turns.
//
// To apply MEAI middleware like UseChatReducer() in a real scenario,
// wrap the ChatClient before passing it to AsAIAgent:
//
//   var chatClient = openAiClient.GetChatClient(model)
//       .AsBuilder()
//       .UseFunctionInvocation()
//       .UseChatReducer()
//       .Build();
//   var agent = chatClient.AsAIAgent(...);
//
// This sample focuses on the architectural pattern rather than middleware
// application mechanics, which requires type conversions in the current API.
var turns = new[]
{
    "Please analyze this document abstract: 'Climate change represents one of the most significant challenges of our time. Rising temperatures are affecting ecosystems globally, causing species extinction, coral bleaching, and weather pattern disruptions. Mitigation strategies include renewable energy adoption, carbon capture technology, and international cooperation.'",
    "What are the main challenges mentioned in the document?",
    "What solutions are proposed in the text?",
    "Can you summarize this in a single sentence?"
};

foreach (var message in turns)
{
    Console.WriteLine($"User: {message}\n");
    var response = await proxy.RunAsync(message, session);
    Console.WriteLine($"Agent: {response.Text}\n");
    Console.WriteLine(new string('-', 80) + "\n");
}

// ── Step 8: Graceful shutdown ────────────────────────────────────────────────
try { await host.StopAsync(); } catch (OperationCanceledException) { }

Console.WriteLine("Done.");
