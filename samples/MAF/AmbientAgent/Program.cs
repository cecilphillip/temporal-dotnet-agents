// AmbientAgent sample — demonstrates ambient (background) agent patterns with Temporal.
//
// An ambient agent operates continuously in the background, monitoring data streams and
// triggering actions without direct user prompts. This sample simulates a system health
// monitor that:
//   • Ingests health-check readings via workflow signals (fire-and-forget)
//   • Periodically calls an LLM AnalysisAgent to assess trends
//   • Proactively signals a separate AlertWorkflow when anomalies are detected
//   • Supports continue-as-new for indefinite monitoring
//
// Key patterns demonstrated:
//   1. Custom [WorkflowSignal] for event ingestion (ambient data stream)
//   2. Proactive LLM analysis without user prompts
//   3. Agent-to-agent communication via cross-workflow signaling
//   4. [WorkflowQuery] for external observability
//   5. Continue-as-new with carried state for long-lived operation
//
// Prerequisites
// ─────────────
// • A local Temporal server:  temporal server start-dev
// • OPENAI_API_KEY: dotnet user-secrets set "OPENAI_API_KEY" "sk-..." --project samples/MAF/AmbientAgent
//
// Run:  dotnet run --project samples/AmbientAgent/AmbientAgent.csproj

using System.ClientModel;
using AmbientAgent;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenAI;
using OpenAI.Chat;
using Temporalio.Api.Enums.V1;
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
    throw new InvalidOperationException("OPENAI_API_KEY is not configured. Set it with: dotnet user-secrets set \"OPENAI_API_KEY\" \"sk-...\" --project samples/MAF/AmbientAgent");
}

var endpoint = new Uri(apiBaseUrl);
var openAiOptions = new OpenAIClientOptions() { Endpoint = endpoint };
var model = "gpt-4o-mini";
var temporalAddress = builder.Configuration.GetValue<string>("TEMPORAL_ADDRESS") ?? "localhost:7233";

ApiKeyCredential credential = new(apiKey!);
OpenAIClient openAiClient = new(credential, openAiOptions);

// ── Step 3: Create the AnalysisAgent and AlertAgent ──────────────────────────
var analysisAgent = openAiClient
    .GetChatClient(model)
    .AsAIAgent(
        name: "AnalysisAgent",
        instructions:
            "You are a system health analyst. You receive time-series health metrics " +
            "(CPU%, Memory%, Temperature) and must assess whether the readings are normal or anomalous. " +
            "Respond with 'NORMAL: <brief summary>' if all metrics are within acceptable ranges, " +
            "or 'ANOMALY: <description of the concerning pattern>' if you detect spikes, " +
            "sustained high usage, or temperature warnings. Be concise.");

var alertAgent = openAiClient
    .GetChatClient(model)
    .AsAIAgent(
        name: "AlertAgent",
        instructions:
            "You are an alert notification composer. Given anomaly details and recent system " +
            "readings, compose a concise, actionable alert notification suitable for an operations " +
            "team. Include: severity assessment, affected metrics, and recommended next steps. " +
            "Keep it under 100 words.");

// ── Step 4: Register ITemporalClient for AlertActivities ─────────────────────
// AlertActivities needs ITemporalClient to signal the alert workflow.
builder.Services.AddTemporalClient(opts =>
{
    opts.TargetHost = temporalAddress;
    opts.Namespace = "default";
});

// ── Step 5: Register the hosted worker with agents and workflows ─────────────
builder.Services
    .AddHostedTemporalWorker("ambient-agents")
    .AddTemporalAgents(opts =>
    {
        opts.AddAIAgent(analysisAgent, timeToLive: TimeSpan.FromHours(1));
        opts.AddAIAgent(alertAgent, timeToLive: TimeSpan.FromHours(1));
    })
    .AddWorkflow<MonitorWorkflow>()
    .AddWorkflow<AlertWorkflow>()
    .AddSingletonActivities<AlertActivities>();

// ── Step 6: Start the host ───────────────────────────────────────────────────
var host = builder.Build();
await host.StartAsync();

Console.WriteLine("Worker started. Launching ambient agent workflows...\n");

var client = host.Services.GetRequiredService<ITemporalClient>();

const string monitorWorkflowId = "ambient-monitor-001";
const string alertWorkflowId = "ambient-alert-001";

