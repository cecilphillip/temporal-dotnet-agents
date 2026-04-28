using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Temporalio.Extensions.Hosting;
using Xunit;

namespace Temporalio.Extensions.AI.Tests;

public class DurableAIServiceCollectionExtensionsTests
{
    [Fact]
    public void AddDurableTools_ThrowsOnNullBuilder()
    {
        Assert.Throws<ArgumentNullException>(
            () => DurableAIServiceCollectionExtensions.AddDurableTools(null!));
    }

    [Fact]
    public void AddDurableTools_ThrowsInvalidOperation_WhenAddDurableAINotCalled()
    {
        var services = new ServiceCollection();
        var workerBuilder = services.AddHostedTemporalWorker("my-queue");
        var tool = AIFunctionFactory.Create(() => "ok", "my_tool");

        var ex = Assert.Throws<InvalidOperationException>(
            () => workerBuilder.AddDurableTools(tool));

        Assert.Equal(
            "AddDurableTools requires AddDurableAI to be called first on the same worker builder.",
            ex.Message);
    }

    [Fact]
    public void AddDurableTools_Succeeds_WhenAddDurableAICalledFirst()
    {
        var services = new ServiceCollection();
        var workerBuilder = services
            .AddHostedTemporalWorker("my-queue")
            .AddDurableAI();
        var tool = AIFunctionFactory.Create(() => "ok", "my_tool");

        var returned = workerBuilder.AddDurableTools(tool);

        Assert.Same(workerBuilder, returned);

        // Verify the registry resolves and contains the tool.
        var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<DurableFunctionRegistry>();
        Assert.True(registry.ContainsKey("my_tool"));
    }
}
