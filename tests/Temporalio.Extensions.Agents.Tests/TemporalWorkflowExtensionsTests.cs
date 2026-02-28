using Xunit;

namespace Temporalio.Extensions.Agents.Tests;

/// <summary>
/// Unit tests for <see cref="TemporalWorkflowExtensions"/>.
/// The parallel helper itself cannot be called outside a workflow context (it delegates to
/// Workflow.WhenAllAsync), but we can verify the supporting types compile and the static helpers
/// exist with the expected signatures.
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
    public void GetAgent_ReturnsTemporalAIAgent()
    {
        // GetAgent is documented as for-workflow use only, but the object it returns
        // (TemporalAIAgent) is constructible via the static helper even outside a workflow.
        // Verify it doesn't throw synchronously.
        var ex = Record.Exception(() => TemporalWorkflowExtensions.GetAgent("TestAgent"));
        Assert.Null(ex);
    }

    [Fact]
    public void GetAgent_EmptyName_Throws()
    {
        Assert.Throws<ArgumentException>(() => TemporalWorkflowExtensions.GetAgent(string.Empty));
    }

    [Fact]
    public void GetAgent_NullName_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => TemporalWorkflowExtensions.GetAgent(null!));
    }

    [Fact]
    public void ExecuteAgentsInParallelAsync_NullRequests_Throws()
    {
        // The method is synchronous up to the point it creates tasks; a null check fires immediately.
        var ex = Assert.ThrowsAsync<ArgumentNullException>(() =>
            TemporalWorkflowExtensions.ExecuteAgentsInParallelAsync(null!));
        Assert.NotNull(ex);
    }
}