// ── Step 7: Start the AlertWorkflow (must exist before MonitorWorkflow signals it)
await client.StartWorkflowAsync(
    (AlertWorkflow wf) => wf.RunAsync(),
    new WorkflowOptions
    {
        Id = alertWorkflowId,
        TaskQueue = "ambient-agents",
        IdConflictPolicy = WorkflowIdConflictPolicy.UseExisting
    });

Console.WriteLine($"AlertWorkflow started: {alertWorkflowId}");

// ── Step 8: Start the MonitorWorkflow ────────────────────────────────────────
var monitorHandle = await client.StartWorkflowAsync(
    (MonitorWorkflow wf) => wf.RunAsync(new MonitorWorkflowInput
    {
        AlertWorkflowId = alertWorkflowId,
        AnalysisInterval = 5,
        MaxBufferSize = 50
    }),
    new WorkflowOptions
    {
        Id = monitorWorkflowId,
        TaskQueue = "ambient-agents",
        IdConflictPolicy = WorkflowIdConflictPolicy.UseExisting
    });

Console.WriteLine($"MonitorWorkflow started: {monitorWorkflowId}\n");

// ── Step 9: Simulate 20 health-check readings ───────────────────────────────
// Normal readings for most, with a spike window at readings 13-15.
Console.WriteLine("── Sending simulated health readings ───────────────────────");

var random = new Random(42); // Deterministic seed for reproducibility
var now = DateTimeOffset.UtcNow;

for (int i = 1; i <= 20; i++)
{
    double cpu, memory, temp;

    if (i is >= 13 and <= 15)
    {
        // Spike window — anomalous readings
        cpu = 95 + random.NextDouble() * 5;       // 95-100%
        memory = 96 + random.NextDouble() * 4;     // 96-100%
        temp = 88 + random.NextDouble() * 7;       // 88-95°C
    }
    else
    {
        // Normal readings
        cpu = 20 + random.NextDouble() * 40;       // 20-60%
        memory = 30 + random.NextDouble() * 35;    // 30-65%
        temp = 45 + random.NextDouble() * 20;      // 45-65°C
    }

    var reading = new HealthCheckData(
        Timestamp: now.AddSeconds(i * 10),
        CpuPercent: Math.Round(cpu, 1),
        MemoryPercent: Math.Round(memory, 1),
        TemperatureCelsius: Math.Round(temp, 1));

    await monitorHandle.SignalAsync(wf => wf.IngestHealthCheckAsync(reading));
    Console.WriteLine($"  Reading {i,2}: CPU={reading.CpuPercent,5:F1}% Mem={reading.MemoryPercent,5:F1}% Temp={reading.TemperatureCelsius,5:F1}°C{(i is >= 13 and <= 15 ? " ⚠️ SPIKE" : "")}");

    // Small delay between readings to let the workflow process them.
    await Task.Delay(200);
}

// ── Step 10: Wait for analyses to complete ───────────────────────────────────
Console.WriteLine("\nWaiting for LLM analyses to complete...\n");
await Task.Delay(15_000); // Give the LLM time to process 4 analysis batches

// ── Step 11: Query status from both workflows ────────────────────────────────
Console.WriteLine("── Monitor Status ──────────────────────────────────────────");
var status = await monitorHandle.QueryAsync(wf => wf.GetStatus());
Console.WriteLine($"  Buffer size: {status.BufferSize}");
Console.WriteLine($"  Total readings: {status.TotalReadings}");
Console.WriteLine($"  Analyses performed: {status.RecentAnalyses.Count}");
foreach (var analysis in status.RecentAnalyses)
{
    Console.WriteLine($"\n  Analysis: {analysis}");
}

Console.WriteLine("\n── Alert Notifications ─────────────────────────────────────");
var alertHandle = client.GetWorkflowHandle<AlertWorkflow>(alertWorkflowId);
var notifications = await alertHandle.QueryAsync(wf => wf.GetNotifications());

if (notifications.Count == 0)
{
    Console.WriteLine("  (no anomalies detected)");
}
else
{
    foreach (var notification in notifications)
    {
        Console.WriteLine($"\n  {notification}");
    }
}

// ── Step 12: Signal shutdown and stop gracefully ─────────────────────────────
Console.WriteLine("\n── Shutting down ───────────────────────────────────────────");
await monitorHandle.SignalAsync(wf => wf.ShutdownAsync());
await alertHandle.SignalAsync(wf => wf.ShutdownAsync());
Console.WriteLine("  Shutdown signals sent.");

try { await host.StopAsync(); } catch (OperationCanceledException) { }
Console.WriteLine("Done.");
