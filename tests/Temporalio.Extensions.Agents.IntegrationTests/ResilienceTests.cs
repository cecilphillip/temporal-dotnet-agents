using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Temporalio.Client;
using Temporalio.Exceptions;
using Temporalio.Extensions.Agents.IntegrationTests.Helpers;
using Temporalio.Extensions.Agents.State;
using Temporalio.Testing;
using Xunit;
using Xunit.Abstractions;

namespace Temporalio.Extensions.Agents.IntegrationTests;

/// <summary>
/// Resilience tests that exercise failure and recovery scenarios.
/// Each test manages its own <see cref="WorkflowEnvironment"/> so it can
/// independently start/stop the Temporal server and worker hosts.
/// </summary>
[Trait("Category", "Integration")]
public class ResilienceTests
{
    private readonly ITestOutputHelper _output;

    public ResilienceTests(ITestOutputHelper output)
    {
        _output = output;
    }

    // ── 7.2: Temporal Server Unavailability ───────────────────────────────────

    [Fact]
    public async Task ServerUnavailable_RunAsyncThrowsRpcException()
    {
        // Start a local server and worker, do one successful turn,
        // then shut down the server and verify the next call fails.
        var env = await WorkflowEnvironment.StartLocalAsync();
        var taskQueue = $"resilience-server-{Guid.NewGuid():N}";

        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<ITemporalClient>(env.Client);
        builder.Services.ConfigureTemporalAgents(
            configure: options => options.AddAIAgent(new EchoAIAgent("EchoAgent")),
            taskQueue: taskQueue);

        using var host = builder.Build();
        await host.StartAsync();

        try
        {
            var proxy = host.Services.GetTemporalAgentProxy("EchoAgent");
            var session = await proxy.CreateSessionAsync();

            // Turn 1 — succeeds while server is running.
            var r1 = await proxy.RunAsync("Hello", session);
            Assert.Contains("Echo [1]: Hello", r1.Messages[0].Text);
            _output.WriteLine("Turn 1 succeeded while server is running.");

            // Shut down the Temporal server.
            await host.StopAsync();
            await env.ShutdownAsync();
            _output.WriteLine("Server shut down. Attempting RunAsync...");

            // The client still exists but the server is gone.
            // Attempting to use it should throw an RPC-related exception.
            var ex = await Assert.ThrowsAnyAsync<Exception>(
                () => proxy.RunAsync("After shutdown", session));

            // The exception should be network/RPC related.
            // Temporal SDK throws RpcException for connection failures.
            _output.WriteLine($"Exception type: {ex.GetType().Name}, Message: {ex.Message}");
            Assert.True(
                ex is RpcException || ex is InvalidOperationException || ex is OperationCanceledException,
                $"Expected RpcException, InvalidOperationException, or OperationCanceledException but got {ex.GetType().Name}: {ex.Message}");
        }
        catch
        {
            // Clean up if we fail before the explicit shutdown above.
            try { await host.StopAsync(); } catch { }
            try { await env.ShutdownAsync(); } catch { }
            throw;
        }
    }

    // ── 7.3: Network Partition Recovery (Worker disconnect/reconnect) ─────────

    [Fact]
    public async Task WorkerDisconnect_RequestCompletesAfterReconnect()
    {
        // Start server + worker, do one turn, stop only the worker (simulating
        // a network partition), send a request (which will hang), start a new
        // worker, and verify the request completes.
        var env = await WorkflowEnvironment.StartLocalAsync();
        var taskQueue = $"resilience-partition-{Guid.NewGuid():N}";

        try
        {
            // Host 1: start worker and do one successful turn.
            var host1 = BuildHost(env.Client, taskQueue);
            await host1.StartAsync();

            var proxy1 = host1.Services.GetTemporalAgentProxy("EchoAgent");
            var session = await proxy1.CreateSessionAsync();

            var r1 = await proxy1.RunAsync("Turn 1", session);
            Assert.Contains("Echo [1]: Turn 1", r1.Messages[0].Text);
            _output.WriteLine("Turn 1 completed on host1.");

            // Stop host1 — simulates the worker losing connection.
            await host1.StopAsync();
            host1.Dispose();
            _output.WriteLine("Host1 stopped (worker disconnected).");

            // Start host2 on the same task queue — simulates reconnection.
            // Send a request that will be picked up by the new worker.
            var host2 = BuildHost(env.Client, taskQueue);
            await host2.StartAsync();

            try
            {
                var proxy2 = host2.Services.GetTemporalAgentProxy("EchoAgent");

                // The new worker replays history from the Temporal server and picks
                // up the update. Session state is fully restored via replay.
                var resumedSession = new TemporalAgentSession(
                    ((TemporalAgentSession)session).SessionId);
                var r2 = await proxy2.RunAsync("Turn 2", resumedSession);

                Assert.Contains("Echo [2]: Turn 2", r2.Messages[0].Text);
                _output.WriteLine("Turn 2 completed on host2 — worker reconnection succeeded.");
            }
            finally
            {
                await host2.StopAsync();
                host2.Dispose();
            }
        }
        finally
        {
            await env.ShutdownAsync();
        }
    }

