using System.Diagnostics;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Temporalio.Api.Enums.V1;
using Temporalio.Client;

namespace Temporalio.Extensions.AI;

/// <summary>
/// External entry point for managed durable chat sessions.
/// Each conversation maps to a Temporal workflow that persists history across turns.
/// </summary>
public sealed class DurableChatSessionClient : IDurableChatSessionClient
{
    private readonly ITemporalClient _client;
    private readonly DurableExecutionOptions _options;
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="DurableChatSessionClient"/> class.
    /// </summary>
    public DurableChatSessionClient(
        ITemporalClient client,
        DurableExecutionOptions options,
        ILogger<DurableChatSessionClient>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        _client = client;
        _options = options;
        _logger = logger ?? NullLogger<DurableChatSessionClient>.Instance;
    }

    /// <summary>
    /// Sends messages to a durable chat session and returns the response entry.
    /// Starts the session workflow if not already running.
    /// </summary>
    /// <param name="conversationId">A unique identifier for the conversation.</param>
    /// <param name="messages">The messages to send.</param>
    /// <param name="options">Optional chat options.</param>
    /// <param name="correlationId">
    /// Optional caller-supplied correlation ID for this turn. When null/empty, the
    /// workflow auto-generates one via <c>Workflow.NewGuid()</c>. Useful for threading
    /// upstream HTTP/gRPC trace IDs into the workflow for cross-system log correlation.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The response entry from the LLM, including per-turn <see cref="UsageDetails"/> and correlation ID.</returns>
    public async Task<DurableSessionResponse> ChatAsync(
        string conversationId,
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        string? correlationId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
        ArgumentNullException.ThrowIfNull(messages);

        var workflowId = GetWorkflowId(conversationId);

        using var span = DurableChatTelemetry.ActivitySource.StartActivity(
            DurableChatTelemetry.ChatSendSpanName,
            ActivityKind.Client);

        span?.SetTag(DurableChatTelemetry.ConversationIdAttribute, conversationId);
        span?.SetTag(DurableChatTelemetry.RequestModelAttribute, options?.ModelId);

        _logger.LogDebug("Sending chat to session {WorkflowId}", workflowId);

        // Start the workflow if it doesn't exist, or reuse the existing one.
        // OriginalCreatedAt is intentionally omitted here — the workflow sets it to
        // Workflow.UtcNow on the first run and carries it forward through CAN transitions.
        await _client.StartWorkflowAsync(
            (DurableChatWorkflow wf) => wf.RunAsync(new DurableChatWorkflowInput
            {
                TimeToLive = _options.SessionTimeToLive,
                ActivityTimeout = _options.ActivityTimeout,
                HeartbeatTimeout = _options.HeartbeatTimeout,
                ApprovalTimeout = _options.ApprovalTimeout,
                EnableSearchAttributes = _options.EnableSearchAttributes,
                MaxEntryCount = _options.MaxEntryCount,
                HistoryReducer = _options.HistoryReducer,
            }),
            new WorkflowOptions(workflowId, _options.TaskQueue!)
            {
                IdConflictPolicy = WorkflowIdConflictPolicy.UseExisting,
                Rpc = new RpcOptions { CancellationToken = cancellationToken },
            });

        // Use a handle WITHOUT a pinned RunId so updates follow the continue-as-new chain.
        var handle = _client.GetWorkflowHandle<DurableChatWorkflow>(workflowId);

        // Resolve effective client key: per-call override wins, then worker-level default.
        var effectiveKey = options.GetChatClientKey() ?? _options.DefaultChatClientKey;

        // Send the chat turn via workflow update.
        var input = new DurableChatInput
        {
            Messages = messages as IList<ChatMessage> ?? messages.ToList(),
            Options = options,
            ConversationId = conversationId,
            ClientKey = effectiveKey,
            CorrelationId = string.IsNullOrEmpty(correlationId) ? null : correlationId,
        };

        var responseEntry = await handle.ExecuteUpdateAsync<DurableChatWorkflow, DurableSessionResponse>(
            wf => wf.ChatAsync(input),
            new WorkflowUpdateOptions { Rpc = new RpcOptions { CancellationToken = cancellationToken } });

        span?.SetTag(DurableChatTelemetry.InputTokensAttribute, responseEntry.Usage?.InputTokenCount);
        span?.SetTag(DurableChatTelemetry.OutputTokensAttribute, responseEntry.Usage?.OutputTokenCount);

        return responseEntry;
    }

    /// <summary>
    /// Retrieves the conversation history for a session as a list of
    /// <see cref="DurableSessionEntry"/> instances. Each turn appears as a request entry
    /// followed by a response entry.
    /// </summary>
    public async Task<IReadOnlyList<DurableSessionEntry>> GetHistoryAsync(
        string conversationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);

        var workflowId = GetWorkflowId(conversationId);
        var handle = _client.GetWorkflowHandle<DurableChatWorkflow>(workflowId);

        return await handle.QueryAsync<DurableChatWorkflow, IReadOnlyList<DurableSessionEntry>>(
            wf => wf.GetHistory(),
            new WorkflowQueryOptions { Rpc = new RpcOptions { CancellationToken = cancellationToken } });
    }

    // ── HITL: Tool Approval ─────────────────────────────────────────────

    /// <summary>
    /// Returns the currently pending approval request for a session, or null if none.
    /// </summary>
    public async Task<DurableApprovalRequest?> GetPendingApprovalAsync(
        string conversationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);

        var handle = _client.GetWorkflowHandle<DurableChatWorkflow>(GetWorkflowId(conversationId));
        return await handle.QueryAsync<DurableChatWorkflow, DurableApprovalRequest?>(
            wf => wf.GetPendingApproval(),
            new WorkflowQueryOptions { Rpc = new RpcOptions { CancellationToken = cancellationToken } });
    }

    /// <summary>
    /// Submits a human decision for a pending tool approval request.
    /// Unblocks the workflow's <c>RequestApprovalAsync</c> update.
    /// </summary>
    public async Task<DurableApprovalDecision> SubmitApprovalAsync(
        string conversationId,
        DurableApprovalDecision decision,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
        ArgumentNullException.ThrowIfNull(decision);

        var handle = _client.GetWorkflowHandle<DurableChatWorkflow>(GetWorkflowId(conversationId));
        return await handle.ExecuteUpdateAsync<DurableChatWorkflow, DurableApprovalDecision>(
            wf => wf.SubmitApprovalAsync(decision),
            new WorkflowUpdateOptions { Rpc = new RpcOptions { CancellationToken = cancellationToken } });
    }

    /// <summary>
    /// Generates the workflow ID from a conversation ID.
    /// </summary>
    internal string GetWorkflowId(string conversationId) =>
        $"{_options.WorkflowIdPrefix}{conversationId}";
}
