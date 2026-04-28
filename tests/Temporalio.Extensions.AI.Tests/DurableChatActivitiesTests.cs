using FakeItEasy;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Temporalio.Extensions.AI.Tests;

public class DurableChatActivitiesTests
{
    [Fact]
    public void Constructor_AcceptsNullLoggerFactory()
    {
        var chatClient = A.Fake<IChatClient>();
        var services = new ServiceCollection()
            .AddSingleton<IChatClient>(chatClient)
            .BuildServiceProvider();
        var activities = new DurableChatActivities(services, null);
        Assert.NotNull(activities);
    }
}
