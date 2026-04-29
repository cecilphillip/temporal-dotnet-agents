using Xunit;

namespace Temporalio.Extensions.Agents.Tests;

/// <summary>
/// Unit tests for <see cref="TemporalWorkflowExtensions"/>.
/// The methods on this type require a Temporal workflow context — we can verify the supporting
/// types compile and the static helpers exist with the expected signatures, but we cannot
/// invoke them here. <see cref="TemporalWorkflowExtensionsGuardTests"/> pins the
/// outside-workflow guard behavior.
/// </summary>
public class TemporalWorkflowExtensionsTests
{
    [Fact]
    public void ExecuteAgentsInParallelAsync_IsPublicStaticMethod()
    {
        // Verify the method exists with the expected signature so callers can discover it.
        var method = typeof(TemporalWorkflowExtensions).GetMethod(
            nameof(TemporalWorkflowExtensions.ExecuteAgentsInParallelAsync));

        Assert.NotNull(method);
        Assert.True(method.IsStatic);
        Assert.True(method.IsPublic);
    }

    [Fact]
    public void GetAgent_IsPublicStaticMethod()
    {
        // GetAgent is now guarded with a runtime workflow-context check, so we can't invoke
        // it from a unit test. Verify the surface remains discoverable via reflection.
        var method = typeof(TemporalWorkflowExtensions).GetMethod(
            nameof(TemporalWorkflowExtensions.GetAgent));

        Assert.NotNull(method);
        Assert.True(method.IsStatic);
        Assert.True(method.IsPublic);
    }

    [Fact]
    public void NewAgentSessionId_IsPublicStaticMethod()
    {
        var method = typeof(TemporalWorkflowExtensions).GetMethod(
            nameof(TemporalWorkflowExtensions.NewAgentSessionId));

        Assert.NotNull(method);
        Assert.True(method.IsStatic);
        Assert.True(method.IsPublic);
    }
}
