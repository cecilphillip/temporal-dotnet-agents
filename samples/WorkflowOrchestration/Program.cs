// WorkflowOrchestration sample — demonstrates how a Temporal workflow can orchestrate
// (call) an AI agent as a sub-agent, using the new fluent AddTemporalAgents() API.
//
// Key differences from BasicAgent:
// • Uses the new fluent builder API: .AddTemporalAgents() instead of ConfigureTemporalAgents()
// • Demonstrates a workflow that internally calls an agent via TemporalAIAgent
// • Shows how agent calls flow through Temporal activities for determinism
//
// Prerequisites
// ─────────────
// • A local Temporal server:  temporal server start-dev
//   (The dev server starts on localhost:7233 with the "default" namespace.)
//
// Run:  dotnet run --project samples/WorkflowOrchestration/WorkflowOrchestration.csproj

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
builder.Configuration.AddJsonFile("appsettings.local.json", optional: true, reloadOnChange: false);
builder.Logging.SetMinimumLevel(LogLevel.Warning);

// ── Step 2: Provide an IChatClient ───────────────────────────────────────────
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

// Simple tool for the agent: get current weather
static string GetCurrentWeather() => Random.Shared.NextDouble() > 0.5 ? "sunny" : "rainy";
var weatherTool = AIFunctionFactory.Create(
    GetCurrentWeather,
    "get_weather",
    "Returns the current weather conditions.");

// ── Step 3: Create the agent ──────────────────────────────────────────────────
var agent = openAiClient
    .GetChatClient(model)
    .AsAIAgent(
        name: "WeatherAssistant",
        instructions: "You are a helpful weather assistant. Use the get_weather tool to answer questions.",
        tools: [weatherTool],
        clientFactory: client => client.AsBuilder()
            .UseFunctionInvocation().Build()
    );

// ── Step 4: Register Temporal Client and Agents using the NEW fluent API ────────────────
// The fluent AddTemporalAgents() method composes with the worker setup:
builder.Services
    .AddTemporalClient(temporalAddress, "default");

builder.Services
    .AddHostedTemporalWorker(temporalAddress, "default", "orchestration")
    .AddTemporalAgents(options =>
    {
        // Register the agent (or factory for DI-resolved agents)
        options.AddAIAgent(agent, timeToLive: TimeSpan.FromHours(1));
    })
    .AddWorkflow<WeatherOrchestrationWorkflow>();

// ── Step 5: Start the host (worker runs as IHostedService) ────────────────────
var host = builder.Build();
await host.StartAsync();

Console.WriteLine("Worker started. Submitting orchestration workflow...\n");

// ── Step 6: Submit the orchestrating workflow to Temporal ─────────────────────
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

// ── Step 7: Wait for the workflow to complete ────────────────────────────────
try
{
    var result = await handle.GetResultAsync();
    Console.WriteLine($"Orchestration workflow result: {result}\n");
}
catch (Exception ex)
{
    Console.WriteLine($"Workflow failed: {ex.Message}\n");
}

// ── Step 8: Graceful shutdown ─────────────────────────────────────────────────
// TemporalWorker.ExecuteAsync intentionally throws TaskCanceledException on shutdown.
try { await host.StopAsync(); } catch (OperationCanceledException) { }
Console.WriteLine("Done.");

// ─────────────────────────────────────────────────────────────────────────────
// WORKFLOW DEFINITION
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// An orchestrating workflow that calls an AI agent to answer a question.
/// </summary>
[Workflow("WorkflowOrchestration.WeatherOrchestration")]
public class WeatherOrchestrationWorkflow
{
    /// <summary>
    /// Runs the orchestration: receives a question, calls the agent, returns the answer.
    /// </summary>
    /// <remarks>
    /// Inside a workflow, you use TemporalAIAgent to call other agents.
    /// The agent activity is executed via Workflow.ExecuteActivityAsync(), which ensures
    /// determinism (the activity result is cached, so replays get the same value).
    /// </remarks>
    [WorkflowRun]
    public async Task<string> RunAsync(string userQuestion)
    {
        // Create a TemporalAIAgent that calls the "WeatherAssistant" agent
        // Use the GetAgent extension method from TemporalWorkflowExtensions
        var agent = GetAgent("WeatherAssistant");

        // Create a session for the conversation
        var session = await agent.CreateSessionAsync();

        // Call the agent with the user's question
        // This internally:
        //   1. Creates a RunRequest from the user question
        //   2. Executes AgentActivities.ExecuteAgentAsync() via Workflow.ExecuteActivityAsync
        //   3. Preserves conversation history in the workflow state
        var response = await agent.RunAsync(userQuestion, session);

        return response.Text ?? "No response";
    }
}
