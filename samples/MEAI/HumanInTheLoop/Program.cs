// Human-in-the-Loop (HITL) Sample — Temporalio.Extensions.AI
// ============================================================
// Demonstrates how a tool can pause a durable chat session and wait for a human
// to approve or reject an operation before proceeding — using only IChatClient and
// Temporalio.Extensions.AI (no Microsoft Agent Framework required).
//
// Scenario: A data management assistant that can query records freely but requires
// explicit human approval before performing any destructive delete operation.
//
// How the approval flow works
// ──────────────────────────
//   1. User:  "Delete all records older than 30 days"
//   2. LLM decides to call the delete_records tool
//   3. delete_records tool calls RequestApprovalAsync on the workflow handle:
//      └─ sends a [WorkflowUpdate("RequestApproval")] to DurableChatWorkflow
//         └─ workflow stores DurableApprovalRequest and blocks on WaitConditionAsync
//            (DurableChatActivities.GetResponseAsync — and therefore ChatAsync — stays
//             suspended here; the Temporal activity keeps its heartbeat going)
//   4. The main loop polls GetPendingApprovalAsync() every second ([WorkflowQuery])
//   5. When a request appears, it is printed to the console
//   6. The sample auto-approves (simulating a human reviewer)
//      └─ SubmitApprovalAsync sends a [WorkflowUpdate("SubmitApproval")]
//         └─ WaitConditionAsync in RequestApprovalAsync unblocks
//         └─ RequestApprovalAsync returns DurableApprovalDecision to the tool
//   7. Tool either performs the delete and returns success, or returns a rejection message
//   8. LLM generates a final response — ChatAsync finally returns
//
// Integration with real approval systems
// ───────────────────────────────────────
// Replace the auto-approval logic below with any external mechanism:
//   • REST webhook: POST the DurableApprovalRequest to a review service;
//     the service calls SubmitApprovalAsync when the reviewer clicks Approve/Reject.
//   • Slack: Send a message with action buttons; the Slack handler calls SubmitApprovalAsync.
//   • PagerDuty / Jira: Create a ticket and poll the ticket state.
//   • Email: Send an email with a signed approval URL that hits your API.
// The workflow will wait up to ApprovalTimeout (default: 7 days). On timeout it
// auto-rejects with a descriptive reason so the tool can handle it gracefully.
//
// Key configuration note
// ──────────────────────
// The delete_records tool suspends inside an activity. The activity's
// StartToCloseTimeout must cover the full expected review window. This sample
// uses 24 hours. For production, set ApprovalTimeout (workflow-side) to match.
//
// Prerequisites
// ─────────────
// • A local Temporal server:  temporal server start-dev
// • OPENAI_API_KEY: dotnet user-secrets set "OPENAI_API_KEY" "sk-..." --project samples/MEAI/HumanInTheLoop
//
// Run:  dotnet run --project samples/MEAI/HumanInTheLoop/HumanInTheLoop.csproj

using System.ClientModel;
using System.ComponentModel;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenAI;
using Temporalio.Client;
using Temporalio.Extensions.AI;
using Temporalio.Extensions.Hosting;

// ── Configuration ─────────────────────────────────────────────────────────────
var builder = Host.CreateApplicationBuilder(args);
builder.Logging.SetMinimumLevel(LogLevel.Warning);

var apiKey        = builder.Configuration.GetValue<string>("OPENAI_API_KEY");
var apiBaseUrl    = builder.Configuration.GetValue<string>("OPENAI_API_BASE_URL");
var model         = builder.Configuration.GetValue<string>("OPENAI_MODEL") ?? "gpt-4o-mini";
var temporalAddress = builder.Configuration.GetValue<string>("TEMPORAL_ADDRESS") ?? "localhost:7233";

if (string.IsNullOrEmpty(apiBaseUrl))
    throw new InvalidOperationException("OPENAI_API_BASE_URL is not configured in appsettings.json.");
if (string.IsNullOrEmpty(apiKey))
    throw new InvalidOperationException("OPENAI_API_KEY is not configured. Set it with: dotnet user-secrets set \"OPENAI_API_KEY\" \"sk-...\" --project samples/MEAI/HumanInTheLoop");

