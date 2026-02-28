using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;

namespace Temporalio.Extensions.Agents.State;

internal sealed class TemporalAgentStateUsageContent : TemporalAgentStateContent
{
    [JsonPropertyName("usage")]
    public TemporalAgentStateUsage Usage { get; init; } = new();

    public static TemporalAgentStateUsageContent FromUsageContent(UsageContent content) =>
        new() { Usage = TemporalAgentStateUsage.FromUsage(content.Details) ?? new() };

    public override AIContent ToAIContent() => new UsageContent(this.Usage.ToUsageDetails());
}
