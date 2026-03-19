using FakeItEasy;
using Microsoft.Extensions.AI;
using Xunit;

namespace Temporalio.Extensions.AI.Tests;

public class DurableChatReducerTests
{
    [Fact]
    public void Constructor_ThrowsOnNull()
    {
        Assert.Throws<ArgumentNullException>(() => new DurableChatReducer(null!));
    }

    [Fact]
    public async Task ReduceAsync_DelegatesToInnerReducer()
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, "You are helpful."),
            new(ChatRole.User, "Message 1"),
            new(ChatRole.Assistant, "Response 1"),
            new(ChatRole.User, "Message 2"),
            new(ChatRole.Assistant, "Response 2"),
        };

        // Inner reducer returns only last 2 non-system messages + system.
        var reducedMessages = new List<ChatMessage>
        {
            messages[0], // system
            messages[3], // user 2
            messages[4], // assistant 2
        };

        var innerReducer = A.Fake<IChatReducer>();
        A.CallTo(() => innerReducer.ReduceAsync(
                A<IEnumerable<ChatMessage>>._, A<CancellationToken>._))
            .Returns(Task.FromResult<IEnumerable<ChatMessage>>(reducedMessages));

        var durableReducer = new DurableChatReducer(innerReducer);

        // When not in workflow, it passes through to inner reducer.
        var result = (await durableReducer.ReduceAsync(messages, CancellationToken.None)).ToList();

        Assert.Equal(3, result.Count);
        A.CallTo(() => innerReducer.ReduceAsync(
                A<IEnumerable<ChatMessage>>._, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task ReduceAsync_OutsideWorkflow_FullHistoryIsEmpty()
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "Hello"),
            new(ChatRole.Assistant, "Hi"),
        };

        var innerReducer = A.Fake<IChatReducer>();
        A.CallTo(() => innerReducer.ReduceAsync(
                A<IEnumerable<ChatMessage>>._, A<CancellationToken>._))
            .Returns(Task.FromResult<IEnumerable<ChatMessage>>(messages));

        var durableReducer = new DurableChatReducer(innerReducer);
        await durableReducer.ReduceAsync(messages, CancellationToken.None);

        // Outside a workflow, FullHistory should remain empty.
        Assert.Empty(durableReducer.FullHistory);
    }

    [Fact]
    public void FullHistory_InitiallyEmpty()
    {
        var innerReducer = A.Fake<IChatReducer>();
        var durableReducer = new DurableChatReducer(innerReducer);
        Assert.Empty(durableReducer.FullHistory);
    }

    [Fact]
    public void UseDurableReduction_ThrowsOnNullBuilder()
    {
        var reducer = A.Fake<IChatReducer>();
        Assert.Throws<ArgumentNullException>(
            () => ChatClientBuilderExtensions.UseDurableReduction(null!, reducer));
    }

    [Fact]
    public void UseDurableReduction_ThrowsOnNullReducer()
    {
        var innerClient = A.Fake<IChatClient>();
        var builder = new ChatClientBuilder(innerClient);
        Assert.Throws<ArgumentNullException>(
            () => builder.UseDurableReduction(null!));
    }

    [Fact]
    public void UseDurableReduction_AddsToPipeline()
    {
        var innerClient = A.Fake<IChatClient>();
        var innerReducer = new MessageCountingChatReducer(10);

        var builder = new ChatClientBuilder(innerClient);
        builder.UseDurableReduction(innerReducer);
        var pipeline = builder.Build();

        // Pipeline should build without error.
        Assert.NotNull(pipeline);
    }
}
