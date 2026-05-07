// WorkflowOrchestration — a Temporal workflow that calls a durable AI agent as a
// sub-agent via TemporalWorkflowExtensions.GetAgent().
//
// Run:  dotnet run --project samples/MAF/WorkflowOrchestration/WorkflowOrchestration.csproj

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
using Temporalio.Workflows;
using static Temporalio.Extensions.Agents.TemporalWorkflowExtensions;

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
        "dotnet user-secrets set \"OPENAI_API_KEY\" \"sk-...\" --project samples/MAF/WorkflowOrchestration");

const string model = "gpt-4o-mini";
var temporalAddress = builder.Configuration.GetValue<string>("TEMPORAL_ADDRESS") ?? "localhost:7233";

var openAiClient = new OpenAIClient(
    new ApiKeyCredential(apiKey),
    new OpenAIClientOptions { Endpoint = new Uri(apiBaseUrl) });

// ── Step 3: Define the weather tool ──────────────────────────────────────────
static string GetCurrentWeather() => Random.Shared.NextDouble() > 0.5 ? "sunny" : "rainy";
var weatherTool = AIFunctionFactory.Create(
    GetCurrentWeather,
    name: "get_weather",
    description: "Returns the current weather conditions.");

// ── Step 4: Register the IChatClient in DI ───────────────────────────────────
builder.Services.AddChatClient(openAiClient.GetChatClient(model).AsIChatClient());

// ── Step 5: Register the worker, durable agent, and orchestrating workflow ───
builder.Services.AddTemporalClient(temporalAddress, "default");
builder.Services
    .AddHostedTemporalWorker("orchestration")
    .AddTemporalAgents(opts =>
    {
        opts.AddDurableAgent("WeatherAssistant", agent =>
        {
            agent.Instructions = "You are a helpful weather assistant. Use the get_weather tool to answer questions.";
            agent.ChatClient   = sp => sp.GetRequiredService<IChatClient>();
            agent.AddTool(weatherTool);
            agent.TimeToLive   = TimeSpan.FromHours(1);
        });
    })
    .AddWorkflow<WeatherOrchestrationWorkflow>();

// ── Step 6: Start the host ───────────────────────────────────────────────────
var host = builder.Build();
await host.StartAsync();

Console.WriteLine("Worker started. Submitting orchestration workflow...\n");

// ── Step 7: Submit the orchestrating workflow ────────────────────────────────
var client = host.Services.GetRequiredService<ITemporalClient>();

var weatherOrchestrationId = $"weather-orchestration-{Guid.NewGuid()}";
var handle = await client.StartWorkflowAsync(
    (WeatherOrchestrationWorkflow wf) => wf.RunAsync("What's the weather like?"),
    new WorkflowOptions
    {
        Id = weatherOrchestrationId,
        TaskQueue = "orchestration"
    });

Console.WriteLine($"Orchestration workflow started: {weatherOrchestrationId}\n");

// ── Step 8: Wait for the workflow to complete ────────────────────────────────
try
{
    var result = await handle.GetResultAsync();
    Console.WriteLine($"Orchestration workflow result: {result}\n");
}
catch (Exception ex)
{
    Console.WriteLine($"Workflow failed: {ex.Message}\n");
}

// ── Step 9: Graceful shutdown ────────────────────────────────────────────────
try { await host.StopAsync(); } catch (OperationCanceledException) { }
Console.WriteLine("Done.");

// ─────────────────────────────────────────────────────────────────────────────
// WORKFLOW DEFINITION
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// An orchestrating workflow that calls a durable agent to answer a question.
/// </summary>
[Workflow("WorkflowOrchestration.WeatherOrchestration")]
public class WeatherOrchestrationWorkflow
{
    /// <summary>
    /// Runs the orchestration: receives a question, calls the agent, returns the answer.
    /// </summary>
    /// <remarks>
    /// Inside a workflow, use <c>TemporalWorkflowExtensions.GetAgent</c> to obtain a
    /// <c>TemporalAIAgent</c>. Each call to <c>RunAsync</c> dispatches activities
    /// (RunDurableAgentStep + InvokeAgentTool per tool call) so results are durable and
    /// replay-cached.
    /// </remarks>
    [WorkflowRun]
    public async Task<string> RunAsync(string userQuestion)
    {
        var agent = GetAgent("WeatherAssistant");
        var session = await agent.CreateSessionAsync();
        var response = await agent.RunAsync(userQuestion, session);

        return response.Text ?? "No response";
    }
}
