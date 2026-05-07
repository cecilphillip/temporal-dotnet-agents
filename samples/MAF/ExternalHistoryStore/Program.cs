// ExternalHistoryStore — durable agent session demonstrating BOTH layers of the
// MAF + Temporal Agents architecture, plus a recent-N reduction strategy.
//
//   Layer 1  IAgentHistoryStore        (workflow-level: PII out of Temporal events)
//   Layer 2  AIContextProvider         (MAF-level: per-turn tenant metadata injection)
//   Reduction strategy lives inside the store's LoadAsync — the documented workaround
//   for the in-process HistoryReducer not applying to external storage.
//
// Run:  temporal server start-dev   (one terminal)
//       dotnet run --project samples/MAF/ExternalHistoryStore/ExternalHistoryStore.csproj

using System.ClientModel;
using System.Text;
using ExternalHistoryStore;
using Microsoft.Agents.AI;
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

// ── Step 2: Load configuration ────────────────────────────────────────────────
var apiKey = builder.Configuration.GetValue<string>("OPENAI_API_KEY");
var apiBaseUrl = builder.Configuration.GetValue<string>("OPENAI_API_BASE_URL");

if (string.IsNullOrEmpty(apiBaseUrl))
    throw new InvalidOperationException("OPENAI_API_BASE_URL is not configured in appsettings.json.");

if (string.IsNullOrEmpty(apiKey))
    throw new InvalidOperationException(
        "OPENAI_API_KEY is not configured. Set it with: " +
        "dotnet user-secrets set \"OPENAI_API_KEY\" \"sk-...\" --project samples/MAF/ExternalHistoryStore");

var endpoint = new Uri(apiBaseUrl);
var openAiOptions = new OpenAIClientOptions { Endpoint = endpoint };
const string model = "gpt-4o-mini";
var temporalAddress = builder.Configuration.GetValue<string>("TEMPORAL_ADDRESS") ?? "localhost:7233";

var credential = new ApiKeyCredential(apiKey);
var openAiClient = new OpenAIClient(credential, openAiOptions);

// ── Step 3: Register tenant directory + Layer 2 provider singleton ────────────
// The provider is a singleton so the demo driver can read its InvokingCalls counter
// after the conversation. The Layer 1 store is registered separately by
// UseExternalAgentHistory<TStore>() in step 5.
builder.Services.AddSingleton(sp => TenantDirectory.LoadFromConfig(builder.Configuration));
builder.Services.AddSingleton<TenantContextProvider>();

// Pre-build the singleton store ourselves so we can read its statistics later.
// We register both the concrete type (so the demo can read the counters) and the
// IAgentHistoryStore service (which the activity resolves at runtime).
var store = new InMemoryHistoryStore(maxRecentEntries: 4);
builder.Services.AddSingleton(store);
builder.Services.AddSingleton<Temporalio.Extensions.Agents.HistoryStore.IAgentHistoryStore>(store);

// ── Step 4: Register Temporal client ──────────────────────────────────────────
builder.Services.AddTemporalClient(temporalAddress, "default");

// ── Step 5: Wire BOTH layers on the worker ────────────────────────────────────
const string taskQueue = "external-history-store";
builder.Services
    .AddHostedTemporalWorker(taskQueue)
    .AddTemporalAgents(opts =>
    {
        // Phase 5 placeholder — Phase 7 rewrites the sample to v0.3 idiomatic shape.
        opts.HistoryStore = sp => sp.GetRequiredService<Temporalio.Extensions.Agents.HistoryStore.IAgentHistoryStore>();

        opts.AddDurableAgent("SupportAgent", a =>
        {
            a.ChatClient = _ => openAiClient.GetChatClient(model).AsIChatClient();
            a.Instructions =
                "You are a multi-tenant customer support agent. Use the tenant " +
                "metadata supplied in system context to tailor responses (mention " +
                "the tenant tier or SLA when relevant). Treat order IDs of the form " +
                "ORD-XXX as plausibly real and answer with concise made-up status text " +
                "(this is a demo). If a question references information you don't see " +
                "in the current messages, say you don't have visibility into that " +
                "earlier part of the conversation.";
            a.AddContextProvider(sp => sp.GetRequiredService<TenantContextProvider>());
        });
    })
    .AddWorkflow<SupportSessionWorkflow>();

