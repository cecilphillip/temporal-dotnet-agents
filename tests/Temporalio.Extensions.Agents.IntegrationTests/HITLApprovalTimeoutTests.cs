using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Temporalio.Api.Enums.V1;
using Temporalio.Client;
using Temporalio.Extensions.Agents.IntegrationTests.Helpers;
using Temporalio.Extensions.Agents.Session;
using Temporalio.Extensions.Agents.Workflows;
using Temporalio.Extensions.AI;
using Temporalio.Extensions.Hosting;
using Xunit;
using Xunit.Abstractions;

namespace Temporalio.Extensions.Agents.IntegrationTests;

/// <summary>
/// Tests that the HITL approval timeout feature works correctly end-to-end.
/// When a human never responds to an approval request, the workflow should
/// return a rejected <see cref="DurableApprovalDecision"/> after the configured timeout.
/// </summary>
[Trait("Category", "Integration")]
public class HITLApprovalTimeoutTests : IClassFixture<IntegrationTestFixture>
{
    private readonly IntegrationTestFixture _fixture;
    private readonly ITestOutputHelper _output;

    public HITLApprovalTimeoutTests(IntegrationTestFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    [Fact]
    public async Task RequestApproval_TimesOut_ReturnsRejectedTicket()
    {
        // Arrange: spin up a worker with a very short approval timeout (2 seconds).
        var taskQueue = $"hitl-timeout-{Guid.NewGuid():N}";
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<ITemporalClient>(_fixture.Client);

        builder.Services
            .AddHostedTemporalWorker(taskQueue)
            .AddTemporalAgents(options =>
            {
                options.AddDurableAgent("HITLAgent", a => a.ChatClient = _ => new Helpers.EchoChatClient());
                options.DefaultApprovalTimeout = TimeSpan.FromSeconds(2);
            });

        using var host = builder.Build();
        await host.StartAsync();

        try
        {
            // Start the agent workflow so it's running and can accept updates.
            var proxy = host.Services.GetTemporalAgentProxy("HITLAgent");
            var session = (TemporalAgentSession)await proxy.CreateSessionAsync();
            var response = await proxy.RunAsync("Hello", session);
            Assert.NotNull(response);

            // Act: send a RequestApproval update directly to the workflow.
            // This simulates what TemporalAgentContext.RequestApprovalAsync does from inside a tool.
            var handle = _fixture.Client.GetWorkflowHandle<AgentWorkflow>(session.SessionId.WorkflowId);
            var approvalRequest = new DurableApprovalRequest
            {
                RequestId = Guid.NewGuid().ToString("N"),
                Description = "Delete all records — This should time out."
            };

            // This call blocks until the approval decision arrives or the timeout elapses.
            var decision = await handle.ExecuteUpdateAsync<AgentWorkflow, DurableApprovalDecision>(
                wf => wf.RequestApprovalAsync(approvalRequest));

            // Assert: the decision should be rejected with a timeout message.
            Assert.False(decision.Approved);
            Assert.Equal(approvalRequest.RequestId, decision.RequestId);
            Assert.NotNull(decision.Reason);
            Assert.Contains("timed out", decision.Reason, StringComparison.OrdinalIgnoreCase);

            _output.WriteLine($"Approval timed out as expected: {decision.Reason}");

            // Verify the pending approval was cleared.
            var pending = await handle.QueryAsync<AgentWorkflow, DurableApprovalRequest?>(
                wf => wf.GetPendingApproval());
            Assert.Null(pending);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task SubmitApproval_BeforeTimeout_ReturnsApprovedTicket()
    {
        // Verify the happy path still works: approval submitted before timeout elapses.
        var taskQueue = $"hitl-approve-{Guid.NewGuid():N}";
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<ITemporalClient>(_fixture.Client);

        builder.Services
            .AddHostedTemporalWorker(taskQueue)
            .AddTemporalAgents(options =>
            {
                options.AddDurableAgent("HITLApproveAgent", a => a.ChatClient = _ => new Helpers.EchoChatClient());
                options.DefaultApprovalTimeout = TimeSpan.FromMinutes(5);
            });

        using var host = builder.Build();
        await host.StartAsync();

        try
        {
            var proxy = host.Services.GetTemporalAgentProxy("HITLApproveAgent");
            var session = (TemporalAgentSession)await proxy.CreateSessionAsync();
            await proxy.RunAsync("Hello", session);

            var handle = _fixture.Client.GetWorkflowHandle<AgentWorkflow>(session.SessionId.WorkflowId);
            var approvalRequest = new DurableApprovalRequest
            {
                RequestId = Guid.NewGuid().ToString("N"),
                Description = "Send email — Should be approved promptly."
            };

            // Start the approval update in the background (it will block until decision).
            var approvalTask = handle.ExecuteUpdateAsync<AgentWorkflow, DurableApprovalDecision>(
                wf => wf.RequestApprovalAsync(approvalRequest));

            // Wait briefly for the workflow to register the pending approval.
            await Task.Delay(TimeSpan.FromMilliseconds(500));

            // Submit the approval decision.
            await handle.ExecuteUpdateAsync<AgentWorkflow, DurableApprovalDecision>(
                wf => wf.SubmitApprovalAsync(new DurableApprovalDecision
                {
                    RequestId = approvalRequest.RequestId,
                    Approved = true,
                    Reason = "Looks good!"
                }));

            var decision = await approvalTask;

            Assert.True(decision.Approved);
            Assert.Equal(approvalRequest.RequestId, decision.RequestId);
            Assert.Equal("Looks good!", decision.Reason);

            _output.WriteLine("Approval submitted and received correctly before timeout.");
        }
        finally
        {
            await host.StopAsync();
        }
    }
}
