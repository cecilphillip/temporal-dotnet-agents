// OpenTelemetry sample — demonstrates how to configure distributed tracing for
// Temporalio.Extensions.AI, showing the full span hierarchy produced by a
// durable chat session.
//
// This sample also demonstrates the canonical plugin-based registration path
// using DurableAIPlugin (an ITemporalWorkerPlugin) composed with the SDK's
// TracingInterceptor. Both AddDurableAI() and AddWorkerPlugin(new DurableAIPlugin())
// produce equivalent runtime behavior; the plugin form is the recommended
// shape for users composing with other Temporal partner plugins. The plugin
// surface is gated by [Experimental("TAI001")] — the file-level #pragma below
// opts in.
//
// Span hierarchy for a single chat turn
// ──────────────────────────────────────
//   durable_chat.send                      ← library span (DurableChatSessionClient)
//     UpdateWorkflow:Chat                  ← Temporal SDK span (client side)
//       RunActivity:GetResponse            ← Temporal SDK span (worker side)
//         durable_chat.turn                ← library span (DurableChatActivities)
//
// When durable tools are involved an additional level appears:
//   durable_chat.turn
//     durable_function.invoke              ← library span (DurableFunctionActivities)
//
// ActivitySource names to register
// ─────────────────────────────────
//   "Temporalio.Extensions.AI"    — DurableChatTelemetry.ActivitySourceName
//       Spans:  durable_chat.send, durable_chat.turn, durable_function.invoke
//   "Temporalio.Client"           — TracingInterceptor.ClientSource.Name
//       Spans:  UpdateWorkflow:*, QueryWorkflow:*, StartWorkflow:*
//   "Temporalio.Workflows"        — TracingInterceptor.WorkflowsSource.Name
//       Spans:  workflow execution spans (replay-safe, only emit on first run)
//   "Temporalio.Activities"       — TracingInterceptor.ActivitiesSource.Name
//       Spans:  RunActivity:*
//
// Span attributes set by the library
// ────────────────────────────────────
//   durable_chat.send
//     conversation.id       — the session/conversation identifier passed to ChatAsync
//   durable_chat.turn
//     conversation.id       — same session identifier, propagated into the activity
//     gen_ai.request.model  — model ID from the ChatOptions
//     gen_ai.response.model — model ID returned by the LLM in the response
//     gen_ai.usage.input_tokens  — prompt token count
//     gen_ai.usage.output_tokens — completion token count
//   durable_function.invoke
//     gen_ai.tool.name      — the AIFunction name being invoked
//     gen_ai.tool.call_id   — the tool call ID from the LLM response
//
// Prerequisites
// ─────────────
// • A local Temporal server:  temporal server start-dev
//   (The dev server starts on localhost:7233 with the "default" namespace.)
// • OPENAI_API_KEY: dotnet user-secrets set "OPENAI_API_KEY" "sk-..." --project samples/MEAI/OpenTelemetry
//
// Run:  dotnet run --project samples/MEAI/OpenTelemetry/OpenTelemetry.csproj

#pragma warning disable TAI001 // Opt in to the experimental plugin surface (DurableAIPlugin, AddWorkerPlugin)

using System.ClientModel;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenAI;
using OpenTelemetry.Trace;
using Temporalio.Client;
using Temporalio.Extensions.AI;
using Temporalio.Extensions.Hosting;
using Temporalio.Extensions.OpenTelemetry;

// ── Setup: Build the application host ────────────────────────────────────────
var builder = Host.CreateApplicationBuilder(args);
builder.Logging.SetMinimumLevel(LogLevel.Warning);

var apiKey = builder.Configuration.GetValue<string>("OPENAI_API_KEY");
var apiBaseUrl = builder.Configuration.GetValue<string>("OPENAI_API_BASE_URL");
var model = builder.Configuration.GetValue<string>("OPENAI_MODEL") ?? "gpt-4o-mini";
var temporalAddress = builder.Configuration.GetValue<string>("TEMPORAL_ADDRESS") ?? "localhost:7233";

if (string.IsNullOrEmpty(apiBaseUrl))
    throw new InvalidOperationException("OPENAI_API_BASE_URL is not configured in appsettings.json.");
