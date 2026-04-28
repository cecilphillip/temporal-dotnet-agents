using Microsoft.Extensions.AI;
using Temporalio.Testing;
using Xunit;

namespace Temporalio.Extensions.AI.Tests;

public class DurableFunctionActivitiesTests
{
    [Fact]
    public void Constructor_AcceptsEmptyRegistry()
    {
        var registry = new Dictionary<string, AIFunction>();
        var activities = new DurableFunctionActivities(registry, null);
        Assert.NotNull(activities);
    }

    [Fact]
    public async Task InvokeFunctionAsync_ThrowsInvalidOperationException_WhenFunctionNotInRegistry()
    {
        var registry = new Dictionary<string, AIFunction>(StringComparer.OrdinalIgnoreCase);
        var activities = new DurableFunctionActivities(registry, null);

        var input = new DurableFunctionInput
        {
            FunctionName = "nonexistent_tool",
        };

        var env = new ActivityEnvironment();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => env.RunAsync(() => activities.InvokeFunctionAsync(input)));

        Assert.Contains("nonexistent_tool", ex.Message);
    }

    [Fact]
    public async Task InvokeFunctionAsync_PropagatesException_WhenFunctionInvocationThrows()
    {
        // Use an explicit Func<string> to avoid delegate ambiguity with the throwing expression.
        Func<string> throwingDelegate = () => throw new InvalidOperationException("boom");
        var throwingFunction = AIFunctionFactory.Create(throwingDelegate, "throwing_tool");

        var registry = new Dictionary<string, AIFunction>(StringComparer.OrdinalIgnoreCase)
        {
            [throwingFunction.Name] = throwingFunction,
        };
        var activities = new DurableFunctionActivities(registry, null);

        var input = new DurableFunctionInput
        {
            FunctionName = "throwing_tool",
        };

        var env = new ActivityEnvironment();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => env.RunAsync(() => activities.InvokeFunctionAsync(input)));

        Assert.Equal("boom", ex.Message);
    }
}