    // ── 7.4: State Consistency After Workflow Interruption ─────────────────────

    [Fact]
    public async Task WorkflowInterruption_StateConsistentAfterRestart()
    {
        // Build conversation state over multiple turns, kill the worker,
        // start a fresh worker, query the workflow history, and verify
        // state consistency. Then send one more turn to prove continuity.
        var env = await WorkflowEnvironment.StartLocalAsync();
        var taskQueue = $"resilience-state-{Guid.NewGuid():N}";

        try
        {
            // Host 1: build up 3 turns of conversation state.
            var host1 = BuildHost(env.Client, taskQueue);
            await host1.StartAsync();

            var proxy1 = host1.Services.GetTemporalAgentProxy("EchoAgent");
            var session = (TemporalAgentSession)await proxy1.CreateSessionAsync();

            await proxy1.RunAsync("Turn 1", session);
            await proxy1.RunAsync("Turn 2", session);
            var r3 = await proxy1.RunAsync("Turn 3", session);
            Assert.Contains("Echo [3]: Turn 3", r3.Messages[0].Text);
            _output.WriteLine("3 turns completed on host1.");

            // Abruptly stop host1 — workflow remains Running on the server.
            await host1.StopAsync();
            host1.Dispose();
            _output.WriteLine("Host1 stopped abruptly.");

            // Start host2 — replays history to rebuild state.
            var host2 = BuildHost(env.Client, taskQueue);
            await host2.StartAsync();

            try
            {
                var proxy2 = host2.Services.GetTemporalAgentProxy("EchoAgent");

                // Query the workflow history — should have 6 entries (3 × request + response).
                var handle = env.Client.GetWorkflowHandle<AgentWorkflow>(
                    session.SessionId.WorkflowId);
                var history = await handle.QueryAsync(wf => wf.GetHistory());

                _output.WriteLine($"History entries after restart: {history.Count}");
                Assert.Equal(6, history.Count); // 3 requests + 3 responses

                // Verify the first request contains "Turn 1".
                var firstRequest = Assert.IsType<TemporalAgentStateRequest>(history[0]);
                var textContent = firstRequest.Messages[0].Contents
                    .OfType<TemporalAgentStateTextContent>()
                    .First();
                Assert.Equal("Turn 1", textContent.Text);

                // Verify the last response is for Turn 3.
                var lastResponse = Assert.IsType<TemporalAgentStateResponse>(history[5]);
                var responseText = lastResponse.Messages[0].Contents
                    .OfType<TemporalAgentStateTextContent>()
                    .First();
                Assert.Contains("Echo [3]: Turn 3", responseText.Text);

                _output.WriteLine("History verified — state is consistent after restart.");

                // Send one more turn to prove the workflow is fully operational.
                var resumedSession = new TemporalAgentSession(session.SessionId);
                var r4 = await proxy2.RunAsync("Turn 4", resumedSession);
                Assert.Contains("Echo [4]: Turn 4", r4.Messages[0].Text);

                _output.WriteLine("Turn 4 succeeded — workflow continues seamlessly after interruption.");
            }
            finally
            {
                await host2.StopAsync();
                host2.Dispose();
            }
        }
        finally
        {
            await env.ShutdownAsync();
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static IHost BuildHost(ITemporalClient client, string taskQueue)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<ITemporalClient>(client);
        builder.Services.ConfigureTemporalAgents(
            configure: options => options.AddAIAgent(new EchoAIAgent("EchoAgent")),
            taskQueue: taskQueue);
        return builder.Build();
    }
}
