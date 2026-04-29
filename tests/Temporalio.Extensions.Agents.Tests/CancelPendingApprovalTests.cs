using Microsoft.Agents.AI;
using Temporalio.Client.Schedules;
using Temporalio.Extensions.Agents.Session;
using Temporalio.Extensions.Agents.Workflows;
using Temporalio.Extensions.AI;
using Xunit;

namespace Temporalio.Extensions.Agents.Tests;

/// <summary>
/// Tests for the <see cref="ITemporalAgentClient.CancelPendingApprovalAsync"/> default
/// interface method. Covers the no-op path (no pending approval) and the rejection-submit
/// path with default and custom reasons.
/// </summary>
/// <remarks>
/// Uses a hand-written recording stub rather than FakeItEasy so the default interface method
/// implementation is exercised. FakeItEasy's proxy generator intercepts default interface
/// methods and treats them as overridable members, returning a default value instead of
/// running the DIM body.
/// </remarks>
public class CancelPendingApprovalTests
{
    private static readonly TemporalAgentSessionId SessionId =
        TemporalAgentSessionId.WithRandomKey("CancelTestAgent");

    [Fact]
    public async Task CancelPendingApprovalAsync_NoPending_NoOp()
    {
        var stub = new RecordingClient(pending: null);

        await ((ITemporalAgentClient)stub).CancelPendingApprovalAsync(SessionId);

        Assert.Equal(1, stub.GetPendingApprovalCallCount);
        Assert.Equal(0, stub.SubmitApprovalCallCount);
    }

    [Fact]
    public async Task CancelPendingApprovalAsync_WithPending_SubmitsRejection()
    {
        var stub = new RecordingClient(pending: new DurableApprovalRequest
        {
            RequestId = "req-123",
            Description = "delete records",
        });

        await ((ITemporalAgentClient)stub).CancelPendingApprovalAsync(SessionId, reason: "User cancelled.");

        Assert.Equal(1, stub.SubmitApprovalCallCount);
        Assert.NotNull(stub.LastSubmittedDecision);
        Assert.Equal("req-123", stub.LastSubmittedDecision!.RequestId);
        Assert.False(stub.LastSubmittedDecision.Approved);
        Assert.Equal("User cancelled.", stub.LastSubmittedDecision.Reason);
        Assert.Equal(SessionId.WorkflowId, stub.LastSubmittedSessionId?.WorkflowId);
    }

    [Fact]
    public async Task CancelPendingApprovalAsync_DefaultReason_Used()
    {
        var stub = new RecordingClient(pending: new DurableApprovalRequest
        {
            RequestId = "req-default",
            Description = "test",
        });

        await ((ITemporalAgentClient)stub).CancelPendingApprovalAsync(SessionId);

        Assert.Equal(1, stub.SubmitApprovalCallCount);
        Assert.NotNull(stub.LastSubmittedDecision);
        Assert.Equal("req-default", stub.LastSubmittedDecision!.RequestId);
        Assert.False(stub.LastSubmittedDecision.Approved);
        Assert.Equal("Cancelled externally.", stub.LastSubmittedDecision.Reason);
    }

    /// <summary>
    /// Records inputs to <see cref="GetPendingApprovalAsync"/> and <see cref="SubmitApprovalAsync"/>
    /// so tests can assert the default interface method dispatched correctly.
    /// All other interface members throw <see cref="NotImplementedException"/>.
    /// </summary>
    private sealed class RecordingClient(DurableApprovalRequest? pending) : ITemporalAgentClient
    {
        private readonly DurableApprovalRequest? _pending = pending;

        public int GetPendingApprovalCallCount { get; private set; }

        public int SubmitApprovalCallCount { get; private set; }

        public TemporalAgentSessionId? LastSubmittedSessionId { get; private set; }

        public DurableApprovalDecision? LastSubmittedDecision { get; private set; }

        public Task<DurableApprovalRequest?> GetPendingApprovalAsync(
            TemporalAgentSessionId sessionId,
            CancellationToken cancellationToken = default)
        {
            GetPendingApprovalCallCount++;
            return Task.FromResult(_pending);
        }

        public Task<DurableApprovalDecision> SubmitApprovalAsync(
            TemporalAgentSessionId sessionId,
            DurableApprovalDecision decision,
            CancellationToken cancellationToken = default)
        {
            SubmitApprovalCallCount++;
            LastSubmittedSessionId = sessionId;
            LastSubmittedDecision = decision;
            return Task.FromResult(decision);
        }

        public Task<AgentResponse> RunAgentAsync(TemporalAgentSessionId sessionId, RunRequest request, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<AgentResponse> RunAgentAsync(string agentName, string message, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task RunAgentFireAndForgetAsync(TemporalAgentSessionId sessionId, RunRequest request, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<AgentResponse> RouteAsync(string sessionKey, RunRequest request, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task RunAgentDelayedAsync(TemporalAgentSessionId sessionId, RunRequest request, TimeSpan delay, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<ScheduleHandle> ScheduleAgentAsync(string agentName, string scheduleId, RunRequest request, ScheduleSpec spec, SchedulePolicy? policy = null, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public ScheduleHandle GetAgentScheduleHandle(string scheduleId) =>
            throw new NotImplementedException();
    }
}
