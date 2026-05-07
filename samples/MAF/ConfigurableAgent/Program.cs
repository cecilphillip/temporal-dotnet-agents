// ConfigurableAgent — two-tier customer support demo using AddAIAgentFactory.
//
// Demonstrates AddAIAgentFactory: both agents need DI-registered services
// (IOptions<T>, OrderService, EscalationPolicyService) that are only available
// after Host.Build(). AddAIAgent would require pre-constructing the agent before DI
// is built, which is impossible without bypassing the container. The factory overload
// receives the fully-built IServiceProvider at first activity dispatch.
//
// Demo story: a TriageAgent handles incoming customer messages. If it cannot resolve
// the case, it signals escalation by appending [ESCALATE: summary] to its response.
// The SupportWorkflow detects the marker, extracts the summary, and hands off to
// the EscalationAgent — agent-to-agent communication orchestrated by the workflow.
//
// Run:  dotnet run --project samples/MAF/ConfigurableAgent/ConfigurableAgent.csproj

using System.ClientModel;
using System.ComponentModel;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAI;
using OpenAI.Chat;
using Temporalio.Client;
// Alias to resolve ChatMessage ambiguity between Microsoft.Extensions.AI and OpenAI.Chat
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;
using Temporalio.Extensions.Agents;
using Temporalio.Extensions.Hosting;
using Temporalio.Workflows;
using static Temporalio.Extensions.Agents.TemporalWorkflowExtensions;

// ── Step 1: Build the application host ───────────────────────────────────────
var builder = Host.CreateApplicationBuilder(args);
// Keep the SDK's own infrastructure quiet; show Information only for things we care about.
builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.Logging.AddFilter("SupportWorkflow", LogLevel.Information);          // workflow routing decisions
builder.Logging.AddFilter("Temporalio.Extensions.Agents", LogLevel.Information); // agent activity dispatch

// ── Step 2: Load configuration ────────────────────────────────────────────────
var apiKey = builder.Configuration.GetValue<string>("OPENAI_API_KEY");
var apiBaseUrl = builder.Configuration.GetValue<string>("OPENAI_API_BASE_URL");

if (string.IsNullOrEmpty(apiBaseUrl))
    throw new InvalidOperationException("OPENAI_API_BASE_URL is not configured in appsettings.json.");

if (string.IsNullOrEmpty(apiKey))
    throw new InvalidOperationException(
        "OPENAI_API_KEY is not configured. Set it with: " +
        "dotnet user-secrets set \"OPENAI_API_KEY\" \"sk-...\" --project samples/MAF/ConfigurableAgent");

var endpoint = new Uri(apiBaseUrl);
var openAiOptions = new OpenAIClientOptions() { Endpoint = endpoint };
var model = "gpt-4o-mini";
var temporalAddress = builder.Configuration.GetValue<string>("TEMPORAL_ADDRESS") ?? "localhost:7233";

ApiKeyCredential credential = new(apiKey);
OpenAIClient openAiClient = new(credential, openAiOptions);

// ── Step 3: Register DI services ─────────────────────────────────────────────
// IOptions<T> bindings for agent configuration — resolved inside each factory.
builder.Services.Configure<TriageAgentOptions>(builder.Configuration.GetSection("TriageAgent"));
builder.Services.Configure<EscalationAgentOptions>(builder.Configuration.GetSection("EscalationAgent"));

// Singleton services that carry domain state used as agent tools.
builder.Services.AddSingleton<OrderService>();
builder.Services.AddSingleton<EscalationPolicyService>();

// ── Step 4: Register Temporal Client and Worker ───────────────────────────────
builder.Services.AddTemporalClient(temporalAddress, "default");

