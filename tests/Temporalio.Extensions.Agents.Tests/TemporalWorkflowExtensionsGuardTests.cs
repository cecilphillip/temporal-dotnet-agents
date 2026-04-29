using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Temporalio.Extensions.Agents.Tests.Helpers;
using Xunit;

namespace Temporalio.Extensions.Agents.Tests;

/// <summary>
/// Pins the contract that every public method on <see cref="TemporalWorkflowExtensions"/>
/// fails fast with a clear <see cref="InvalidOperationException"/> when called outside a
/// Temporal workflow. The guards replace the previous <c>[EditorBrowsable(Never)]</c>
/// IntelliSense-hide trick with an actual runtime defense.
/// </summary>
public class TemporalWorkflowExtensionsGuardTests
{
    [Fact]
    public void GetAgent_OutsideWorkflow_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            TemporalWorkflowExtensions.GetAgent("WeatherAgent"));
        Assert.Contains("GetAgent", ex.Message, StringComparison.Ordinal);
        Assert.Contains("workflow", ex.Message, StringComparison.OrdinalIgnoreCase);
        // Surface the recommended alternative for external code so users know what to do.
        Assert.Contains("GetTemporalAgentProxy", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NewAgentSessionId_OutsideWorkflow_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            TemporalWorkflowExtensions.NewAgentSessionId("WeatherAgent"));
        Assert.Contains("NewAgentSessionId", ex.Message, StringComparison.Ordinal);
        // Surface the recommended alternative (TemporalAgentSessionId.WithRandomKey).
        Assert.Contains("WithRandomKey", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteAgentsInParallelAsync_OutsideWorkflow_Throws()
    {
        var stubAgent = new StubAIAgent("Stub");
        var temporalAgent = TryGetAgentInsideGuard();
        // We can't construct TemporalAIAgent outside a workflow either (it has its own
        // workflow guard now via GetAgent), so verify ExecuteAgentsInParallelAsync's guard
        // by passing an empty sequence — the guard fires before any iteration.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await TemporalWorkflowExtensions.ExecuteAgentsInParallelAsync(
                Array.Empty<(TemporalAIAgent, IList<ChatMessage>, AgentSession)>()));
        Assert.Contains("ExecuteAgentsInParallelAsync", ex.Message, StringComparison.Ordinal);
        Assert.Contains("workflow", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Helper that simply documents the fact that <see cref="TemporalWorkflowExtensions.GetAgent"/>
    /// itself throws outside workflow context — used to express intent in the test above.
    /// </summary>
    private static TemporalAIAgent? TryGetAgentInsideGuard() => null;
}
