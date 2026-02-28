using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;

namespace Temporalio.Extensions.Agents.State;

internal sealed class TemporalAgentStateUsage
{
    [JsonPropertyName("inputTokenCount")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? InputTokenCount { get; init; }

    [JsonPropertyName("outputTokenCount")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? OutputTokenCount { get; init; }

    [JsonPropertyName("totalTokenCount")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? TotalTokenCount { get; init; }

    [JsonExtensionData]
    public IDictionary<string, JsonElement>? ExtensionData { get; set; }

    [return: NotNullIfNotNull(nameof(usage))]
    public static TemporalAgentStateUsage? FromUsage(UsageDetails? usage) =>
        usage is not null
            ? new()
            {
                InputTokenCount = usage.InputTokenCount,
                OutputTokenCount = usage.OutputTokenCount,
                TotalTokenCount = usage.TotalTokenCount
            }
            : null;

    public UsageDetails ToUsageDetails()
    {
        return new UsageDetails
        {
            InputTokenCount = this.InputTokenCount,
            OutputTokenCount = this.OutputTokenCount,
            TotalTokenCount = this.TotalTokenCount
        };
    }
}
