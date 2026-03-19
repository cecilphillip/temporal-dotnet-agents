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
public sealed class DurableChatSessionClient
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
    /// Sends messages to a durable chat session and returns the response.
    /// Starts the session workflow if not already running.
    /// </summary>
    /// <param name="conversationId">A unique identifier for the conversation.</param>
    /// <param name="messages">The messages to send.</param>
    /// <param name="options">Optional chat options.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The chat response from the LLM.</returns>
    public async Task<ChatResponse> ChatAsync(
        string conversationId,
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
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
        await _client.StartWorkflowAsync(
            (DurableChatWorkflow wf) => wf.RunAsync(new DurableChatWorkflowInput
            {
                TimeToLive = _options.SessionTimeToLive,
                ActivityTimeout = _options.ActivityTimeout,
                HeartbeatTimeout = _options.HeartbeatTimeout,
                ApprovalTimeout = _options.ApprovalTimeout,
            }),
            new WorkflowOptions(workflowId, _options.TaskQueue!)
            {
                IdConflictPolicy = WorkflowIdConflictPolicy.UseExisting,
            });

        // Use a handle WITHOUT a pinned RunId so updates follow the continue-as-new chain.
        var handle = _client.GetWorkflowHandle<DurableChatWorkflow>(workflowId);

        // Send the chat turn via workflow update.
        var input = new DurableChatInput
        {
            Messages = messages as IList<ChatMessage> ?? messages.ToList(),
            Options = options,
            ConversationId = conversationId,
        };

        var output = await handle.ExecuteUpdateAsync<DurableChatWorkflow, DurableChatOutput>(
            wf => wf.ChatAsync(input));

        span?.SetTag(DurableChatTelemetry.ResponseModelAttribute, output.Response.ModelId);
        span?.SetTag(DurableChatTelemetry.InputTokensAttribute, output.Response.Usage?.InputTokenCount);
        span?.SetTag(DurableChatTelemetry.OutputTokensAttribute, output.Response.Usage?.OutputTokenCount);

        return output.Response;
    }

    /// <summary>
    /// Retrieves the conversation history for a session.
    /// </summary>
    public async Task<IReadOnlyList<ChatMessage>> GetHistoryAsync(
        string conversationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);

        var workflowId = GetWorkflowId(conversationId);
        var handle = _client.GetWorkflowHandle<DurableChatWorkflow>(workflowId);

        return await handle.QueryAsync<DurableChatWorkflow, IReadOnlyList<ChatMessage>>(
            wf => wf.GetHistory());
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
            wf => wf.GetPendingApproval());
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
            wf => wf.SubmitApprovalAsync(decision));
    }

    /// <summary>
    /// Generates the workflow ID from a conversation ID.
    /// </summary>
    internal string GetWorkflowId(string conversationId) =>
        $"{_options.WorkflowIdPrefix}{conversationId}";
}
