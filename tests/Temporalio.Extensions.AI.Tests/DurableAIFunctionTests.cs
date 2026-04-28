using FakeItEasy;
using Microsoft.Extensions.AI;
using Xunit;

namespace Temporalio.Extensions.AI.Tests;

public class DurableAIFunctionTests
{
    [Fact]
    public async Task InvokeAsync_PassesThroughWhenNotInWorkflow()
    {
        var innerFunction = AIFunctionFactory.Create(() => "result", "TestFunc");
        var durableFunc = new DurableAIFunction(innerFunction);

        var result = await durableFunc.InvokeAsync();
        Assert.Equal("result", result?.ToString());
    }

    [Fact]
    public void AsDurable_PreservesNameAndDescription()
    {
        var innerFunction = AIFunctionFactory.Create(
            () => 42,
            "MyFunction",
            "Does something cool");

        var durable = innerFunction.AsDurable();

        Assert.Equal("MyFunction", durable.Name);
        Assert.Equal("Does something cool", durable.Description);
    }

    [Fact]
    public void AsDurable_ThrowsOnNull()
    {
        AIFunction? func = null;
        Assert.Throws<ArgumentNullException>(() => func!.AsDurable());
    }

    [Fact]
    public async Task InvokeAsync_WithArguments_PassesThroughWhenNotInWorkflow()
    {
        var innerFunction = AIFunctionFactory.Create(
            (string name) => $"Hello, {name}!",
            "Greet");

        var durable = innerFunction.AsDurable();

        var args = new AIFunctionArguments(new Dictionary<string, object?> { ["name"] = "World" });
        var result = await durable.InvokeAsync(args);
        Assert.Equal("Hello, World!", result?.ToString());
    }

    // ── Activity Summary (visible in Temporal Web UI activity list) ────────

    [Fact]
    public void BuildActivitySummary_ReturnsFunctionName_WhenSet() =>
        Assert.Equal("GetWeather", DurableAIFunction.BuildActivitySummary("GetWeather"));

    [Fact]
    public void BuildActivitySummary_ReturnsNull_WhenNameMissing()
    {
        Assert.Null(DurableAIFunction.BuildActivitySummary(null));
        Assert.Null(DurableAIFunction.BuildActivitySummary(""));
        Assert.Null(DurableAIFunction.BuildActivitySummary("   "));
    }
}
