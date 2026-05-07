using FakeItEasy;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Temporalio.Client;
using Temporalio.Extensions.Agents.HistoryStore;
using Temporalio.Extensions.Agents.Tests.Helpers;
using Temporalio.Extensions.Agents.Workflows;
using Temporalio.Extensions.Hosting;
using Xunit;

namespace Temporalio.Extensions.Agents.Tests;

/// <summary>
/// Coverage for the v0.3 <see cref="IAgentHistoryStore"/> integration via
/// <see cref="TemporalAgentsOptions.HistoryStore"/> and
/// <see cref="DurableAgentBuilder.HistoryStore"/>.
/// </summary>
public class AgentHistoryStoreTests
{
    [Fact]
    public void TemporalAgentsOptions_HistoryStore_DefaultsToNull()
    {
        var options = new TemporalAgentsOptions();
        Assert.Null(options.HistoryStore);
    }

    [Fact]
    public void DurableAgentBuilder_HistoryStore_DefaultsToNull()
    {
        var options = new TemporalAgentsOptions();
        options.AddDurableAgent("agent", agent =>
        {
            agent.ChatClient = _ => A.Fake<IChatClient>();
        });

        Assert.Null(options.DurableAgentRegistrations["agent"].HistoryStore);
    }

    [Fact]
    public void WorkerHistoryStore_FlowsToWorkflowInputAsExternalStoreMode()
    {
        var options = new TemporalAgentsOptions
        {
            HistoryStore = _ => new FakeHistoryStore(),
        };
        options.AddDurableAgent("agent", agent =>
        {
            agent.ChatClient = _ => A.Fake<IChatClient>();
        });

        var input = DefaultTemporalAgentClient.BuildAgentWorkflowInputCore("agent", options, "test-task-queue");

        Assert.True(input.UseExternalStoreMode);
    }

    [Fact]
    public void PerAgentHistoryStore_OverridesWorkerDefault()
    {
        var options = new TemporalAgentsOptions();
        options.AddDurableAgent("agent", agent =>
        {
            agent.ChatClient = _ => A.Fake<IChatClient>();
            agent.HistoryStore = _ => new FakeHistoryStore();
        });

        var input = DefaultTemporalAgentClient.BuildAgentWorkflowInputCore("agent", options, "test-task-queue");

        Assert.True(input.UseExternalStoreMode);
    }

    [Fact]
    public void NoHistoryStore_FlowsAsInWorkflowMode()
    {
        var options = new TemporalAgentsOptions();
        options.AddDurableAgent("agent", agent =>
        {
            agent.ChatClient = _ => A.Fake<IChatClient>();
        });

        var input = DefaultTemporalAgentClient.BuildAgentWorkflowInputCore("agent", options, "test-task-queue");

        Assert.False(input.UseExternalStoreMode);
    }

    [Fact]
    public void AddTemporalAgents_RegistersOptions()
    {
        var services = new ServiceCollection();
        services.AddSingleton(A.Fake<ITemporalClient>());
        var builder = services.AddHostedTemporalWorker("test-task-queue");

        builder.AddTemporalAgents(opts =>
        {
            opts.HistoryStore = _ => new FakeHistoryStore();
            opts.AddDurableAgent("agent", agent =>
            {
                agent.ChatClient = _ => A.Fake<IChatClient>();
            });
        });

        var provider = services.BuildServiceProvider();
        var resolvedOpts = provider.GetRequiredService<TemporalAgentsOptions>();
        Assert.NotNull(resolvedOpts.HistoryStore);
    }

    /// <summary>
    /// Hand-written stub <see cref="IAgentHistoryStore"/>.
    /// </summary>
    private sealed class FakeHistoryStore : IAgentHistoryStore
    {
        public Task<IReadOnlyList<Temporalio.Extensions.AI.DurableSessionEntry>> LoadAsync(
            string sessionId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Temporalio.Extensions.AI.DurableSessionEntry>>([]);

        public Task AppendAsync(
            string sessionId,
            IReadOnlyList<Temporalio.Extensions.AI.DurableSessionEntry> entries,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task ReplaceAsync(
            string sessionId,
            IReadOnlyList<Temporalio.Extensions.AI.DurableSessionEntry> reducedEntries,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
