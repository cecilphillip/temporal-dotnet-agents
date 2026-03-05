// SplitWorkerClient — Client
//
// This process sends messages to agent sessions running in the Worker process.
// It has no IChatClient, no workflow/activity registrations, and no AI dependencies —
// it only needs a connection to the same Temporal server.
//
// Prerequisites
// ─────────────
// • Temporal server running:  temporal server start-dev
// • Worker running in a separate terminal (start Worker first)
//
// Run:  dotnet run --project samples/SplitWorkerClient/Client/Client.csproj

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Temporalio.Extensions.Agents;

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.SetMinimumLevel(LogLevel.Warning);
var temporalAddress = builder.Configuration.GetValue<string>("TEMPORAL_ADDRESS") ?? "localhost:7233";

// ── Step 1: Register client-side Temporal infrastructure only ────────────────
// AddTemporalAgentProxies wires up:
//   • ITemporalClient        — connection to the Temporal server
//   • ITemporalAgentClient   — sends messages via WorkflowUpdate (no polling)
//   • Keyed AIAgent proxy    — the surface your code calls
//
// Crucially, NO worker, NO AgentWorkflow, NO AgentActivities, and NO IChatClient
// are registered here. All of that lives in the Worker process.
builder.Services.AddTemporalAgentProxies(
    configure: options =>
    {
        // Declare which agents you want to send messages to.
        // AddAgentProxy registers only the name + optional TTL — no factory needed.
        // The TTL here must match (or be compatible with) what the Worker registers,
        // since it is used when this client starts a brand-new session workflow.
        options.AddAgentProxy("Assistant", timeToLive: TimeSpan.FromHours(1));
    },
    taskQueue: "agents",           // must match the Worker's task queue
    targetHost: temporalAddress,
    @namespace: "default");

var host = builder.Build();
await host.StartAsync();

// ── Step 2: Resolve the proxy ─────────────────────────────────────────────────
// The proxy is a TemporalAIAgentProxy: calling RunAsync on it sends a
// WorkflowUpdate to the AgentWorkflow running in the Worker process.
var proxy = host.Services.GetTemporalAgentProxy("Assistant");
// ── Step 3: Create a session ──────────────────────────────────────────────────
// CreateSessionAsync generates a TemporalAgentSessionId (workflow ID: ta-assistant-{guid}).
// The AgentWorkflow is started in the Worker when the first message is sent.
var session = await proxy.CreateSessionAsync();
Console.WriteLine($"Session workflow ID: {session}\n");

// ── Step 4: Multi-turn conversation ───────────────────────────────────────────
// Each RunAsync is a durable WorkflowUpdate — the Worker processes it,
// runs the agent activity, appends to conversation history, and returns the response.
// Context is preserved across turns because history lives in the workflow state.
var r1 = await proxy.RunAsync("What is the capital of France?", session);
Console.WriteLine($"User : What is the capital of France?");
Console.WriteLine($"Agent: {r1.Text}\n");

var r2 = await proxy.RunAsync("What is its population?", session);
Console.WriteLine($"User : What is its population?");
Console.WriteLine($"Agent: {r2.Text}\n");

var r3 = await proxy.RunAsync("What's the current weather condition?", session);
Console.WriteLine($"User : What's the current weather condition");
Console.WriteLine($"Agent: {r3.Text}\n");

// ── Step 5: Reuse the session from a new client instance ─────────────────────
// Sessions are durable: you can reconnect to an existing session from its workflow ID.
// session.ToString() returns the Temporal workflow ID (e.g. "ta-assistant-abc123").
// This simulates a second client process picking up where the first left off.
var sessionId = TemporalAgentSessionId.Parse(session.ToString()!);
var resumedSession = new TemporalAgentSession(sessionId);

var r4 = await proxy.RunAsync("Summarize what we discussed.", resumedSession);
Console.WriteLine($"User : Summarize what we discussed.");
Console.WriteLine($"Agent: {r4.Text}\n");

// TemporalWorker.ExecuteAsync intentionally throws TaskCanceledException on shutdown.
try { await host.StopAsync(); } catch (OperationCanceledException) { }
Console.WriteLine("Done.");