// ── Step 6: Start the host ───────────────────────────────────────────────────
var host = builder.Build();
await host.StartAsync();

Console.WriteLine("Worker started.");
Console.WriteLine();

var directory = host.Services.GetRequiredService<TenantDirectory>();
var tenantProvider = host.Services.GetRequiredService<TenantContextProvider>();
var temporalClient = host.Services.GetRequiredService<ITemporalClient>();

var activeTenant = directory.TryGet("acme")
    ?? throw new InvalidOperationException("Acme tenant missing from configuration.");

Console.WriteLine($"=== Tenant: {activeTenant.Name} ({activeTenant.Tier} tier) " +
                  $"— reduction window: 4 entries ===");
Console.WriteLine();

// ── Step 7: Drive 6 turns through the workflow ───────────────────────────────
var workflowId = $"support-acme-{Guid.NewGuid():N}";
var handle = await temporalClient.StartWorkflowAsync(
    (SupportSessionWorkflow wf) => wf.RunAsync(),
    new WorkflowOptions { Id = workflowId, TaskQueue = taskQueue });

string[] questions =
[
    "What's the status of order ORD-001?",
    "And ORD-002?",
    "What about ORD-003?",
    "Which one was delivered?",
    "Tell me about ORD-004.",
    "What was my very first question?",
];

for (int i = 0; i < questions.Length; i++)
{
    var q = questions[i];
    Console.WriteLine($"Turn {i + 1}: \"{q}\"");

    var answer = await handle.ExecuteUpdateAsync(
        wf => wf.AskAsync(new AskInput(q, activeTenant.Id)));

    Console.WriteLine($"Agent: {answer}");
    Console.WriteLine();
}

// Signal the workflow to wrap up so it doesn't keep running forever.
await handle.SignalAsync(wf => wf.ShutdownAsync());

// ── Step 8: Print verification output ────────────────────────────────────────
var fullHistory = store.SnapshotFull(workflowId);
Console.WriteLine($"=== Full History (audit trail via SnapshotFull) ===");
Console.WriteLine($"Session '{workflowId}': {fullHistory.Count} entries " +
                  $"({fullHistory.Count / 2} request + {fullHistory.Count / 2} response)");
Console.WriteLine();

Console.WriteLine($"=== Reduction Statistics ===");
Console.WriteLine(
    $"[Reduction] LoadAsync called {store.LoadCalls} times. " +
    $"Window applied {store.ReductionEvents} times " +
    $"(turns where full history > 4 entries triggered the recent-N truncation).");
Console.WriteLine();

// Inspect the most recent ExecuteAgent activity payload from the workflow's history.
// Confirms turn-1's question is NOT carried in the late-turn ActivityScheduled event.
Console.WriteLine($"=== Temporal Activity Payload Inspection ===");
string? lastExecuteAgentPayload = null;
await foreach (var ev in handle.FetchHistoryEventsAsync())
{
    if (ev.ActivityTaskScheduledEventAttributes is { } attrs &&
        attrs.ActivityType.Name == "Temporalio.Extensions.Agents.ExecuteAgent" &&
        attrs.Input?.Payloads_.Count >= 1)
    {
        lastExecuteAgentPayload = Encoding.UTF8.GetString(
            attrs.Input.Payloads_[0].Data.ToByteArray());
    }
}

