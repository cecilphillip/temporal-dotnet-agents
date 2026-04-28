// DurableChat sample — demonstrates how to make any IChatClient durable using
// Temporalio.Extensions.AI — no Microsoft Agent Framework required.
//
// Demos
// ─────
//   1. Multi-turn conversation  — history is preserved across turns in the workflow
//   2. Tool call                — ChatOptions.Tools + UseFunctionInvocation()
//   3. History query            — GetHistoryAsync returns the full persisted log
//
// How this differs from the Agents library
// ─────────────────────────────────────────
// • No AIAgent, AgentWorkflow, or AgentActivities — pure IChatClient + MEAI.
// • DurableChatSessionClient.ChatAsync replaces TemporalAIAgentProxy.RunAsync.
// • DurableChatWorkflow (internal) manages history; you never reference it directly.
// • DurableAIDataConverter is mandatory — MEAI uses a $type discriminator for
//   AIContent polymorphism that the default Temporal JSON converter does not handle.
//
// Prerequisites
// ─────────────
// • A local Temporal server:  temporal server start-dev
//   (The dev server starts on localhost:7233 with the "default" namespace.)
// • OPENAI_API_KEY: dotnet user-secrets set "OPENAI_API_KEY" "sk-..." --project samples/MEAI/DurableChat
//
// Run:  dotnet run --project samples/DurableChat/DurableChat.csproj

using System.ClientModel;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenAI;
using Temporalio.Client;
using Temporalio.Extensions.AI;
using Temporalio.Extensions.Hosting;

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
    throw new InvalidOperationException("OPENAI_API_KEY is not configured. Set it with: dotnet user-secrets set \"OPENAI_API_KEY\" \"sk-...\" --project samples/MEAI/DurableChat");

// ── Setup: Connect Temporal client with DurableAIDataConverter ────────────────
// DurableAIDataConverter.Instance wraps Temporal's payload converter with
// AIJsonUtilities.DefaultOptions, which handles MEAI's $type discriminator for
// polymorphic AIContent subclasses (TextContent, FunctionCallContent, etc.).
// Without this, type information is lost when types round-trip through history.
var temporalClient = await TemporalClient.ConnectAsync(new TemporalClientConnectOptions(temporalAddress)
{
    DataConverter = DurableAIDataConverter.Instance,
    Namespace = "default",
});
builder.Services.AddSingleton<ITemporalClient>(temporalClient);

// ── Setup: Weather tool (used in Demo 2) ─────────────────────────────────────
static string GetCurrentWeather(string city)
    => Random.Shared.NextDouble() > 0.5
        ? $"It's sunny and 22 °C in {city}."
        : $"It's overcast and 15 °C in {city}.";

var weatherTool = AIFunctionFactory.Create(
    GetCurrentWeather,
    name: "get_current_weather",
    description: "Returns the current weather conditions for a given city.");

// ── Setup: Register IChatClient ───────────────────────────────────────────────
// AddChatClient is the idiomatic MEAI pattern — it returns a ChatClientBuilder
// for chaining middleware, then Build() registers the final IChatClient singleton.
// DurableChatActivities constructor-injects this on the worker side.
IChatClient openAiChatClient = (IChatClient)new OpenAIClient(
    new ApiKeyCredential(apiKey),
    new OpenAIClientOptions { Endpoint = new Uri(apiBaseUrl) }
).GetChatClient(model);

builder.Services
    .AddChatClient(openAiChatClient)
    .UseFunctionInvocation()   // handles tool call loops inside the activity
    .Build();

// ── Setup: Register worker + durable AI ──────────────────────────────────────
// AddDurableAI registers DurableChatWorkflow, DurableChatActivities, and
// DurableChatSessionClient on the worker. The session client is resolved from
// DI after the host starts.
builder.Services
    .AddHostedTemporalWorker(temporalAddress, "default", "durable-chat")
    .AddDurableAI(opts =>
    {
        opts.ActivityTimeout = TimeSpan.FromMinutes(5);
        opts.SessionTimeToLive = TimeSpan.FromHours(1);
    });

// ── Start ─────────────────────────────────────────────────────────────────────
var host = builder.Build();
await host.StartAsync();

Console.WriteLine("Worker started.\n");

var sessionClient = host.Services.GetRequiredService<DurableChatSessionClient>();

// ── Run demos ─────────────────────────────────────────────────────────────────
await RunMultiTurnDemoAsync(sessionClient);
await RunToolCallDemoAsync(sessionClient, weatherTool);
await RunHistoryQueryDemoAsync(sessionClient);

