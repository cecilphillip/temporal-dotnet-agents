using Microsoft.Extensions.AI;
using Temporalio.Extensions.AI.IntegrationTests.Helpers;
using Xunit;

namespace Temporalio.Extensions.AI.IntegrationTests;

public class DurableApprovalIntegrationTests : IClassFixture<IntegrationTestFixture>
{
    private readonly IntegrationTestFixture _fixture;

    public DurableApprovalIntegrationTests(IntegrationTestFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task ApprovalFlow_ApproveUnblocksWorkflow()
    {
        var conversationId = $"approval-approve-{Guid.NewGuid():N}";

        // First, start a session so the workflow exists.
        await _fixture.SessionClient.ChatAsync(
            conversationId,
            [new ChatMessage(ChatRole.User, "Hello")]);

        // Submit an approval request (simulating what a tool would do inside a workflow).
        var request = new DurableApprovalRequest
        {
            RequestId = $"req-{Guid.NewGuid():N}",
            FunctionName = "delete_records",
            Description = "Delete user data",
        };

        // Request approval in a background task (it blocks until decision).
        var requestTask = RequestApprovalAsync(conversationId, request);

        // Give the update a moment to register.
        await Task.Delay(500);

        // Check pending approval.
        var pending = await _fixture.SessionClient.GetPendingApprovalAsync(conversationId);
        Assert.NotNull(pending);
        Assert.Equal(request.RequestId, pending!.RequestId);

        // Submit approval decision.
        var decision = new DurableApprovalDecision
        {
            RequestId = request.RequestId,
            Approved = true,
            Reason = "Approved by test",
        };

        var submitted = await _fixture.SessionClient.SubmitApprovalAsync(conversationId, decision);
        Assert.True(submitted.Approved);

        // The request task should complete now.
        var result = await requestTask;
        Assert.True(result.Approved);
        Assert.Equal(request.RequestId, result.RequestId);
    }

    [Fact]
    public async Task ApprovalFlow_RejectUnblocksWorkflow()
    {
        var conversationId = $"approval-reject-{Guid.NewGuid():N}";

        await _fixture.SessionClient.ChatAsync(
            conversationId,
            [new ChatMessage(ChatRole.User, "Hello")]);

        var request = new DurableApprovalRequest
        {
            RequestId = $"req-{Guid.NewGuid():N}",
            FunctionName = "dangerous_operation",
        };

        var requestTask = RequestApprovalAsync(conversationId, request);
        await Task.Delay(500);

        var decision = new DurableApprovalDecision
        {
            RequestId = request.RequestId,
            Approved = false,
            Reason = "Too risky",
        };

        await _fixture.SessionClient.SubmitApprovalAsync(conversationId, decision);

        var result = await requestTask;
        Assert.False(result.Approved);
        Assert.Equal("Too risky", result.Reason);
    }

    [Fact]
    public async Task GetPendingApproval_ReturnsNullWhenNoPending()
    {
        var conversationId = $"approval-none-{Guid.NewGuid():N}";

        await _fixture.SessionClient.ChatAsync(
            conversationId,
            [new ChatMessage(ChatRole.User, "Hello")]);

        var pending = await _fixture.SessionClient.GetPendingApprovalAsync(conversationId);
        Assert.Null(pending);
    }

    /// <summary>
    /// Sends a RequestApproval update to the workflow. This blocks until a decision is submitted.
    /// </summary>
    private async Task<DurableApprovalDecision> RequestApprovalAsync(
        string conversationId, DurableApprovalRequest request)
    {
        var workflowId = _fixture.SessionClient.GetWorkflowId(conversationId);
        var handle = _fixture.Client.GetWorkflowHandle<DurableChatWorkflow>(workflowId);
        return await handle.ExecuteUpdateAsync<DurableChatWorkflow, DurableApprovalDecision>(
            wf => wf.RequestApprovalAsync(request));
    }
}