if (lastExecuteAgentPayload is not null)
{
    var hasConvHistoryKey = lastExecuteAgentPayload.Contains("ConversationHistory", StringComparison.OrdinalIgnoreCase);
    var hasTurn1Question = lastExecuteAgentPayload.Contains("ORD-001", StringComparison.Ordinal);
    Console.WriteLine($"  Last ExecuteAgent input contains 'ConversationHistory' key: {hasConvHistoryKey}");
    Console.WriteLine($"  Last ExecuteAgent input contains turn-1 marker (ORD-001):    {hasTurn1Question}");
    Console.WriteLine(hasConvHistoryKey
        ? "  ⚠ Unexpected: payload still carries ConversationHistory."
        : "  ✓ Payload omits ConversationHistory — PII / O(n²) growth mitigated.");
}
else
{
    Console.WriteLine("  (no ExecuteAgent activity found in workflow history)");
}
Console.WriteLine();

Console.WriteLine($"=== Layer Cooperation ===");
Console.WriteLine($"[Layer 1]  IAgentHistoryStore.LoadAsync       called {store.LoadCalls} times");
Console.WriteLine($"[Layer 1]  IAgentHistoryStore reductions       applied {store.ReductionEvents} times");
Console.WriteLine($"[Layer 1]  IAgentHistoryStore.SnapshotFull     {fullHistory.Count} entries retained for audit");
Console.WriteLine($"[Layer 2]  TenantContextProvider.InvokingAsync called {tenantProvider.InvokingCalls} times");
Console.WriteLine();

// ── Step 9: Graceful shutdown ────────────────────────────────────────────────
try { await host.StopAsync(); } catch (OperationCanceledException) { }
Console.WriteLine("Done.");

// ─────────────────────────────────────────────────────────────────────────────
// SUPPORT WORKFLOW
// ─────────────────────────────────────────────────────────────────────────────

namespace ExternalHistoryStore
{
    using Microsoft.Extensions.AI;
    using Temporalio.Extensions.Agents;
    using Temporalio.Extensions.Agents.Session;
    using Temporalio.Workflows;
    using static Temporalio.Extensions.Agents.TemporalWorkflowExtensions;

    /// <summary>
    /// Input for the <see cref="SupportSessionWorkflow.AskAsync"/> update.
    /// </summary>
    /// <param name="Question">The user's question for this turn.</param>
    /// <param name="TenantId">
    /// The active tenant's ID. Stamped onto the outgoing
    /// <see cref="ChatMessage.AdditionalProperties"/> so <see cref="TenantContextProvider"/>
    /// can find it inside the activity and inject the matching tenant system message.
    /// </param>
    public sealed record AskInput(string Question, string TenantId);

    /// <summary>
    /// Long-lived workflow that holds a single <see cref="TemporalAIAgent"/> session
    /// and exposes <see cref="AskAsync"/> as a <c>[WorkflowUpdate]</c>. Each update is a
    /// durable, acknowledged request/response round-trip — the caller blocks until the
    /// agent responds and the result is recorded in workflow history.
    /// </summary>
    [Workflow("ExternalHistoryStore.SupportSessionWorkflow")]
    public class SupportSessionWorkflow
    {
        private TemporalAgentSession? _session;
        private bool _shutdownRequested;

        [WorkflowRun]
        public Task RunAsync()
        {
            // Wait until shutdown is signaled. Updates fire concurrently with this wait.
            return Workflow.WaitConditionAsync(() => _shutdownRequested);
        }

        [WorkflowUpdate("Ask")]
        public async Task<string> AskAsync(AskInput input)
        {
            var agent = GetAgent("SupportAgent");
            _session ??= (TemporalAgentSession)await agent.CreateSessionAsync();

            // Stamp the active tenant ID onto the user message — the
            // TenantContextProvider running in the activity reads this off
            // ChatMessage.AdditionalProperties and emits the matching system context.
            var userMessage = new ChatMessage(ChatRole.User, input.Question)
            {
                AdditionalProperties = new AdditionalPropertiesDictionary
                {
                    [TenantContextProvider.TenantIdProperty] = input.TenantId,
                },
            };

            var response = await agent.RunAsync([userMessage], _session);
            return response.Text ?? string.Empty;
        }

        [WorkflowSignal("Shutdown")]
        public Task ShutdownAsync()
        {
            _shutdownRequested = true;
            return Task.CompletedTask;
        }
    }
}
