using FakeItEasy;
using Microsoft.Extensions.AI;
using Xunit;

namespace Temporalio.Extensions.AI.Tests;

public class ChatClientBuilderExtensionsTests
{
    [Fact]
    public void UseDurableExecution_ThrowsOnNullBuilder()
    {
        Assert.Throws<ArgumentNullException>(
            () => ChatClientBuilderExtensions.UseDurableExecution(null!));
    }

    [Fact]
    public void UseDurableExecution_CreatesDurableChatClientInPipeline()
    {
        var innerClient = A.Fake<IChatClient>();
        var builder = new ChatClientBuilder(innerClient);

        builder.UseDurableExecution(opts => opts.TaskQueue = "test-queue");
        var pipeline = builder.Build();

        // The outermost client should be a DurableChatClient.
        var durableOptions = pipeline.GetService<DurableExecutionOptions>();
        Assert.NotNull(durableOptions);
        Assert.Equal("test-queue", durableOptions!.TaskQueue);
    }

    [Fact]
    public void UseDurableExecution_WorksWithNullConfigure()
    {
        var innerClient = A.Fake<IChatClient>();
        var builder = new ChatClientBuilder(innerClient);

        builder.UseDurableExecution();
        var pipeline = builder.Build();

        var durableOptions = pipeline.GetService<DurableExecutionOptions>();
        Assert.NotNull(durableOptions);
    }
}