builder.Services
    .AddHostedTemporalWorker("configurable-agent")
    .AddTemporalAgents(opts =>
    {
        // AddAIAgentFactory is required for both agents: IOptions<T> and the tool
        // services live in DI and are not available until after Host.Build().
        // AddAIAgent would require having the constructed agent in hand here — which
        // would mean manually newing up services (defeating DI) or calling Build()
        // early (antipattern). The factory receives the fully-built IServiceProvider
        // at first activity dispatch, so everything resolves cleanly.

        // Phase 5 placeholder — Phase 7 rewrites samples to the v0.3 idiomatic shape.
        opts.AddDurableAgent("TriageAgent", a =>
        {
            a.Description = "First-line support — handles order lookups and common questions.";
            a.ChatClient = _ => openAiClient.GetChatClient(model).AsIChatClient();
            a.AddTool("LookupOrder", sp =>
            {
                var orderService = sp.GetRequiredService<OrderService>();
                return AIFunctionFactory.Create(
                    orderService.LookupOrder,
                    "LookupOrder",
                    "Look up the current status of a customer order by order ID.");
            });
        });

        opts.AddDurableAgent("EscalationAgent", a =>
        {
            a.Description = "Specialist for complaints, refunds, and complex cases.";
            a.ChatClient = _ => openAiClient.GetChatClient(model).AsIChatClient();
            a.AddTool("GetReturnPolicy", sp =>
            {
                var policyService = sp.GetRequiredService<EscalationPolicyService>();
                return AIFunctionFactory.Create(
                    policyService.GetReturnPolicy,
                    "GetReturnPolicy",
                    "Returns Acme Store's return and refund policy.");
            });
        });
    })
    .AddWorkflow<SupportWorkflow>();

// ── Step 5: Start the host (worker runs as IHostedService) ────────────────────
var host = builder.Build();
await host.StartAsync();

Console.WriteLine("Worker started. Submitting support workflows...\n");

// ── Step 6: Submit workflows ──────────────────────────────────────────────────
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

// ── Step 7: Await and display results ────────────────────────────────────────
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

// ── Step 8: Graceful shutdown ─────────────────────────────────────────────────
try { await host.StopAsync(); } catch (OperationCanceledException) { }
Console.WriteLine("Done.");

// ─────────────────────────────────────────────────────────────────────────────
// SUPPORT WORKFLOW
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Two-tier customer support workflow.
/// <para>
/// TriageAgent handles the first pass. If it appends <c>[ESCALATE: summary]</c>
/// the workflow extracts the summary and forwards both the customer message and the
/// triage summary to EscalationAgent. Both agent invocations are durable Temporal
/// activities — a crash between the two calls replays from cached history and the
/// first agent is never re-executed.
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
        var triageSession = await triage.CreateSessionAsync();
        var triageResponse = await triage.RunAsync(
            [new ChatMessage(ChatRole.User, customerMessage)], triageSession);

        var responseText = triageResponse.Text ?? string.Empty;

        // ── Escalation detection ──────────────────────────────────────────────
        const string escalationMarker = "[ESCALATE:";
        var markerIndex = responseText.IndexOf(escalationMarker, StringComparison.OrdinalIgnoreCase);

        if (markerIndex < 0)
        {
            // Resolved by triage — no escalation needed.
            Workflow.Logger.LogInformation(
                "[TriageAgent] Resolved directly — no escalation.");
            return responseText.Trim();
        }

        // Extract the one-sentence summary that TriageAgent embedded in the marker.
        var summaryStart = markerIndex + escalationMarker.Length;
        var summaryEnd = responseText.IndexOf(']', summaryStart);
        var caseSummary = summaryEnd > summaryStart
            ? responseText[summaryStart..summaryEnd].Trim()
            : "escalated case";

        Workflow.Logger.LogInformation(
            "[TriageAgent] Escalating → EscalationAgent. Summary: \"{Summary}\"", caseSummary);

        // ── Handoff to EscalationAgent ────────────────────────────────────────
        // Pass triage's summary alongside the original message so EscalationAgent
        // has full context without re-reading the conversation history.
        Workflow.Logger.LogInformation(
            "[EscalationAgent] Taking over with triage context.");

        var escalation = GetAgent("EscalationAgent");
        var escalationSession = await escalation.CreateSessionAsync();

        var handoffMessage =
            $"[Triage summary: {caseSummary}]\n\nCustomer's original message: {customerMessage}";

        var escalationResponse = await escalation.RunAsync(
            [new ChatMessage(ChatRole.User, handoffMessage)], escalationSession);

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
// DOMAIN SERVICES (registered in DI, injected via factory)
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// In-memory order store. In a real application this would query a database or
/// external API — exactly why it belongs in DI rather than being constructed inline.
/// </summary>
public sealed class OrderService
{
    private static readonly Dictionary<string, string> Orders = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ORD-001"] = "Shipped — estimated delivery in 2 days",
        ["ORD-002"] = "Delivered on April 28",
        ["ORD-003"] = "Processing — not yet shipped",
        ["ORD-004"] = "Delayed — carrier exception reported",
    };

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