if (string.IsNullOrEmpty(apiKey))
    throw new InvalidOperationException("OPENAI_API_KEY is not configured. Set it with: dotnet user-secrets set \"OPENAI_API_KEY\" \"sk-...\" --project samples/MEAI/OpenTelemetry");

// ── Setup: Register OpenTelemetry ─────────────────────────────────────────────
// Four ActivitySource names must be registered:
//
//   DurableChatTelemetry.ActivitySourceName  ("Temporalio.Extensions.AI")
//     — emits durable_chat.send (client), durable_chat.turn (worker),
//       and durable_function.invoke (per-tool activity)
//
//   TracingInterceptor.ClientSource.Name     ("Temporalio.Client")
//     — emits spans for workflow updates, queries, and starts issued from
//       the client side (e.g. UpdateWorkflow:Chat)
//
//   TracingInterceptor.WorkflowsSource.Name  ("Temporalio.Workflows")
//     — emits spans during workflow execution; these are suppressed on
//       replay so they only appear on the first execution
//
//   TracingInterceptor.ActivitiesSource.Name ("Temporalio.Activities")
//     — emits spans for every activity invocation (e.g. RunActivity:GetResponse)
//
// The console exporter prints each span to stdout so you can see the full
// hierarchy without running a collector. In production, replace or supplement
// AddConsoleExporter() with AddOtlpExporter().
builder.Services
    .AddOpenTelemetry()
    .WithTracing(tracing => tracing
        .AddSource(DurableChatTelemetry.ActivitySourceName)
        .AddSource(TracingInterceptor.ClientSource.Name)
        .AddSource(TracingInterceptor.WorkflowsSource.Name)
        .AddSource(TracingInterceptor.ActivitiesSource.Name)
        .AddConsoleExporter());

// ── Setup: Connect Temporal client with TracingInterceptor + DurableAIDataConverter
//
// TWO things are configured here and both are required:
//
//   TracingInterceptor  — propagates the W3C trace context (traceparent header)
//   from the client into the workflow and from the workflow into each activity.
//   Without it, Temporal's internal gRPC calls break the distributed trace and
//   the spans from the library appear disconnected in your backend.
//
//   DurableAIDataConverter.Instance  — wraps Temporal's payload converter with
//   AIJsonUtilities.DefaultOptions, which preserves the $type discriminator that
//   MEAI uses for polymorphic AIContent subclasses (TextContent, FunctionCallContent,
//   etc.). Without it, type information is silently lost when types round-trip
//   through workflow history, causing deserialization errors on replay.
var temporalClient = await TemporalClient.ConnectAsync(new TemporalClientConnectOptions(temporalAddress)
{
    DataConverter = DurableAIDataConverter.Instance,
    Interceptors = [new TracingInterceptor()],
    Namespace = "default",
});
builder.Services.AddSingleton<ITemporalClient>(temporalClient);

// ── Setup: Register IChatClient ───────────────────────────────────────────────
// AddChatClient is the idiomatic MEAI DI pattern — it returns a ChatClientBuilder
// for chaining middleware, then Build() registers the final IChatClient singleton.
// DurableChatActivities constructor-injects the unkeyed IChatClient on the worker
// side; this is the client it calls when executing the durable_chat.turn activity.
IChatClient openAiChatClient = (IChatClient)new OpenAIClient(
    new ApiKeyCredential(apiKey),
    new OpenAIClientOptions { Endpoint = new Uri(apiBaseUrl) }
).GetChatClient(model);

builder.Services
    .AddChatClient(openAiChatClient)
    .UseFunctionInvocation()   // handles tool call loops inside the activity
    .Build();

