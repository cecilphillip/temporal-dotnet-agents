using Temporalio.Extensions.Agents.Workflows;
using Xunit;

namespace Temporalio.Extensions.Agents.Tests;

/// <summary>
/// Pins the contract for <see cref="AgentActivities.BuildActivitySummary"/> — the helper that
/// populates <c>ActivityOptions.Summary</c> at every dispatch site so the Temporal Web UI shows
/// the agent name in the activity list.
/// </summary>
public class AgentActivitiesBuildActivitySummaryTests
{
    [Fact]
    public void ReturnsAgentName_WhenSet()
    {
        Assert.Equal("WeatherAgent", AgentActivities.BuildActivitySummary("WeatherAgent"));
    }

    [Fact]
    public void ReturnsNull_WhenNull()
    {
        Assert.Null(AgentActivities.BuildActivitySummary(null));
    }

    [Fact]
    public void ReturnsNull_WhenEmpty()
    {
        Assert.Null(AgentActivities.BuildActivitySummary(string.Empty));
    }

    [Fact]
    public void ReturnsNull_WhenWhitespace()
    {
        Assert.Null(AgentActivities.BuildActivitySummary("   "));
    }
}