// ── Temporal client with DurableAIDataConverter ───────────────────────────────
// DurableAIDataConverter.Instance wraps Temporal's payload converter with
// AIJsonUtilities.DefaultOptions, which handles MEAI's $type discriminator for
// polymorphic AIContent subclasses (TextContent, FunctionCallContent, etc.).
// Without this, type information is lost when types round-trip through history.
var temporalClient = await TemporalClient.ConnectAsync(new TemporalClientConnectOptions(temporalAddress)
{
    DataConverter = DurableAIDataConverter.Instance,
    Namespace     = "default",
});
builder.Services.AddSingleton<ITemporalClient>(temporalClient);

// ── Register IChatClient ──────────────────────────────────────────────────────
// AddChatClient + UseFunctionInvocation() is the idiomatic MEAI pattern.
// UseFunctionInvocation() handles the tool-call loop inside the activity:
//   LLM request → tool call → tool result → LLM request (repeat until done)
// The tool itself may suspend inside that loop for HITL approval.
IChatClient openAiChatClient = (IChatClient)new OpenAIClient(
    new ApiKeyCredential(apiKey),
    new OpenAIClientOptions { Endpoint = new Uri(apiBaseUrl) }
).GetChatClient(model);

builder.Services
    .AddChatClient(openAiChatClient)
    .UseFunctionInvocation()
    .Build();

// ── Worker + durable AI ───────────────────────────────────────────────────────
// ApprovalTimeout must be long enough to cover the full human review window.
// ActivityTimeout must also be long enough — the activity stays alive while
// the workflow is blocked waiting for a human response.
const string taskQueue = "hitl-meai-sample";

builder.Services
    .AddHostedTemporalWorker(temporalAddress, "default", taskQueue)
    .AddDurableAI(opts =>
    {
        // Leave headroom for the full review window.
        opts.ActivityTimeout  = TimeSpan.FromHours(24);
        opts.HeartbeatTimeout = TimeSpan.FromMinutes(5);
        opts.ApprovalTimeout  = TimeSpan.FromHours(24);
        opts.SessionTimeToLive = TimeSpan.FromHours(2);
    });

// ── Build and start host ──────────────────────────────────────────────────────
var host = builder.Build();
await host.StartAsync();

Console.WriteLine();
Console.WriteLine("╔══════════════════════════════════════════════════════════╗");
Console.WriteLine("║   Data Management Assistant — HITL Approval Sample       ║");
Console.WriteLine("╠══════════════════════════════════════════════════════════╣");
Console.WriteLine("║  The assistant can delete records but requires approval.  ║");
Console.WriteLine("║  This sample auto-approves to demonstrate the full flow.  ║");
Console.WriteLine("╚══════════════════════════════════════════════════════════╝");
Console.WriteLine();

var sessionClient = host.Services.GetRequiredService<DurableChatSessionClient>();

// ── Run the HITL demo ─────────────────────────────────────────────────────────
await RunHitlDemoAsync(sessionClient, temporalClient);

// ── Shutdown ──────────────────────────────────────────────────────────────────
try { await host.StopAsync(); } catch (OperationCanceledException) { }
Console.WriteLine("\nDone.");

