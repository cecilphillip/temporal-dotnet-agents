using System.Text.Json;
using Microsoft.Extensions.AI;
using Temporalio.Converters;

namespace Temporalio.Extensions.AI;

/// <summary>
/// Provides a <see cref="DataConverter"/> configured with <see cref="AIJsonUtilities.DefaultOptions"/>
/// so that MEAI types (<see cref="ChatMessage"/>, <see cref="AIContent"/> subtypes, etc.)
/// round-trip correctly through Temporal's payload converter.
/// </summary>
/// <remarks>
/// <para>
/// MEAI types use a <c>$type</c> discriminator for polymorphic <see cref="AIContent"/> serialization
/// (e.g., <see cref="TextContent"/>, <see cref="FunctionCallContent"/>). The default Temporal
/// <see cref="DefaultPayloadConverter"/> does not include these converters, so MEAI types may
/// lose type information during round-trips through workflow history.
/// </para>
/// <para>
/// Register this converter on the Temporal client or worker options:
/// <code>
/// new TemporalClientConnectOptions
/// {
///     DataConverter = DurableAIDataConverter.Instance
/// }
/// </code>
/// Or when using hosted workers:
/// <code>
/// services.AddHostedTemporalWorker(opts =>
/// {
///     opts.DataConverter = DurableAIDataConverter.Instance;
/// });
/// </code>
/// </para>
/// </remarks>
public static class DurableAIDataConverter
{
    /// <summary>
    /// A <see cref="DataConverter"/> whose JSON serializer uses <see cref="AIJsonUtilities.DefaultOptions"/>,
    /// which correctly handles polymorphic <see cref="AIContent"/> types.
    /// </summary>
    public static DataConverter Instance { get; } = new(
        new DefaultPayloadConverter(CreateOptions()),
        new DefaultFailureConverter());

    private static JsonSerializerOptions CreateOptions()
    {
        // Clone AIJsonUtilities.DefaultOptions which includes polymorphic AIContent support.
        var options = new JsonSerializerOptions(AIJsonUtilities.DefaultOptions);
        return options;
    }
}
