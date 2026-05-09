// ConfigurableAgent — two-tier customer support demo using DI factories on the
// AddDurableAgent builder.
//
// Demonstrates the v0.3 DI-factory-per-slot pattern: every tool resolves its
// backing service via sp.GetRequiredService<T>() at first activity dispatch.
// No BuildServiceProvider() bootstrap is needed — the library invokes each
// factory once with the worker's runtime IServiceProvider.
//
// Demo story: a TriageAgent handles incoming customer messages. If it cannot
// resolve the case, it appends [ESCALATE: summary] to its response. The
// SupportWorkflow detects the marker, extracts the summary, and hands off to
// the EscalationAgent — agent-to-agent communication orchestrated by the workflow.
//
// Run:  dotnet run --project samples/MAF/ConfigurableAgent/ConfigurableAgent.csproj

using System.ClientModel;
using System.Collections.Frozen;
using System.ComponentModel;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenAI;
using Temporalio.Client;
using Temporalio.Extensions.Agents;
using Temporalio.Extensions.Hosting;
using Temporalio.Workflows;
using static Temporalio.Extensions.Agents.TemporalWorkflowExtensions;

// ── Step 1: Build the application host ───────────────────────────────────────
var builder = Host.CreateApplicationBuilder(args);
builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.Logging.AddFilter("SupportWorkflow", LogLevel.Information);          // workflow routing decisions
builder.Logging.AddFilter("Temporalio.Extensions.Agents", LogLevel.Information); // agent activity dispatch

// ── Step 2: Load configuration ───────────────────────────────────────────────
var apiKey = builder.Configuration.GetValue<string>("OPENAI_API_KEY");
var apiBaseUrl = builder.Configuration.GetValue<string>("OPENAI_API_BASE_URL");

if (string.IsNullOrEmpty(apiKey))
    throw new InvalidOperationException(
        "OPENAI_API_KEY is not configured. Set it with: " +
        "dotnet user-secrets set \"OPENAI_API_KEY\" \"sk-...\" --project samples/MAF/ConfigurableAgent");

if (string.IsNullOrEmpty(apiBaseUrl))
    throw new InvalidOperationException("OPENAI_API_BASE_URL is not configured in appsettings.json.");

const string model = "gpt-4o-mini";
var temporalAddress = builder.Configuration.GetValue<string>("TEMPORAL_ADDRESS") ?? "localhost:7233";

var openAiClient = new OpenAIClient(
    new ApiKeyCredential(apiKey),
    new OpenAIClientOptions { Endpoint = new Uri(apiBaseUrl) });

// ── Step 3: Bind agent options from configuration ────────────────────────────
// These options drive each agent's instruction template. We bind them eagerly so
// the templated text can flow into agent.Instructions at registration time.
var triageOptions = builder.Configuration
    .GetSection("TriageAgent").Get<TriageAgentOptions>() ?? new TriageAgentOptions();
var escalationOptions = builder.Configuration
    .GetSection("EscalationAgent").Get<EscalationAgentOptions>() ?? new EscalationAgentOptions();

var triageInstructions = triageOptions.Instructions
    .Replace("{CompanyName}", triageOptions.CompanyName);
// EscalationAgentOptions has no CompanyName — the company name is intentionally
// shared and lives on TriageAgentOptions as the single source of truth.
var escalationInstructions = escalationOptions.Instructions
    .Replace("{CompanyName}", triageOptions.CompanyName);

// Make the bound options available to anything else that wants IOptions<T>.
builder.Services.Configure<TriageAgentOptions>(builder.Configuration.GetSection("TriageAgent"));
builder.Services.Configure<EscalationAgentOptions>(builder.Configuration.GetSection("EscalationAgent"));

// ── Step 4: Register DI services ─────────────────────────────────────────────
// Tool services live in DI and are resolved by the AddTool factory at first dispatch.
builder.Services.AddSingleton<OrderService>();
builder.Services.AddSingleton<EscalationPolicyService>();
builder.Services.AddChatClient(openAiClient.GetChatClient(model).AsIChatClient());

