using FakeItEasy;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Temporalio.Client;
using Temporalio.Extensions.AI;
using Temporalio.Extensions.Agents.Tests.Helpers;
using Temporalio.Extensions.Hosting;
using Xunit;

namespace Temporalio.Extensions.Agents.Tests;

/// <summary>
/// Startup-time validation tests for <c>TemporalAgentsRegistrar</c>.
/// Mirrors the validation pattern used for <c>UseExternalHistory</c>: misconfigured
/// workers should fail loudly at composition time, not silently at the first turn.
/// </summary>
public class TemporalAgentsRegistrarValidationTests
{
    [Fact]
    public void Validate_PerToolEnabled_NoDurableFunctionActivities_Throws()
    {
        // Step mode (EnablePerToolActivities = true) requires DurableFunctionActivities to be
        // registered on the same worker (via AddDurableAI). Without it, the workflow's
        // step-mode loop has nowhere to dispatch InvokeFunctionAsync. Surface this as an
        // InvalidOperationException at composition time with a message that points the user
        // at AddDurableAI / AddDurableTools.
        var services = new ServiceCollection();
        services.AddSingleton(A.Fake<ITemporalClient>());
        var builder = services.AddHostedTemporalWorker("test-task-queue");

        var ex = Assert.Throws<InvalidOperationException>(() =>
            builder.AddTemporalAgents(opts =>
            {
                opts.EnablePerToolActivities = true;
                opts.AddAIAgent(new StubAIAgent("Agent"));
            }));

        Assert.Contains("EnablePerToolActivities", ex.Message, StringComparison.Ordinal);
        Assert.Contains("AddDurableAI", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_PerToolEnabled_DurableAIRegistered_Succeeds()
    {
        // Sanity check the positive case: when AddDurableAI is called first,
        // EnablePerToolActivities = true composes cleanly.
        var services = new ServiceCollection();
        services.AddSingleton(A.Fake<ITemporalClient>());
        // Required so the IChatClient constructor-resolution inside DurableChatActivities
        // does not blow up later (we never actually run the worker here).
        services.AddSingleton(A.Fake<IChatClient>());

        var builder = services.AddHostedTemporalWorker("test-task-queue");

        // No throw expected.
        builder
            .AddDurableAI()
            .AddTemporalAgents(opts =>
            {
                opts.EnablePerToolActivities = true;
                opts.AddAIAgent(new StubAIAgent("Agent"));
            });
    }

    [Fact]
    public void Validate_PerToolDisabled_NoDurableAI_Succeeds()
    {
        // Default (EnablePerToolActivities = false) does not require AddDurableAI;
        // existing single-activity behavior is preserved.
        var services = new ServiceCollection();
        services.AddSingleton(A.Fake<ITemporalClient>());
        var builder = services.AddHostedTemporalWorker("test-task-queue");

        // No throw expected.
        builder.AddTemporalAgents(opts => opts.AddAIAgent(new StubAIAgent("Agent")));
    }
}
