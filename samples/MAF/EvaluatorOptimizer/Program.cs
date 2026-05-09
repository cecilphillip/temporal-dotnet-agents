// EvaluatorOptimizer — Generator and Evaluator agents collaborate in a durable loop
// until the draft is approved or the iteration limit is reached.
//
// Run:  dotnet run --project samples/MAF/EvaluatorOptimizer/EvaluatorOptimizer.csproj

using System.ClientModel;
using EvaluatorOptimizer;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenAI;
using Temporalio.Client;
using Temporalio.Extensions.Agents;
using Temporalio.Extensions.Hosting;

// ── Step 1: Build the application host ───────────────────────────────────────
var builder = Host.CreateApplicationBuilder(args);
builder.Logging.SetMinimumLevel(LogLevel.Warning);

// ── Step 2: Load configuration ───────────────────────────────────────────────
var apiKey = builder.Configuration.GetValue<string>("OPENAI_API_KEY");
var apiBaseUrl = builder.Configuration.GetValue<string>("OPENAI_API_BASE_URL");

if (string.IsNullOrEmpty(apiKey))
    throw new InvalidOperationException(
        "OPENAI_API_KEY is not configured. Set it with: " +
        "dotnet user-secrets set \"OPENAI_API_KEY\" \"sk-...\" --project samples/MAF/EvaluatorOptimizer");

if (string.IsNullOrEmpty(apiBaseUrl))
    throw new InvalidOperationException("OPENAI_API_BASE_URL is not configured in appsettings.json.");

const string model = "gpt-4o-mini";
var temporalAddress = builder.Configuration.GetValue<string>("TEMPORAL_ADDRESS") ?? "localhost:7233";

var openAiClient = new OpenAIClient(
    new ApiKeyCredential(apiKey),
    new OpenAIClientOptions { Endpoint = new Uri(apiBaseUrl) });

// ── Step 3: Register the IChatClient in DI ───────────────────────────────────
builder.Services.AddChatClient(openAiClient.GetChatClient(model).AsIChatClient());

// ── Step 4: Register the two collaborating agents and the orchestrating workflow ─
builder.Services.AddTemporalClient(temporalAddress, "default");
builder.Services
    .AddHostedTemporalWorker("evaluator-optimizer")
    .AddTemporalAgents(opts =>
    {
        opts.AddDurableAgent("Generator", agent =>
        {
            agent.Instructions =
                "You are a skilled technical writer. Produce clear, concise, and well-structured " +
                "content based on the task description. When given revision feedback, incorporate it faithfully.";
            agent.ChatClient = sp => sp.GetRequiredService<IChatClient>();
        });

        opts.AddDurableAgent("Evaluator", agent =>
        {
            agent.Instructions =
                "You are a precise reviewer. Evaluate the given draft critically. " +
                "If it meets high standards of clarity, accuracy, and completeness, reply with EXACTLY 'APPROVED'. " +
                "Otherwise, provide a numbered list of specific, actionable improvements.";
            agent.ChatClient = sp => sp.GetRequiredService<IChatClient>();
        });
    })
    .AddWorkflow<EvaluatorOptimizerWorkflow>();

// ── Step 5: Start the host ───────────────────────────────────────────────────
var host = builder.Build();
await host.StartAsync();

Console.WriteLine("Worker started. Submitting EvaluatorOptimizer workflow...\n");

// ── Step 6: Submit the workflow ──────────────────────────────────────────────
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
try
{
    var result = await handle.GetResultAsync();

    Console.WriteLine("── Final Draft ─────────────────────────────────────────────");
    Console.WriteLine(result);
    Console.WriteLine("────────────────────────────────────────────────────────────\n");
}
catch (Exception ex)
{
    Console.WriteLine($"Workflow failed: {ex.Message}\n");
}

// ── Step 8: Graceful shutdown ────────────────────────────────────────────────
try { await host.StopAsync(); } catch (OperationCanceledException) { }
Console.WriteLine("Done.");