// ── Step 5: Register the durable agents ──────────────────────────────────────
// Both agents pull their tool services from DI via the AddTool factory. The
// factory runs once per worker at first activity dispatch and the result is
// cached for the worker's lifetime — equivalent to the v0.2 AddAIAgentFactory
// path, but with one DI hop per slot rather than one factory per agent.
builder.Services.AddTemporalClient(temporalAddress, "default");

builder.Services
    .AddHostedTemporalWorker("configurable-agent")
    .AddTemporalAgents(opts =>
    {
        opts.AddDurableAgent("TriageAgent", agent =>
        {
            agent.Description = "First-line support — handles order lookups and common questions.";
            agent.Instructions = triageInstructions;
            agent.ChatClient = sp => sp.GetRequiredService<IChatClient>();

            agent.AddTool("LookupOrder", sp => AIFunctionFactory.Create(
                sp.GetRequiredService<OrderService>().LookupOrder,
                name: "LookupOrder",
                description: "Look up the current status of a customer order by order ID."));
        });

        opts.AddDurableAgent("EscalationAgent", agent =>
        {
            agent.Description = "Specialist for complaints, refunds, and complex cases.";
            agent.Instructions = escalationInstructions;
            agent.ChatClient = sp => sp.GetRequiredService<IChatClient>();

            agent.AddTool("GetReturnPolicy", sp => AIFunctionFactory.Create(
                sp.GetRequiredService<EscalationPolicyService>().GetReturnPolicy,
                name: "GetReturnPolicy",
                description: "Returns Acme Store's return and refund policy."));
        });
    })
    .AddWorkflow<SupportWorkflow>();

// ── Step 6: Start the host ───────────────────────────────────────────────────
var host = builder.Build();
await host.StartAsync();

Console.WriteLine("Worker started. Submitting support workflows...\n");

// ── Step 7: Submit workflows ─────────────────────────────────────────────────
var client = host.Services.GetRequiredService<ITemporalClient>();

// Case 1: Simple order lookup — TriageAgent resolves it directly, no escalation.
var simpleHandle = await client.StartWorkflowAsync(
    (SupportWorkflow wf) => wf.RunAsync("Where is my order ORD-001?"),
    new WorkflowOptions
    {
        Id = $"support-simple-{Guid.NewGuid()}",
        TaskQueue = "configurable-agent"
    });

// Case 2: Complaint + refund request — TriageAgent escalates to EscalationAgent.
var complexHandle = await client.StartWorkflowAsync(
    (SupportWorkflow wf) => wf.RunAsync(
        "My order ORD-004 has been delayed for two weeks. I want a refund."),
    new WorkflowOptions
    {
        Id = $"support-complex-{Guid.NewGuid()}",
        TaskQueue = "configurable-agent"
    });

// ── Step 8: Await and display results ────────────────────────────────────────
Console.WriteLine("─── Case 1: Simple order lookup ─────────────────────────────");
Console.WriteLine("User: Where is my order ORD-001?");
try
{
    var result = await simpleHandle.GetResultAsync();
    Console.WriteLine($"Agent: {result}\n");
}
catch (Exception ex)
{
    Console.WriteLine($"Workflow failed: {ex.Message}\n");
}

Console.WriteLine("─── Case 2: Delayed order + refund request ──────────────────");
Console.WriteLine("User: My order ORD-004 has been delayed for two weeks. I want a refund.");
try
{
    var result = await complexHandle.GetResultAsync();
    Console.WriteLine($"Agent: {result}\n");
}
catch (Exception ex)
{
    Console.WriteLine($"Workflow failed: {ex.Message}\n");
}

// ── Step 9: Graceful shutdown ────────────────────────────────────────────────
try { await host.StopAsync(); } catch (OperationCanceledException) { }
Console.WriteLine("Done.");