// ── Setup: Register worker + durable AI via the plugin path ─────────────────
// AddWorkerPlugin(DurableAIPlugin) is the canonical Temporal Partner pattern
// for AI integrations. It registers DurableChatWorkflow, DurableChatActivities,
// DurableFunctionActivities, DurableEmbeddingActivities, the function registry,
// DurableChatSessionClient, the DurableExecutionOptions singleton, and queues
// DurableAIPlugin in the worker plugin chain — equivalent to AddDurableAI().
//
// Composing with other plugins/interceptors:
//   • TracingInterceptor was added to the client's Interceptors list above —
//     that handles W3C trace context propagation across the gRPC boundary.
//   • DurableAIPlugin handles the AI-specific worker-side wiring.
//   • Other partner plugins (Braintrust, Pydantic, etc.) can be added with
//     additional .AddWorkerPlugin(...) calls without conflict — each has a
//     unique Name so the client-plugin dedupe path leaves them alone.
//
// AddDurableAI() remains supported and is equivalent for users who prefer
// the lowest-friction DI-extension shape:
//
//     .AddHostedTemporalWorker(temporalAddress, "default", "durable-chat-otel")
//     .AddDurableAI(opts => { /* ... */ });
builder.Services
    .AddHostedTemporalWorker(temporalAddress, "default", "durable-chat-otel")
    .AddWorkerPlugin(new DurableAIPlugin(opts =>
    {
        opts.ActivityTimeout = TimeSpan.FromMinutes(5);
        opts.SessionTimeToLive = TimeSpan.FromHours(1);
    }));

// ── Start ─────────────────────────────────────────────────────────────────────
var host = builder.Build();
await host.StartAsync();

Console.WriteLine("Worker started. OpenTelemetry console exporter is active.\n");

var sessionClient = host.Services.GetRequiredService<DurableChatSessionClient>();

// ── Run multi-turn conversation ───────────────────────────────────────────────
await RunMultiTurnDemoAsync(sessionClient);

// ── Shutdown ──────────────────────────────────────────────────────────────────
try { await host.StopAsync(); } catch (OperationCanceledException) { }
Console.WriteLine("Done.");

// ═════════════════════════════════════════════════════════════════════════════
// Multi-turn conversation demo
//
// This demo issues two chat turns in a single conversation. Look at the console
// exporter output above (or below) this block for the span hierarchy. Each call
// to ChatAsync produces:
//
//   durable_chat.send (conversation.id = <id>)
//     UpdateWorkflow:Chat
//       RunActivity:GetResponse
//         durable_chat.turn (conversation.id, gen_ai.usage.*)
//
// The conversation.id attribute is the same on both the send and turn spans,
// making it easy to filter all traces for a single session in your backend.
// ═════════════════════════════════════════════════════════════════════════════
static async Task RunMultiTurnDemoAsync(DurableChatSessionClient sessionClient)
{
    Console.WriteLine("════════════════════════════════════════════════════════");
    Console.WriteLine(" Multi-Turn Conversation with OpenTelemetry Tracing");
    Console.WriteLine("════════════════════════════════════════════════════════");

    // Each conversation maps to a Temporal workflow. Reusing the same ID across
    // ChatAsync calls routes all turns to the same workflow instance and keeps
    // the conversation.id attribute consistent across all related spans.
    var conversationId = $"otel-demo-{Guid.NewGuid():N}";
    Console.WriteLine($" Conversation ID: {conversationId}");
    Console.WriteLine($" (Search for this ID in the span output below)\n");

    var q1 = "What is the capital of France?";
    Console.WriteLine($" User : {q1}");
    var r1 = await sessionClient.ChatAsync(conversationId, [new ChatMessage(ChatRole.User, q1)]);
    Console.WriteLine($" Agent: {r1.Text}\n");

    // The workflow's history already contains the previous exchange, so the
    // model can answer this pronoun reference without being told explicitly.
    var q2 = "What is the population of that city?";
    Console.WriteLine($" User : {q2}");
    var r2 = await sessionClient.ChatAsync(conversationId, [new ChatMessage(ChatRole.User, q2)]);
    Console.WriteLine($" Agent: {r2.Text}");

    Console.WriteLine("════════════════════════════════════════════════════════");
    Console.WriteLine();
    Console.WriteLine(" Check the console exporter output for the span hierarchy:");
    Console.WriteLine("   durable_chat.send");
    Console.WriteLine("     UpdateWorkflow:Chat");
    Console.WriteLine("       RunActivity:GetResponse");
    Console.WriteLine("         durable_chat.turn");
    Console.WriteLine();
    Console.WriteLine($" Filter by tag conversation.id = {conversationId}");
    Console.WriteLine("════════════════════════════════════════════════════════\n");
}
