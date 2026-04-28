using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;

namespace Temporalio.Extensions.AI;

/// <summary>
/// Source-generated JSON serialization context for durable AI types.
/// Uses <see cref="AIJsonUtilities.DefaultOptions"/> as the base to correctly
/// handle <see cref="AIContent"/> polymorphism (TextContent, FunctionCallContent, etc.).
/// </summary>
[ExcludeFromCodeCoverage(Justification = "Source-generated serialization context — no hand-written logic to measure.")]
[JsonSerializable(typeof(DurableChatInput))]
[JsonSerializable(typeof(DurableChatOutput))]
[JsonSerializable(typeof(DurableFunctionInput))]
[JsonSerializable(typeof(DurableFunctionOutput))]
[JsonSerializable(typeof(DurableChatWorkflowInput))]
[JsonSerializable(typeof(DurableApprovalRequest))]
[JsonSerializable(typeof(DurableApprovalDecision))]
[JsonSerializable(typeof(DurableEmbeddingInput))]
[JsonSerializable(typeof(DurableEmbeddingOutput))]
[JsonSerializable(typeof(ChatMessage))]
[JsonSerializable(typeof(ChatResponse))]
[JsonSerializable(typeof(ChatOptions))]
[JsonSerializable(typeof(IList<ChatMessage>))]
[JsonSerializable(typeof(List<ChatMessage>))]
[JsonSerializable(typeof(IDictionary<string, object?>))]
internal partial class DurableAIJsonContext : JsonSerializerContext;

/// <summary>
/// JSON serialization utilities for the durable AI library.
/// </summary>
[ExcludeFromCodeCoverage(Justification = "Boilerplate options wiring for the source-generated serialization context.")]
internal static class DurableAIJsonUtilities
{
    /// <summary>
    /// Gets the default <see cref="JsonSerializerOptions"/> configured with MEAI type support.
    /// This leverages <see cref="AIJsonUtilities.DefaultOptions"/> which handles
    /// <see cref="AIContent"/> polymorphism correctly.
    /// </summary>
    public static JsonSerializerOptions DefaultOptions { get; } = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        // Start from AIJsonUtilities.DefaultOptions which already handles
        // AIContent polymorphism (TextContent, FunctionCallContent, etc.)
        var options = new JsonSerializerOptions(AIJsonUtilities.DefaultOptions);
        options.TypeInfoResolverChain.Add(DurableAIJsonContext.Default);
        options.MakeReadOnly();
        return options;
    }
}
