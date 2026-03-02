using FakeItEasy;
using Temporalio.Client;
using Temporalio.Client.Schedules;
using Temporalio.Extensions.Agents.Tests.Helpers;
using Xunit;

namespace Temporalio.Extensions.Agents.Tests;

/// <summary>
/// Tests for <see cref="DefaultTemporalAgentClient"/> guard clauses and validation logic.
/// These test the validation layer — all guards throw before touching the Temporal client.
/// </summary>
public class DefaultTemporalAgentClientTests
{
    private readonly ITemporalClient _fakeClient = A.Fake<ITemporalClient>();
    private readonly TemporalAgentsOptions _options = new();
    private const string TaskQueue = "test-queue";

    private DefaultTemporalAgentClient CreateClient(IAgentRouter? router = null) =>
        new(_fakeClient, _options, TaskQueue, logger: null, router: router);

    // ─── RunAgentAsync ───────────────────────────────────────────────────────

    [Fact]
    public async Task RunAgentAsync_NullRequest_ThrowsArgumentNullException()
    {
        var client = CreateClient();
        var sessionId = TemporalAgentSessionId.WithRandomKey("Agent");

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            client.RunAgentAsync(sessionId, null!));
    }

    // ─── RunAgentFireAndForgetAsync ──────────────────────────────────────────

    [Fact]
    public async Task RunAgentFireAndForgetAsync_NullRequest_ThrowsArgumentNullException()
    {
        var client = CreateClient();
        var sessionId = TemporalAgentSessionId.WithRandomKey("Agent");

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            client.RunAgentFireAndForgetAsync(sessionId, null!));
    }

    // ─── RouteAsync ──────────────────────────────────────────────────────────

    [Fact]
    public async Task RouteAsync_NullSessionKey_ThrowsArgumentException()
    {
        var client = CreateClient();

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            client.RouteAsync(null!, new RunRequest("test")));
    }

    [Fact]
    public async Task RouteAsync_WhitespaceSessionKey_ThrowsArgumentException()
    {
        var client = CreateClient();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            client.RouteAsync("   ", new RunRequest("test")));
    }

    [Fact]
    public async Task RouteAsync_NullRequest_ThrowsArgumentNullException()
    {
        var client = CreateClient();

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            client.RouteAsync("session-key", null!));
    }

    [Fact]
    public async Task RouteAsync_NoRouter_ThrowsInvalidOperationException()
    {
        var client = CreateClient(router: null);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.RouteAsync("session-key", new RunRequest("test")));

        Assert.Contains("No IAgentRouter is configured", ex.Message);
    }

    [Fact]
    public async Task RouteAsync_NoDescriptors_ThrowsInvalidOperationException()
    {
        var fakeRouter = A.Fake<IAgentRouter>();
        var client = CreateClient(router: fakeRouter);
        // _options has no descriptors added

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.RouteAsync("session-key", new RunRequest("test")));

        Assert.Contains("No agent descriptors are registered", ex.Message);
    }

    // ─── SubmitApprovalAsync ─────────────────────────────────────────────────

    [Fact]
    public async Task SubmitApprovalAsync_NullDecision_ThrowsArgumentNullException()
    {
        var client = CreateClient();
        var sessionId = TemporalAgentSessionId.WithRandomKey("Agent");

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            client.SubmitApprovalAsync(sessionId, null!));
    }

    // ─── RunAgentDelayedAsync ────────────────────────────────────────────────

    [Fact]
    public async Task RunAgentDelayedAsync_NullRequest_ThrowsArgumentNullException()
    {
        var client = CreateClient();
        var sessionId = TemporalAgentSessionId.WithRandomKey("Agent");

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            client.RunAgentDelayedAsync(sessionId, null!, TimeSpan.FromMinutes(5)));
    }

    // ─── ScheduleAgentAsync ──────────────────────────────────────────────────

    [Fact]
    public async Task ScheduleAgentAsync_NullAgentName_ThrowsArgumentException()
    {
        var client = CreateClient();

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            client.ScheduleAgentAsync(null!, "schedule-1", new RunRequest("test"),
                new ScheduleSpec()));
    }

    [Fact]
    public async Task ScheduleAgentAsync_WhitespaceAgentName_ThrowsArgumentException()
    {
        var client = CreateClient();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            client.ScheduleAgentAsync("  ", "schedule-1", new RunRequest("test"),
                new ScheduleSpec()));
    }

    [Fact]
    public async Task ScheduleAgentAsync_NullScheduleId_ThrowsArgumentException()
    {
        var client = CreateClient();

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            client.ScheduleAgentAsync("Agent", null!, new RunRequest("test"),
                new ScheduleSpec()));
    }

    [Fact]
    public async Task ScheduleAgentAsync_NullRequest_ThrowsArgumentNullException()
    {
        var client = CreateClient();

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            client.ScheduleAgentAsync("Agent", "schedule-1", null!,
                new ScheduleSpec()));
    }

    [Fact]
    public async Task ScheduleAgentAsync_NullSpec_ThrowsArgumentNullException()
    {
        var client = CreateClient();

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            client.ScheduleAgentAsync("Agent", "schedule-1", new RunRequest("test"),
                null!));
    }

    // ─── GetAgentScheduleHandle ──────────────────────────────────────────────

    [Fact]
    public void GetAgentScheduleHandle_NullScheduleId_ThrowsArgumentException()
    {
        var client = CreateClient();

        Assert.Throws<ArgumentNullException>(() =>
            client.GetAgentScheduleHandle(null!));
    }

    [Fact]
    public void GetAgentScheduleHandle_WhitespaceScheduleId_ThrowsArgumentException()
    {
        var client = CreateClient();

        Assert.Throws<ArgumentException>(() =>
            client.GetAgentScheduleHandle("   "));
    }
}
