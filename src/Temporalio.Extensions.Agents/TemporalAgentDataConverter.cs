using Temporalio.Converters;

namespace Temporalio.Extensions.Agents;

/// <summary>
/// Provides a <see cref="DataConverter"/> that handles MEAI <c>AIContent</c> polymorphism
/// (inherited from <see cref="Temporalio.Extensions.AI.DurableAIDataConverter"/>) AND
/// the MAF-specific session-entry subclasses
/// (<see cref="State.AgentSessionRequest"/> / <see cref="State.AgentSessionResponse"/>).
/// </summary>
/// <remarks>
/// <para>
/// This converter must be set on the Temporal client when using <c>Temporalio.Extensions.Agents</c>;
/// otherwise the workflow history's <c>$type</c> discriminator drops to the AI-library defaults
/// and MAF entries lose their agent-specific fields after replay.
/// </para>
/// <para>
/// <c>AddTemporalAgents</c> auto-wires this converter for the standard hosted-worker and
/// <c>AddTemporalClient</c> registration paths. Users who construct a client manually should
/// set <c>DataConverter = TemporalAgentDataConverter.Instance</c> explicitly.
/// </para>
/// </remarks>
public static class TemporalAgentDataConverter
{
    /// <summary>
    /// A <see cref="DataConverter"/> whose JSON serializer uses
    /// <see cref="TemporalAgentJsonUtilities.DefaultOptions"/>.
    /// </summary>
    public static DataConverter Instance { get; } = new(
        new DefaultPayloadConverter(TemporalAgentJsonUtilities.DefaultOptions),
        new DefaultFailureConverter());
}
