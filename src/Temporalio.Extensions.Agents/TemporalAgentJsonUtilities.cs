using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.AI;
using Temporalio.Extensions.AI;
using Temporalio.Extensions.Agents.State;

namespace Temporalio.Extensions.Agents;

/// <summary>JSON serialization utilities for Temporal agent types.</summary>
public static class TemporalAgentJsonUtilities
{
    /// <summary>
    /// Default <see cref="JsonSerializerOptions"/> for Temporal agent serialization.
    /// Builds on <see cref="AIJsonUtilities.DefaultOptions"/> (which carries the MEAI
    /// <see cref="Microsoft.Extensions.AI.AIContent"/> polymorphism) and adds a runtime
    /// modifier that registers <see cref="AgentSessionRequest"/> /
    /// <see cref="AgentSessionResponse"/> as derived types of
    /// <see cref="Temporalio.Extensions.AI.DurableSessionEntry"/>.
    /// </summary>
    public static JsonSerializerOptions DefaultOptions { get; } = CreateDefaultOptions();

    private static JsonSerializerOptions CreateDefaultOptions()
    {
        var options = new JsonSerializerOptions(DurableAIJsonUtilities.DefaultOptions);
        options.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
        options.Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping;
        options.TypeInfoResolverChain.Add(AgentSessionJsonContext.Default);

        // Register MAF-specific derived types on DurableSessionEntry's polymorphism options
        // at runtime. The base class declares the AI-library discriminators
        // ("ai_request" / "ai_response") via [JsonDerivedType] attributes; this modifier
        // appends "agent_request" / "agent_response" so the workflow-history wire format
        // round-trips MAF entries with their agent-specific fields intact.
        options.TypeInfoResolver = options.TypeInfoResolver?.WithAddedModifier(AddAgentDerivedTypes);

        options.MakeReadOnly();
        return options;
    }

    private static void AddAgentDerivedTypes(JsonTypeInfo typeInfo)
    {
        if (typeInfo.Type != typeof(DurableSessionEntry))
        {
            return;
        }

        typeInfo.PolymorphismOptions ??= new JsonPolymorphismOptions
        {
            TypeDiscriminatorPropertyName = "$type",
        };

        typeInfo.PolymorphismOptions.DerivedTypes.Add(
            new JsonDerivedType(typeof(AgentSessionRequest), "agent_request"));
        typeInfo.PolymorphismOptions.DerivedTypes.Add(
            new JsonDerivedType(typeof(AgentSessionResponse), "agent_response"));
    }
}
