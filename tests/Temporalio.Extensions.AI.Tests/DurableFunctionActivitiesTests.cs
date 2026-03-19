using Microsoft.Extensions.AI;
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
}
