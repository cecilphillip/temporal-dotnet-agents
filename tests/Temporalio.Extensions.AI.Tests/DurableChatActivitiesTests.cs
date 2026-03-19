using FakeItEasy;
using Microsoft.Extensions.AI;
using Xunit;

namespace Temporalio.Extensions.AI.Tests;

public class DurableChatActivitiesTests
{
    [Fact]
    public void Constructor_AcceptsNullLoggerFactory()
    {
        var chatClient = A.Fake<IChatClient>();
        var activities = new DurableChatActivities(chatClient, null);
        Assert.NotNull(activities);
    }
}