// ─────────────────────────────────────────────────────────────────────────────
// SUPPORT WORKFLOW
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Two-tier customer support workflow.
/// <para>
/// TriageAgent handles the first pass. If it appends <c>[ESCALATE: summary]</c>
/// the workflow extracts the summary and forwards both the customer message and
/// the triage summary to EscalationAgent. Both agent invocations are durable
/// Temporal activities — a crash between the two calls replays from cached history
/// and the first agent is never re-executed.
/// </para>
/// </summary>
[Workflow("ConfigurableAgent.SupportWorkflow")]
public class SupportWorkflow
{
    [WorkflowRun]
    public async Task<string> RunAsync(string customerMessage)
    {
        // ── Triage ────────────────────────────────────────────────────────────
        Workflow.Logger.LogInformation(
            "[TriageAgent] Handling: \"{Message}\"", customerMessage);

        var triage = GetAgent("TriageAgent");
        var triageSession = await triage.CreateSessionAsync().ConfigureAwait(true);
        var triageResponse = await triage.RunAsync(
            [new ChatMessage(ChatRole.User, customerMessage)], triageSession).ConfigureAwait(true);

        var responseText = triageResponse.Text ?? string.Empty;

        // ── Escalation detection ──────────────────────────────────────────────
        const string escalationMarker = "[ESCALATE:";
        var markerIndex = responseText.IndexOf(escalationMarker, StringComparison.OrdinalIgnoreCase);

        if (markerIndex < 0)
        {
            // Resolved by triage — no escalation needed.
            // Note: if the LLM was expected to escalate but didn't include the [ESCALATE: ...]
            // marker, this branch silently returns the triage response. For production use,
            // prefer structured output (ChatResponseFormat.ForJson) over free-text markers.
            Workflow.Logger.LogInformation(
                "[TriageAgent] Resolved directly — no escalation.");
            return responseText.Trim();
        }

        // Extract the one-sentence summary that TriageAgent embedded in the marker.
        var summaryStart = markerIndex + escalationMarker.Length;
        var summaryEnd = responseText.IndexOf(']', summaryStart);
        var caseSummary = summaryEnd > summaryStart
            ? responseText[summaryStart..summaryEnd].Trim()
            : "(triage summary unavailable)";

        Workflow.Logger.LogInformation(
            "[TriageAgent] Escalating → EscalationAgent. Summary: \"{Summary}\"", caseSummary);

        // ── Handoff to EscalationAgent ────────────────────────────────────────
        Workflow.Logger.LogInformation(
            "[EscalationAgent] Taking over with triage context.");

        var escalation = GetAgent("EscalationAgent");
        var escalationSession = await escalation.CreateSessionAsync().ConfigureAwait(true);

        var handoffMessage =
            $"[Triage summary: {caseSummary}]\n\nCustomer's original message: {customerMessage}";

        var escalationResponse = await escalation.RunAsync(
            [new ChatMessage(ChatRole.User, handoffMessage)], escalationSession).ConfigureAwait(true);

        Workflow.Logger.LogInformation("[EscalationAgent] Responded.");

        return escalationResponse.Text ?? string.Empty;
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// CONFIGURATION POCOs
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>Bound from the <c>TriageAgent</c> section of appsettings.json.</summary>
public sealed class TriageAgentOptions
{
    public string CompanyName { get; set; } = "Acme Store";
    public string Instructions { get; set; } = string.Empty;
}

/// <summary>Bound from the <c>EscalationAgent</c> section of appsettings.json.</summary>
public sealed class EscalationAgentOptions
{
    public string Instructions { get; set; } = string.Empty;
}

// ─────────────────────────────────────────────────────────────────────────────
// DOMAIN SERVICES (registered in DI, injected via AddTool factories)
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// In-memory order store. In a real application this would query a database or
/// external API — exactly why it belongs in DI rather than being constructed inline.
/// </summary>
public sealed class OrderService
{
    private static readonly FrozenDictionary<string, string> Orders =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["ORD-001"] = "Shipped — estimated delivery in 2 days",
            ["ORD-002"] = "Delivered on April 28",
            ["ORD-003"] = "Processing — not yet shipped",
            ["ORD-004"] = "Delayed — carrier exception reported",
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    [Description("Look up the current status of a customer order by order ID.")]
    public string LookupOrder(string orderId) =>
        Orders.TryGetValue(orderId, out var status)
            ? status
            : $"Order '{orderId}' not found.";
}

/// <summary>
/// Provides Acme Store's return and refund policy text. Registered as a singleton
/// so policy updates can be reloaded without restarting the worker.
/// </summary>
public sealed class EscalationPolicyService
{
    public string GetReturnPolicy() =>
        "Acme Store accepts returns within 30 days of delivery for unused items in " +
        "original packaging. Refunds are processed within 5–7 business days. " +
        "Damaged or delayed orders qualify for immediate replacement or full refund.";
}