// ── Shutdown ──────────────────────────────────────────────────────────────────
try { await host.StopAsync(); } catch (OperationCanceledException) { }
Console.WriteLine("Done.");

// ═════════════════════════════════════════════════════════════════════════════
// Demo 1: Multi-turn conversation
//
// Shows that conversation history is preserved across turns in the Temporal
// workflow. The second question ("that city") is only answerable because the
// workflow held onto the first turn's context.
// ═════════════════════════════════════════════════════════════════════════════
static async Task RunMultiTurnDemoAsync(DurableChatSessionClient sessionClient)
{
    Console.WriteLine("════════════════════════════════════════════════════════");
    Console.WriteLine(" Demo 1: Multi-Turn Conversation");
    Console.WriteLine("════════════════════════════════════════════════════════");

    // Each conversation maps to a Temporal workflow. Reusing the same ID across
    // ChatAsync calls routes all turns to the same workflow instance.
    var conversationId = $"multi-turn-{Guid.NewGuid():N}";
    Console.WriteLine($" Conversation ID: {conversationId}\n");

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

    Console.WriteLine("════════════════════════════════════════════════════════\n");
}

// ═════════════════════════════════════════════════════════════════════════════
// Demo 2: Tool call
//
// Shows how to expose tools to the LLM via ChatOptions.Tools. The
// UseFunctionInvocation() middleware in the pipeline handles the tool call
// loop automatically inside the Temporal activity — the whole round-trip
// (LLM request → tool call → LLM request with result) runs as one activity.
// ═════════════════════════════════════════════════════════════════════════════
static async Task RunToolCallDemoAsync(DurableChatSessionClient sessionClient, AIFunction weatherTool)
{
    Console.WriteLine("════════════════════════════════════════════════════════");
    Console.WriteLine(" Demo 2: Tool Call");
    Console.WriteLine("════════════════════════════════════════════════════════");

    var conversationId = $"tool-call-{Guid.NewGuid():N}";
    Console.WriteLine($" Conversation ID: {conversationId}\n");

    var q = "What is the weather like in Seattle right now?";
    Console.WriteLine($" User : {q}");

    // Pass tools via ChatOptions. The LLM will request a call to
    // get_current_weather; UseFunctionInvocation() executes it and sends
    // the result back in the same activity invocation.
    var options = new ChatOptions { Tools = [weatherTool] };
    var response = await sessionClient.ChatAsync(
        conversationId,
        [new ChatMessage(ChatRole.User, q)],
        options: options);

    Console.WriteLine($" Agent: {response.Text}");
    Console.WriteLine("════════════════════════════════════════════════════════\n");
}

// ═════════════════════════════════════════════════════════════════════════════
// Demo 3: History query
//
// Shows that the full conversation log is persisted in the Temporal workflow
// and can be retrieved at any time via GetHistoryAsync. This includes tool
// call and tool result messages, not just user/assistant text.
// ═════════════════════════════════════════════════════════════════════════════
static async Task RunHistoryQueryDemoAsync(DurableChatSessionClient sessionClient)
{
    Console.WriteLine("════════════════════════════════════════════════════════");
    Console.WriteLine(" Demo 3: History Query");
    Console.WriteLine("════════════════════════════════════════════════════════");

    var conversationId = $"history-{Guid.NewGuid():N}";
    Console.WriteLine($" Conversation ID: {conversationId}\n");

    // Build up a short conversation to populate the history.
    await sessionClient.ChatAsync(conversationId,
        [new ChatMessage(ChatRole.User, "Name three planets in our solar system.")]);

    await sessionClient.ChatAsync(conversationId,
        [new ChatMessage(ChatRole.User, "Which of those is closest to the Sun?")]);

    // GetHistoryAsync sends a Temporal Query to the running workflow.
    // The workflow returns every ChatMessage it has accumulated — user,
    // assistant, tool calls, and tool results.
    var history = await sessionClient.GetHistoryAsync(conversationId);

    Console.WriteLine(" Persisted history:");
    foreach (var msg in history)
    {
        var role = msg.Role == ChatRole.User      ? "User "
                 : msg.Role == ChatRole.Assistant ? "Agent"
                 : msg.Role.Value;

        var text = string.Concat(msg.Contents.OfType<TextContent>().Select(c => c.Text));
        if (!string.IsNullOrWhiteSpace(text))
            Console.WriteLine($"   [{role}] {text}");
    }

    Console.WriteLine($"\n Total messages stored: {history.Count}");
    Console.WriteLine("════════════════════════════════════════════════════════\n");
}
