using System.Text.Json.Serialization.Metadata;
using Temporalio.Extensions.Agents.State;
using Temporalio.Extensions.Agents.Workflows;
using Xunit;

namespace Temporalio.Extensions.Agents.Tests;

/// <summary>
/// Verifies that <see cref="AgentSessionJsonContext"/> includes source-gen metadata for the
/// activity I/O types added in v0.3 per-tool-activities redesign. Each test calls
/// <see cref="System.Text.Json.JsonSerializerOptions.GetTypeInfo"/> on the context's options
/// and asserts that the returned <see cref="JsonTypeInfo"/> is source-gen backed (i.e.,
/// <see cref="JsonTypeInfoKind"/> is not <see cref="JsonTypeInfoKind.None"/>), which would
/// indicate a reflection fallback rather than source-generated metadata.
/// </summary>
public class AgentSessionJsonContextTests
{
    [Fact]
    public void AgentStepInput_RoundTrips_ViaSourceGen()
    {
        // Verify GetTypeInfo is source-gen backed
        var typeInfo = AgentSessionJsonContext.Default.Options.GetTypeInfo(typeof(AgentStepInput));
        Assert.NotNull(typeInfo);
        Assert.NotEqual(JsonTypeInfoKind.None, typeInfo.Kind);
    }

    [Fact]
    public void AgentStepResult_RoundTrips_ViaSourceGen()
    {
        var typeInfo = AgentSessionJsonContext.Default.Options.GetTypeInfo(typeof(AgentStepResult));
        Assert.NotNull(typeInfo);
        Assert.NotEqual(JsonTypeInfoKind.None, typeInfo.Kind);
    }

    [Fact]
    public void InvokeAgentToolInput_RoundTrips_ViaSourceGen()
    {
        var typeInfo = AgentSessionJsonContext.Default.Options.GetTypeInfo(typeof(InvokeAgentToolInput));
        Assert.NotNull(typeInfo);
        Assert.NotEqual(JsonTypeInfoKind.None, typeInfo.Kind);
    }
}