// ═════════════════════════════════════════════════════════════════════════════
// HITL Demo
//
// Shows the complete approval gate: the LLM calls delete_records, which sends
// a RequestApproval update to the workflow and blocks. The main loop polls
// GetPendingApprovalAsync, discovers the request, and auto-approves it.
// ChatAsync then returns with the LLM's final response.
// ═════════════════════════════════════════════════════════════════════════════
static async Task RunHitlDemoAsync(
    DurableChatSessionClient sessionClient,
    ITemporalClient temporalClient)
{
    Console.WriteLine("════════════════════════════════════════════════════════════");
    Console.WriteLine(" Demo: Human-in-the-Loop Tool Approval");
    Console.WriteLine("════════════════════════════════════════════════════════════");

    // Each conversationId maps to one DurableChatWorkflow instance.
    var conversationId = $"hitl-demo-{Guid.NewGuid():N}";
    Console.WriteLine($" Conversation ID: {conversationId}\n");

    // ── Build the delete_records tool ─────────────────────────────────────
    // The tool closes over:
    //   • conversationId — used to locate the workflow handle
    //   • sessionClient  — exposes GetPendingApprovalAsync / SubmitApprovalAsync
    //     as well as the internal workflow ID prefix ("chat-" by default)
    //   • temporalClient — used to call the RequestApproval workflow update
    //
    // Flow inside the tool:
    //   1. Build a DurableApprovalRequest with a unique RequestId
    //   2. Call the workflow's RequestApproval [WorkflowUpdate] via the handle
    //      → workflow stores the request and blocks on WaitConditionAsync
    //      → this await returns only after SubmitApproval is called externally
    //   3. Inspect the returned DurableApprovalDecision
    //   4. If approved: perform the delete and return success
    //      If rejected: return a cancellation message
    var deleteTool = AIFunctionFactory.Create(
        async (
            [Description("Age threshold in days; records older than this will be deleted")]
            int olderThanDays) =>
        {
            // ── Step 1: Build the approval request ───────────────────────
            var requestId = Guid.NewGuid().ToString("N");
            var request   = new DurableApprovalRequest
            {
                RequestId    = requestId,
                FunctionName = "delete_records",
                Description  = $"Permanently delete all records older than {olderThanDays} days. " +
                               "This operation cannot be undone.",
            };

            Console.WriteLine($"\n [Tool] delete_records called (olderThan={olderThanDays} days)");
            Console.WriteLine(" [Tool] Sending approval request to workflow...");

            // ── Step 2: Send the RequestApproval update ───────────────────
            // The workflow ID is "{prefix}{conversationId}". The prefix is
            // "chat-" by default (DurableExecutionOptions.WorkflowIdPrefix).
            // We construct it the same way DurableChatSessionClient does.
            //
            // DurableChatWorkflow is internal to the library, so we use the
            // untyped GetWorkflowHandle(workflowId) overload and call the update
            // by its registered name ("RequestApproval") with an argument array.
            //
            // ExecuteUpdateAsync blocks until the workflow's RequestApprovalAsync
            // update handler returns — which only happens after SubmitApprovalAsync
            // is called. The activity (and therefore this tool invocation) remains
            // suspended in Temporal until then.
            var workflowId = $"chat-{conversationId}";
            var handle     = temporalClient.GetWorkflowHandle(workflowId);

            // This line suspends until a human calls SubmitApprovalAsync.
            // The DurableChatWorkflow.RequestApprovalAsync [WorkflowUpdate("RequestApproval")] handler:
            //   • stores the request in _pendingApproval
            //   • waits on WaitConditionAsync until _approvalDecision is set
            //   • returns the decision once SubmitApprovalAsync sets _approvalDecision
            var decision = await handle.ExecuteUpdateAsync<DurableApprovalDecision>(
                "RequestApproval",
                new object[] { request });

            Console.WriteLine($" [Tool] Approval decision received: {(decision.Approved ? "APPROVED" : "REJECTED")}");
            if (decision.Reason is { Length: > 0 })
                Console.WriteLine($" [Tool] Reason: {decision.Reason}");

            // ── Step 3: Act on the decision ───────────────────────────────
            if (!decision.Approved)
            {
                var reason = decision.Reason ?? "no reason given";
                return $"Deletion rejected by reviewer ({reason}). No records were deleted.";
            }

            // In a real system this would call your database / storage layer.
            Console.WriteLine($" [Tool] Deleting records older than {olderThanDays} days...");
            await Task.Delay(TimeSpan.FromMilliseconds(200)); // simulate I/O

            return $"Successfully deleted all records older than {olderThanDays} days.";
        },
        name: "delete_records",
        description: "Permanently deletes records older than the specified number of days. " +
                     "Requires explicit human approval before any data is removed.");

    // ── System prompt explaining the assistant's purpose ──────────────────
    var systemMessage = new ChatMessage(ChatRole.System,
        """
        You are a helpful data management assistant.
        You can answer questions about records and data.
        When the user requests a delete operation, use the delete_records tool.
        Always confirm what you are about to delete before calling the tool.
        If a deletion is rejected by the reviewer, explain the situation and offer alternatives.
        """);

    var userQuestion = "Delete all records older than 30 days.";
    Console.WriteLine($" User : {userQuestion}\n");

    // ── Start the chat turn ───────────────────────────────────────────────
    // ChatAsync sends a Chat [WorkflowUpdate] to DurableChatWorkflow.
    // Inside the workflow, DurableChatActivities.GetResponseAsync is scheduled.
    // UseFunctionInvocation() runs the tool-call loop within that activity.
    // When delete_records calls RequestApprovalAsync on the handle, the activity
    // stays alive (the Temporal SDK heartbeats it) while the workflow is blocked.
    var chatOptions = new ChatOptions
    {
        Tools = [deleteTool],
    };

    // Start chat in the background — it will block inside the tool waiting for approval.
    var chatTask = sessionClient.ChatAsync(
        conversationId,
        [systemMessage, new ChatMessage(ChatRole.User, userQuestion)],
        options: chatOptions);

    Console.WriteLine(" [Main] Chat started — polling for pending approval...\n");

    // ── Poll for the pending approval request ─────────────────────────────
    // GetPendingApprovalAsync is a [WorkflowQuery] — it returns instantly and
    // never blocks the workflow. Poll until the tool has registered its request.
    DurableApprovalRequest? pending = null;
    while (!chatTask.IsCompleted)
    {
        await Task.Delay(TimeSpan.FromSeconds(1));

        if (chatTask.IsCompleted) break;

        try
        {
            pending = await sessionClient.GetPendingApprovalAsync(conversationId);
        }
        catch (Temporalio.Exceptions.RpcException ex)
            when (ex.Code == Temporalio.Exceptions.RpcException.StatusCode.NotFound)
        {
            // Workflow may not have registered its first event yet on the very
            // first poll. Retry on the next tick.
            continue;
        }

        if (pending is not null) break;
    }

    if (pending is not null)
    {
        // ── Display the pending approval request ──────────────────────────
        Console.WriteLine(" ╔══════════════════════════════════════════════════╗");
        Console.WriteLine(" ║           APPROVAL REQUIRED                      ║");
        Console.WriteLine(" ╠══════════════════════════════════════════════════╣");
        Console.WriteLine($" ║  Request ID  : {pending.RequestId[..8]}...                       ║");
        Console.WriteLine($" ║  Function    : {pending.FunctionName,-38}║");
        if (pending.Description is { Length: > 0 })
            Console.WriteLine($" ║  Description : {pending.Description[..Math.Min(38, pending.Description.Length)],-38}║");
        Console.WriteLine(" ╚══════════════════════════════════════════════════╝");
        Console.WriteLine();

        // ── Auto-approve (simulating a human reviewer) ────────────────────
        // In a real system this would be replaced by:
        //   • Console.ReadLine() to capture input
        //   • A webhook/Slack handler
        //   • Any external decision mechanism
        Console.WriteLine(" [Reviewer] Auto-approving request to demonstrate the full flow...");

        // SubmitApprovalAsync sends the SubmitApproval [WorkflowUpdate].
        // This sets _approvalDecision in the workflow, which satisfies
        // the WaitConditionAsync in RequestApprovalAsync, which returns the
        // decision to the tool, which unblocks the activity, which allows
        // DurableChatActivities.GetResponseAsync to complete.
        var decision = new DurableApprovalDecision
        {
            RequestId = pending.RequestId,
            Approved  = true,
            Reason    = "Auto-approved by sample reviewer.",
        };

        await sessionClient.SubmitApprovalAsync(conversationId, decision);
        Console.WriteLine(" [Reviewer] Approval submitted — waiting for assistant response...\n");
    }
    else
    {
        Console.WriteLine(" [Main] Chat completed without triggering an approval gate.\n");
    }

    // ── Await the final response ──────────────────────────────────────────
    // Now that the approval has been submitted, the workflow unblocks,
    // the tool returns its result, UseFunctionInvocation() sends it back to
    // the LLM for a final response, and ChatAsync returns.
    var response = await chatTask;
    Console.WriteLine($" Assistant: {response.Text}");
    Console.WriteLine();

    // ── Show persisted history ────────────────────────────────────────────
    // GetHistoryAsync retrieves the full conversation log from the workflow,
    // including user messages, assistant messages, tool calls, and tool results.
    var history = await sessionClient.GetHistoryAsync(conversationId);
    Console.WriteLine($" [History] {history.Count} messages persisted in workflow state.");

    Console.WriteLine("════════════════════════════════════════════════════════════\n");
}
