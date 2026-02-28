using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Temporalio.Api.Enums.V1;
using Temporalio.Client;
using Xunit;
using Xunit.Abstractions;

namespace Temporalio.Extensions.Agents.IntegrationTests;

/// <summary>
/// Tests for workflow lifecycle management: shutdown signals, concurrent update
/// serialization, and worker restart / history replay.
/// </summary>
[Trait("Category", "Integration")]
public class WorkflowLifecycleTests : IClassFixture<IntegrationTestFixture>
{
    private readonly IntegrationTestFixture _fixture;
    private readonly ITestOutputHelper _output;

    public WorkflowLifecycleTests(IntegrationTestFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    // ── Shutdown signal ────────────────────────────────────────────────────────

    [Fact]
    public async Task Shutdown_Signal_CompletesWorkflow()
    {
        // Establish a session with one turn so the workflow is running.
        var session = (TemporalAgentSession)await _fixture.AgentProxy.CreateSessionAsync();
        var r1 = await _fixture.AgentProxy.RunAsync("Before shutdown", session);
        Assert.Contains("Echo [1]:", r1.Messages[0].Text);

        // Get a typed handle and send the Shutdown signal.
        var handle = _fixture.Client.GetWorkflowHandle<AgentWorkflow>(
            session.SessionId.WorkflowId);
        await handle.SignalAsync(wf => wf.RequestShutdownAsync());

        // Wait for the workflow to complete (should be near-instant after signal).
        await handle.GetResultAsync();

        // Confirm the workflow is actually completed.
        var desc = await handle.DescribeAsync();
        Assert.Equal(WorkflowExecutionStatus.Completed, desc.Status);

        _output.WriteLine($"Workflow {session.SessionId.WorkflowId} completed after shutdown signal.");
    }

    [Fact]
    public async Task Shutdown_ThenNewRequest_StartsFreshWorkflow()
    {
        // Establish a session with one turn.
        var session = (TemporalAgentSession)await _fixture.AgentProxy.CreateSessionAsync();
        await _fixture.AgentProxy.RunAsync("Turn 1", session);

        // Shutdown the workflow.
        var handle = _fixture.Client.GetWorkflowHandle<AgentWorkflow>(
            session.SessionId.WorkflowId);
        await handle.SignalAsync(wf => wf.RequestShutdownAsync());
        await handle.GetResultAsync();

        // Send another request to the SAME session ID.
        // With IdReusePolicy.AllowDuplicate, this starts a fresh workflow run.
        var r2 = await _fixture.AgentProxy.RunAsync("After restart", session);

        // The fresh workflow has no prior history — turn count resets to 1.
        Assert.Contains("Echo [1]: After restart", r2.Messages[0].Text);

        _output.WriteLine("Fresh workflow started after shutdown; turn count reset to 1.");
    }

    // ── Concurrent updates serialization ───────────────────────────────────────

    [Fact]
    public async Task ConcurrentUpdates_AllComplete_WithoutError()
    {
        var session = await _fixture.AgentProxy.CreateSessionAsync();
        const int concurrentRequests = 5;

        // Launch multiple RunAsync calls in parallel to the same session.
        var tasks = Enumerable.Range(1, concurrentRequests)
            .Select(i => _fixture.AgentProxy.RunAsync($"Msg-{i}", session))
            .ToArray();

        var results = await Task.WhenAll(tasks);

        // All should succeed.
        Assert.Equal(concurrentRequests, results.Length);
        foreach (var r in results)
        {
            Assert.NotNull(r);
            Assert.NotEmpty(r.Messages);
        }

        _output.WriteLine($"All {concurrentRequests} concurrent updates completed successfully.");
    }

    [Fact]
    public async Task ConcurrentUpdates_TurnCountsAreSequential()
    {
        var session = await _fixture.AgentProxy.CreateSessionAsync();
        const int concurrentRequests = 5;

        // Launch multiple RunAsync calls in parallel to the same session.
        var tasks = Enumerable.Range(1, concurrentRequests)
            .Select(i => _fixture.AgentProxy.RunAsync($"Msg-{i}", session))
            .ToArray();

        var results = await Task.WhenAll(tasks);

        // Each response's turn count should be unique (1–5) because the
        // workflow serializes updates via _isProcessing flag.
        var turnCounts = results
            .Select(r => r.Messages[0].Text)
            .Select(text =>
            {
                // Parse "Echo [N]: ..." → N
                var bracket = text.IndexOf('[');
                var closeBracket = text.IndexOf(']');
                return int.Parse(text[(bracket + 1)..closeBracket]);
            })
            .OrderBy(n => n)
            .ToList();

        _output.WriteLine($"Turn counts observed: [{string.Join(", ", turnCounts)}]");

        // Should be exactly [1, 2, 3, 4, 5] — no duplicates, no gaps.
        Assert.Equal(Enumerable.Range(1, concurrentRequests).ToList(), turnCounts);
    }

    // ── Fire-and-forget vs synchronous race ────────────────────────────────────

    [Fact]
    public async Task FireAndForget_AndSynchronousUpdate_BothComplete()
    {
        // Send a fire-and-forget signal and a synchronous update to the same session.
        // The workflow serializes both via the _isProcessing flag: the signal queues
        // work via Workflow.RunTaskAsync, while the update awaits WaitConditionAsync.
        var session = await _fixture.AgentProxy.CreateSessionAsync();

        // Send synchronous turn 1 to establish the session.
        var r1 = await _fixture.AgentProxy.RunAsync("Turn 1", session);
        Assert.Contains("Echo [1]:", r1.Messages[0].Text);

        // Now fire both a fire-and-forget signal and a synchronous update concurrently.
        var fireAndForgetOptions = new TemporalAgentRunOptions { IsFireAndForget = true };
        var fireAndForgetTask = _fixture.AgentProxy.RunAsync("F&F message", session, fireAndForgetOptions);
        var synchronousTask = _fixture.AgentProxy.RunAsync("Sync message", session);

        // Both should complete without error.
        var fafResponse = await fireAndForgetTask;
        var syncResponse = await synchronousTask;

        // Fire-and-forget returns empty response.
        Assert.NotNull(fafResponse);
        Assert.Empty(fafResponse.Messages);

        // Synchronous update returns the agent's response.
        Assert.NotNull(syncResponse);
        Assert.NotEmpty(syncResponse.Messages);

        // Give the fire-and-forget a moment to process.
        await Task.Delay(TimeSpan.FromSeconds(1));

        // Query history — should have 3 requests total (turn 1 + F&F + sync).
        var typedSession = (TemporalAgentSession)session;
        var handle = _fixture.Client.GetWorkflowHandle<AgentWorkflow>(
            typedSession.SessionId.WorkflowId);
        var history = await handle.QueryAsync(wf => wf.GetHistory());

        // Each request+response pair = 2 entries, so at least 6 entries.
        // The F&F might still be processing, so just verify at least 4 entries
        // (turn 1 req + resp, sync req + resp) and at most 6.
        Assert.True(history.Count >= 4,
            $"Expected at least 4 history entries but got {history.Count}");

        _output.WriteLine(
            $"Fire-and-forget + synchronous both completed. " +
            $"History entries: {history.Count}");
    }

    // ── Worker restart & history replay ────────────────────────────────────────

    [Fact]
    public async Task WorkerRestart_HistoryPreservedViaReplay()
    {
        // Build a SEPARATE host so we can control its lifecycle independently.
        // Using a unique task queue avoids interference with the shared fixture worker.
        var taskQueue = $"restart-test-{Guid.NewGuid():N}";
        var host1 = BuildHostForTaskQueue(taskQueue);
        await host1.StartAsync();

        try
        {
            var proxy1 = host1.Services.GetTemporalAgentProxy("EchoAgent");

            // Turn 1 — on host1.
            var session = (TemporalAgentSession)await proxy1.CreateSessionAsync();
            var r1 = await proxy1.RunAsync("Turn 1", session);
            Assert.Contains("Echo [1]: Turn 1", r1.Messages[0].Text);

            _output.WriteLine($"Turn 1 completed on host1. Stopping host1...");

            // Stop host1 (kills the worker — the workflow stays Running on the server).
            await host1.StopAsync();
            host1.Dispose();

            _output.WriteLine("Host1 stopped. Starting host2...");

            // Start a brand new host on the SAME task queue.
            var host2 = BuildHostForTaskQueue(taskQueue);
            await host2.StartAsync();

            try
            {
                var proxy2 = host2.Services.GetTemporalAgentProxy("EchoAgent");

                // Turn 2 — on host2, same session ID.
                // The workflow replays from event history to rebuild state,
                // then processes the new update.
                var resumedSession = new TemporalAgentSession(session.SessionId);
                var r2 = await proxy2.RunAsync("Turn 2", resumedSession);

                // History is preserved: the agent sees 2 user messages.
                Assert.Contains("Echo [2]: Turn 2", r2.Messages[0].Text);

                _output.WriteLine("Turn 2 completed on host2 — history preserved via replay.");
            }
            finally
            {
                await host2.StopAsync();
                host2.Dispose();
            }
        }
        catch
        {
            // If we fail before stopping host1, clean up.
            try { await host1.StopAsync(); } catch { }
            host1.Dispose();
            throw;
        }
    }

    // ── Multiple concurrent sessions ──────────────────────────────────────────

    [Fact]
    public async Task MultipleConcurrentSessions_AreFullyIsolated()
    {
        // Create 3 independent sessions to the same agent.
        // Each session gets its own workflow instance (unique WorkflowId).
        var sessions = await Task.WhenAll(
            _fixture.AgentProxy.CreateSessionAsync().AsTask(),
            _fixture.AgentProxy.CreateSessionAsync().AsTask(),
            _fixture.AgentProxy.CreateSessionAsync().AsTask());

        // Verify all 3 workflow IDs are distinct.
        var workflowIds = sessions
            .Cast<TemporalAgentSession>()
            .Select(s => s.SessionId.WorkflowId)
            .ToList();
        Assert.Equal(3, workflowIds.Distinct().Count());

        // Send messages in parallel — each session should maintain independent state.
        var tasks = sessions.Select(async (session, index) =>
        {
            var r1 = await _fixture.AgentProxy.RunAsync($"Session{index}-Turn1", session);
            Assert.Contains("Echo [1]:", r1.Messages[0].Text);

            var r2 = await _fixture.AgentProxy.RunAsync($"Session{index}-Turn2", session);
            Assert.Contains("Echo [2]:", r2.Messages[0].Text);

            return r2;
        }).ToArray();

        var results = await Task.WhenAll(tasks);

        // All 3 sessions completed with correct per-session turn counts.
        Assert.Equal(3, results.Length);
        for (int i = 0; i < 3; i++)
        {
            Assert.Contains($"Session{i}-Turn2", results[i].Messages[0].Text);
        }

        _output.WriteLine(
            $"3 concurrent sessions with IDs [{string.Join(", ", workflowIds)}] " +
            "all maintained independent history.");
    }

    // ── Session TTL enforcement ──────────────────────────────────────────────

    [Fact]
    public async Task SessionTTL_WorkflowCompletesAfterExpiry()
    {
        // Register an agent with a very short TTL (2 seconds).
        var taskQueue = $"ttl-test-{Guid.NewGuid():N}";
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<ITemporalClient>(_fixture.Client);
        builder.Services.ConfigureTemporalAgents(
            configure: options =>
            {
                options.DefaultTimeToLive = TimeSpan.FromSeconds(2);
                options.AddAIAgent(new Helpers.EchoAIAgent("TTLAgent"));
            },
            taskQueue: taskQueue);

        using var host = builder.Build();
        await host.StartAsync();

        try
        {
            var proxy = host.Services.GetTemporalAgentProxy("TTLAgent");
            var session = (TemporalAgentSession)await proxy.CreateSessionAsync();

            // Send one message to start the workflow.
            var r1 = await proxy.RunAsync("Hello TTL", session);
            Assert.Contains("Echo [1]: Hello TTL", r1.Messages[0].Text);

            var handle = _fixture.Client.GetWorkflowHandle<AgentWorkflow>(
                session.SessionId.WorkflowId);

            // Wait for the workflow to complete via TTL expiry.
            // The WaitConditionAsync in RunAsync has a 2-second timeout.
            await handle.GetResultAsync();

            var desc = await handle.DescribeAsync();
            Assert.Equal(WorkflowExecutionStatus.Completed, desc.Status);

            _output.WriteLine(
                $"Workflow {session.SessionId.WorkflowId} completed after TTL expiry.");
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task SessionTTL_NewRequestAfterExpiry_StartsFreshWorkflow()
    {
        // Register an agent with a very short TTL (2 seconds).
        var taskQueue = $"ttl-restart-{Guid.NewGuid():N}";
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<ITemporalClient>(_fixture.Client);
        builder.Services.ConfigureTemporalAgents(
            configure: options =>
            {
                options.DefaultTimeToLive = TimeSpan.FromSeconds(2);
                options.AddAIAgent(new Helpers.EchoAIAgent("TTLAgent"));
            },
            taskQueue: taskQueue);

        using var host = builder.Build();
        await host.StartAsync();

        try
        {
            var proxy = host.Services.GetTemporalAgentProxy("TTLAgent");
            var session = (TemporalAgentSession)await proxy.CreateSessionAsync();

            // Send one message to start the workflow.
            await proxy.RunAsync("Before TTL", session);

            var handle = _fixture.Client.GetWorkflowHandle<AgentWorkflow>(
                session.SessionId.WorkflowId);

            // Wait for the workflow to complete via TTL.
            await handle.GetResultAsync();

            // Send another request to the same session — should start a fresh workflow.
            // With IdReusePolicy.AllowDuplicate, a new run is allowed after completion.
            var r2 = await proxy.RunAsync("After TTL", session);

            // Fresh workflow has no prior history — turn count resets to 1.
            Assert.Contains("Echo [1]: After TTL", r2.Messages[0].Text);

            _output.WriteLine(
                "Fresh workflow started after TTL expiry; turn count reset to 1.");
        }
        finally
        {
            await host.StopAsync();
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    /// <summary>Builds a host targeting a specific task queue.</summary>
    private IHost BuildHostForTaskQueue(string taskQueue)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<ITemporalClient>(_fixture.Client);
        builder.Services.ConfigureTemporalAgents(
            configure: options => options.AddAIAgent(
                new Helpers.EchoAIAgent("EchoAgent")),
            taskQueue: taskQueue);
        return builder.Build();
    }
}
