// EvaluatorOptimizer sample — demonstrates the Evaluator-Optimizer multi-agent pattern.
//
// Two agents collaborate in a loop inside a single durable Temporal workflow:
//   • Generator — produces a draft in response to a writing task or revision request.
//   • Evaluator — reviews the draft and either approves it ("APPROVED") or gives feedback.
//
// The workflow repeats up to `maxIterations` times. Because each generation/evaluation
// turn runs as a durable Temporal activity, the process is fault-tolerant: a worker
// crash simply replays from the last committed turn with no duplicate LLM calls.
//
// Prerequisites
// ─────────────
// • A local Temporal server:  temporal server start-dev
// • Set OPENAI_API_KEY in appsettings.json
//
// Run:  dotnet run --project samples/EvaluatorOptimizer/EvaluatorOptimizer.csproj

using System.ClientModel;
using EvaluatorOptimizer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenAI;
using OpenAI.Chat;
using Temporalio.Client;
using Temporalio.Extensions.Agents;
using Temporalio.Extensions.Hosting;

// ── Step 1: Build the application host ───────────────────────────────────────
var builder = Host.CreateApplicationBuilder(args);
builder.Logging.SetMinimumLevel(LogLevel.Warning);

// ── Step 2: Load configuration ────────────────────────────────────────────────
var apiKey = builder.Configuration.GetValue<string>("OPENAI_API_KEY");
var apiBaseUrl = builder.Configuration.GetValue<string>("OPENAI_API_BASE_URL");

if (string.IsNullOrEmpty(apiBaseUrl))
{
    throw new InvalidOperationException("OPENAI_API_BASE_URL is not configured in appsettings.json.");
}

if (string.IsNullOrEmpty(apiKey))
{
    throw new InvalidOperationException("OPENAI_API_KEY is not configured in appsettings.json.");
}

var endpoint = new Uri(apiBaseUrl);
var openAiOptions = new OpenAIClientOptions() { Endpoint = endpoint };
var model = "gpt-4o-mini";

ApiKeyCredential credential = new(apiKey!);
OpenAIClient openAiClient = new(credential, openAiOptions);

// ── Step 3: Create the Generator and Evaluator agents ─────────────────────────
var generator = openAiClient
    .GetChatClient(model)
    .AsAIAgent(
        name: "Generator",
        instructions:
            "You are a skilled technical writer. Produce clear, concise, and well-structured " +
            "content based on the task description. When given revision feedback, incorporate it faithfully.");

var evaluator = openAiClient
    .GetChatClient(model)
    .AsAIAgent(
        name: "Evaluator",
        instructions:
            "You are a precise reviewer. Evaluate the given draft critically. " +
            "If it meets high standards of clarity, accuracy, and completeness, reply with EXACTLY 'APPROVED'. " +
            "Otherwise, provide a numbered list of specific, actionable improvements.");

// ── Step 4: Register agents and the orchestrating workflow ────────────────────
builder.Services
    .AddHostedTemporalWorker("localhost:7233", "default", "evaluator-optimizer")
    .AddTemporalAgents(opts =>
    {
        opts.AddAIAgent(generator);
        opts.AddAIAgent(evaluator);
    })
    .AddWorkflow<EvaluatorOptimizerWorkflow>();

// ── Step 5: Start the host ────────────────────────────────────────────────────
var host = builder.Build();
await host.StartAsync();

Console.WriteLine("Worker started. Submitting EvaluatorOptimizer workflow...\n");

// ── Step 6: Submit the workflow ───────────────────────────────────────────────
var client = host.Services.GetRequiredService<ITemporalClient>();

var task = "Write a concise (100-word) explanation of how Temporal workflows achieve fault tolerance.";

var handle = await client.StartWorkflowAsync(
    (EvaluatorOptimizerWorkflow wf) => wf.RunAsync(task, maxIterations: 3),
    new WorkflowOptions
    {
        Id = $"eval-opt-{Guid.NewGuid():N}",
        TaskQueue = "evaluator-optimizer"
    });

Console.WriteLine($"Workflow started: {handle.Id}\n");
Console.WriteLine($"Task: {task}\n");

// ── Step 7: Wait for the final draft ─────────────────────────────────────────
var result = await handle.GetResultAsync();

Console.WriteLine("── Final Draft ─────────────────────────────────────────────");
Console.WriteLine(result);
Console.WriteLine("────────────────────────────────────────────────────────────\n");

// ── Step 8: Graceful shutdown ─────────────────────────────────────────────────
// TemporalWorker.ExecuteAsync intentionally throws TaskCanceledException on shutdown.
try { await host.StopAsync(); } catch (OperationCanceledException) { }
Console.WriteLine("Done.");
