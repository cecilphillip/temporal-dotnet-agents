using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;
using Temporalio.Extensions.Agents.State;

namespace Temporalio.Extensions.Agents;

/// <summary>JSON serialization utilities for Temporal agent types.</summary>
internal static class TemporalAgentJsonUtilities
{
    public static JsonSerializerOptions DefaultOptions { get; } = CreateDefaultOptions();

    private static JsonSerializerOptions CreateDefaultOptions()
    {
        var options = new JsonSerializerOptions(AIJsonUtilities.DefaultOptions);
        options.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
        options.Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping;
        options.TypeInfoResolverChain.Add(TemporalAgentStateJsonContext.Default);

        options.MakeReadOnly();
        return options;
    }
}
