// Copyright (c) Microsoft. All rights reserved.

// Human-in-the-Loop (HITL) Sample
// =================================
// Demonstrates how an agent tool can pause execution, surface a structured approval
// request to a human reviewer, and then resume or cancel based on that decision —
// all transparently within a single proxy.RunAsync call.
//
// Scenario: An email assistant that can draft emails freely but requires a human
// to approve before any email is actually sent.
//
// How the approval flow works:
//
//   1. User: "Send a welcome email to alice@example.com"
//   2. Agent decides to call the send_email tool
//   3. send_email tool calls TemporalAgentContext.RequestApprovalAsync(...)
//      └─ sends a [WorkflowUpdate] to AgentWorkflow.RequestApprovalAsync
//         └─ workflow stores the ApprovalRequest and blocks on WaitConditionAsync
//            (the activity — and therefore proxy.RunAsync — stays suspended here)
//   4. This console polls GetPendingApprovalAsync() every second ([WorkflowQuery])
//   5. When a request appears, the human types "approve" or "reject"
//   6. SubmitApprovalAsync sends a [WorkflowUpdate] that sets _approvalDecision
//      └─ WaitConditionAsync unblocks
//      └─ RequestApprovalAsync returns an ApprovalTicket to the tool
//   7. Tool either completes the action or returns a cancellation message
//   8. Agent generates a final response — proxy.RunAsync finally returns
//
// Key configuration: ActivityStartToCloseTimeout must exceed your expected review
// time. This sample uses 24 hours. Adjust for your SLA.

using System.ClientModel;
using System.ComponentModel;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenAI;
using OpenAI.Chat;
using Temporalio.Extensions.Agents;
using Temporalio.Extensions.Hosting;

// OpenAI.Chat also defines ChatMessage and ChatRole; pin to the MEAI versions
// throughout this file so the conversation loop types remain unambiguous.
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;
using ChatRole = Microsoft.Extensions.AI.ChatRole;

// ── Configuration ──────────────────────────────────────────────────────────────
var builder = Host.CreateApplicationBuilder(args);
builder.Configuration.AddJsonFile("appsettings.local.json", optional: true, reloadOnChange: false);
builder.Logging.SetMinimumLevel(LogLevel.Warning);

var apiKey     = builder.Configuration.GetValue<string>("OPENAI_API_KEY")
    ?? throw new InvalidOperationException("OPENAI_API_KEY is required in appsettings.json.");
var apiBaseUrl = builder.Configuration.GetValue<string>("OPENAI_API_BASE_URL")
    ?? throw new InvalidOperationException("OPENAI_API_BASE_URL is required in appsettings.json.");
var model           = "gpt-4o-mini";
var temporalAddress = builder.Configuration.GetValue<string>("TEMPORAL_ADDRESS") ?? "localhost:7233";

// ── AI client ──────────────────────────────────────────────────────────────────
var openAiOptions = new OpenAIClientOptions { Endpoint = new Uri(apiBaseUrl) };
OpenAIClient openAiClient = new(new ApiKeyCredential(apiKey), openAiOptions);

// ── send_email tool — the heart of this sample ────────────────────────────────
// Before sending, the tool suspends the activity by sending a structured
// ApprovalRequest to the workflow. Execution resumes only when a human
// submits a decision via ITemporalAgentClient.SubmitApprovalAsync.
var sendEmailTool = AIFunctionFactory.Create(
    async (
        [Description("Recipient email address")] string to,
        [Description("Email subject")]           string subject,
        [Description("Full email body")]         string body) =>
    {
        var ctx = TemporalAgentContext.Current;

        // This call sends a [WorkflowUpdate] and blocks until SubmitApprovalAsync
        // is called from the approval console below.
        var ticket = await ctx.RequestApprovalAsync(new ApprovalRequest
        {
            Action  = $"Send email to {to}",
            Details = $"Subject: {subject}\n\nBody:\n{body}"
        });

        if (!ticket.Approved)
        {
            var reason = ticket.Comment ?? "no reason given";
            return $"Email to {to} was rejected by reviewer ({reason}). Not sent.";
        }

        // In a real system this would call your SMTP / SendGrid / SES client.
        Console.WriteLine();
        Console.WriteLine($"  [EMAIL SENT] To: {to}");
        Console.WriteLine($"               Subject: {subject}");
        return $"Email successfully sent to {to}.";
    },
    name: "send_email",
    description: "Sends an email to the specified recipient. Requires human approval before delivery.");

// ── EmailAssistant agent ───────────────────────────────────────────────────────
// Explicitly typed as ChatClient (not IChatClient) so the compiler selects the
// OpenAIChatClientExtensions.AsAIAgent overload, which exposes the clientFactory
// parameter needed to inject middleware (UseFunctionInvocation).
ChatClient chatClient = openAiClient.GetChatClient(model);
var emailAgent = chatClient.AsAIAgent(
    name: "EmailAssistant",
    instructions: """
        You are a helpful email assistant. Help users compose and send emails.
        When the user wants to send an email, use the send_email tool.
        Confirm the recipient, subject, and body content before calling the tool.
        If a send is rejected, explain what happened and offer to revise.
        """,
    tools: [sendEmailTool],
    clientFactory: client => client.AsBuilder().UseFunctionInvocation().Build());

