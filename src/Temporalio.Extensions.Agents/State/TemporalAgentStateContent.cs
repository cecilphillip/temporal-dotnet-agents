using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;

namespace Temporalio.Extensions.Agents.State;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(TemporalAgentStateDataContent), "data")]
[JsonDerivedType(typeof(TemporalAgentStateErrorContent), "error")]
[JsonDerivedType(typeof(TemporalAgentStateFunctionCallContent), "functionCall")]
[JsonDerivedType(typeof(TemporalAgentStateFunctionResultContent), "functionResult")]
[JsonDerivedType(typeof(TemporalAgentStateHostedFileContent), "hostedFile")]
[JsonDerivedType(typeof(TemporalAgentStateHostedVectorStoreContent), "hostedVectorStore")]
[JsonDerivedType(typeof(TemporalAgentStateTextContent), "text")]
[JsonDerivedType(typeof(TemporalAgentStateTextReasoningContent), "reasoning")]
[JsonDerivedType(typeof(TemporalAgentStateUriContent), "uri")]
[JsonDerivedType(typeof(TemporalAgentStateUsageContent), "usage")]
[JsonDerivedType(typeof(TemporalAgentStateUnknownContent), "unknown")]
internal abstract class TemporalAgentStateContent
{
    [JsonExtensionData]
    public IDictionary<string, JsonElement>? ExtensionData { get; set; }

    public abstract AIContent ToAIContent();

    public static TemporalAgentStateContent FromAIContent(AIContent content)
    {
        return content switch
        {
            DataContent dataContent => TemporalAgentStateDataContent.FromDataContent(dataContent),
            ErrorContent errorContent => TemporalAgentStateErrorContent.FromErrorContent(errorContent),
            FunctionCallContent functionCallContent => TemporalAgentStateFunctionCallContent.FromFunctionCallContent(functionCallContent),
            FunctionResultContent functionResultContent => TemporalAgentStateFunctionResultContent.FromFunctionResultContent(functionResultContent),
            HostedFileContent hostedFileContent => TemporalAgentStateHostedFileContent.FromHostedFileContent(hostedFileContent),
            HostedVectorStoreContent hostedVectorStoreContent => TemporalAgentStateHostedVectorStoreContent.FromHostedVectorStoreContent(hostedVectorStoreContent),
            TextContent textContent => TemporalAgentStateTextContent.FromTextContent(textContent),
            TextReasoningContent textReasoningContent => TemporalAgentStateTextReasoningContent.FromTextReasoningContent(textReasoningContent),
            UriContent uriContent => TemporalAgentStateUriContent.FromUriContent(uriContent),
            UsageContent usageContent => TemporalAgentStateUsageContent.FromUsageContent(usageContent),
            _ => TemporalAgentStateUnknownContent.FromUnknownContent(content)
        };
    }
}