// ── Worker registration ────────────────────────────────────────────────────────
builder.Services
    .AddTemporalClient(temporalAddress, "default");
builder.Services
    .AddHostedTemporalWorker(temporalAddress, "default", "hitl-sample")
    .AddTemporalAgents(opts =>
    {
        // HITL requires a timeout that covers the full human review window.
        // The underlying activity heartbeats during this period so the worker
        // won't treat it as stuck — as long as HeartbeatTimeout < StartToCloseTimeout.
        opts.ActivityStartToCloseTimeout = TimeSpan.FromHours(24);
        opts.ActivityHeartbeatTimeout    = TimeSpan.FromMinutes(5);

        opts.AddAIAgent(emailAgent, timeToLive: TimeSpan.FromHours(2));
    });

using var host = builder.Build();
await host.StartAsync();

Console.WriteLine();
Console.WriteLine("╔═══════════════════════════════════════════════════╗");
Console.WriteLine("║      Email Assistant — HITL Approval Sample       ║");
Console.WriteLine("╠═══════════════════════════════════════════════════╣");
Console.WriteLine("║  Ask the assistant to send an email.              ║");
Console.WriteLine("║  When it tries, you will be prompted to           ║");
Console.WriteLine("║  approve or reject before it is delivered.        ║");
Console.WriteLine("║  Type 'quit' to exit.                             ║");
Console.WriteLine("╚═══════════════════════════════════════════════════╝");
Console.WriteLine();

// ── Resolve services ───────────────────────────────────────────────────────────
var proxy  = host.Services.GetTemporalAgentProxy("EmailAssistant");
var client = host.Services.GetRequiredService<ITemporalAgentClient>();

// ── Conversation session ───────────────────────────────────────────────────────
// A single session means the agent remembers context across turns.
var session   = await proxy.CreateSessionAsync();
var sessionId = session.GetService<TemporalAgentSessionId>()!;

// ── Main conversation loop ─────────────────────────────────────────────────────
while (true)
{
    Console.Write("You: ");
    var input = Console.ReadLine();

    if (string.IsNullOrWhiteSpace(input)) continue;
    if (input.Equals("quit", StringComparison.OrdinalIgnoreCase)) break;

    var userMessages = new List<ChatMessage> { new(ChatRole.User, input) };

    // Start the agent call without awaiting — it may block inside the tool
    // while waiting for human approval, so we need to stay responsive.
    var agentTask = proxy.RunAsync(userMessages, session);

    Console.WriteLine("Assistant: (thinking...)");

    // Poll for pending approvals while the agent is running.
    // GetPendingApprovalAsync is a [WorkflowQuery] — it never blocks
    // the workflow and is safe to call as frequently as needed.
    while (!agentTask.IsCompleted)
    {
        await Task.Delay(TimeSpan.FromSeconds(1));

        if (agentTask.IsCompleted) break;

        ApprovalRequest? pending = null;
        try
        {
            pending = await client.GetPendingApprovalAsync(sessionId);
        }
        catch (Temporalio.Exceptions.RpcException ex) when (ex.Code == Temporalio.Exceptions.RpcException.StatusCode.NotFound)
        {
            // The workflow may not have started yet on the very first poll.
            // Retry on the next tick.
            continue;
        }

        if (pending is null) continue;

        // ── Approval gate ──────────────────────────────────────────────────
        // The agent is now suspended inside the tool. Surface the request
        // and wait for the human reviewer to decide.
        Console.WriteLine();
        Console.WriteLine("  ╔══════════════════════════════════════════════╗");
        Console.WriteLine("  ║            ⚠  APPROVAL REQUIRED             ║");
        Console.WriteLine("  ╠══════════════════════════════════════════════╣");
        Console.WriteLine($"  ║  Action: {pending.Action,-37}║");
        if (pending.Details is { } details)
        {
            Console.WriteLine("  ╠══════════════════════════════════════════════╣");
            foreach (var line in details.Split('\n'))
                Console.WriteLine($"  ║  {line,-44}║");
        }
        Console.WriteLine("  ╚══════════════════════════════════════════════╝");

        string choice;
        do
        {
            Console.Write("  Decision [approve/reject]: ");
            choice = (Console.ReadLine() ?? string.Empty).Trim().ToLowerInvariant();
        }
        while (choice is not "approve" and not "reject");

        string? comment = null;
        if (choice == "reject")
        {
            Console.Write("  Reason (optional, press Enter to skip): ");
            comment = Console.ReadLine()?.Trim();
            if (string.IsNullOrWhiteSpace(comment)) comment = null;
        }

        // SubmitApprovalAsync is a [WorkflowUpdate] — strongly consistent,
        // validates the RequestId, and unblocks WaitConditionAsync in the workflow.
        await client.SubmitApprovalAsync(sessionId, new ApprovalDecision
        {
            RequestId = pending.RequestId,
            Approved  = choice == "approve",
            Comment   = comment
        });

        Console.WriteLine(choice == "approve"
            ? "\n  ✓ Approved — agent is resuming..."
            : "\n  ✗ Rejected — agent is resuming...");
        Console.WriteLine();
    }

    var response = await agentTask;
    Console.WriteLine($"Assistant: {response.Text}");
    Console.WriteLine();
}

await host.StopAsync();
