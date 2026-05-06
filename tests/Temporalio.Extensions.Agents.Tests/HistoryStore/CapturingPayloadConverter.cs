using System.Collections.Concurrent;
using Temporalio.Api.Common.V1;
using Temporalio.Converters;

namespace Temporalio.Extensions.Agents.Tests.HistoryStore;

/// <summary>
/// A wrapping <see cref="IPayloadConverter"/> that records every value it serializes.
/// Used in tests to assert "the workflow did not include <c>ConversationHistory</c>
/// in the activity-scheduled event payload" without round-tripping through a live
/// Temporal server.
/// </summary>
/// <remarks>
/// <para>
/// Delegates the actual JSON encoding to <see cref="DefaultPayloadConverter"/> configured
/// with <see cref="AIJsonUtilities.DefaultOptions"/> — matching what
/// <c>DurableAIDataConverter.Instance</c> uses in production. This way payload bytes
/// captured here are byte-identical to what the live server would observe.
/// </para>
/// <para>
/// To use:
/// <code>
/// var capturing = new CapturingPayloadConverter();
/// var dataConverter = new DataConverter(capturing, new DefaultFailureConverter());
/// // pass to TemporalClientConnectOptions or worker options
/// // ... after the workflow runs, inspect capturing.CapturedInputs
/// </code>
/// </para>
/// </remarks>
internal sealed class CapturingPayloadConverter : IPayloadConverter
{
    private readonly IPayloadConverter _inner;
    private readonly ConcurrentQueue<CapturedToPayload> _toPayload = new();
    private readonly ConcurrentQueue<CapturedToValue> _toValue = new();

    public CapturingPayloadConverter()
        : this(new DefaultPayloadConverter(TemporalAgentJsonUtilities.DefaultOptions))
    {
    }

    public CapturingPayloadConverter(IPayloadConverter inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
    }

    /// <summary>Every value passed to <see cref="ToPayload(object?)"/>, in order.</summary>
    public IReadOnlyCollection<CapturedToPayload> CapturedInputs => _toPayload.ToArray();

    /// <summary>Every payload-to-value decode, in order.</summary>
    public IReadOnlyCollection<CapturedToValue> CapturedOutputs => _toValue.ToArray();

    /// <summary>
    /// Returns all captured inputs whose runtime type matches <typeparamref name="T"/>.
    /// Useful for: <c>capturing.OfInputType&lt;ExecuteAgentInput&gt;()</c>.
    /// </summary>
    public IEnumerable<T> OfInputType<T>()
        where T : class =>
        _toPayload.Select(c => c.Value).OfType<T>();

    /// <inheritdoc />
    public Payload ToPayload(object? value)
    {
        var payload = _inner.ToPayload(value);
        _toPayload.Enqueue(new CapturedToPayload(value, value?.GetType(), payload));
        return payload;
    }

    /// <inheritdoc />
    public object? ToValue(Payload payload, Type type)
    {
        var value = _inner.ToValue(payload, type);
        _toValue.Enqueue(new CapturedToValue(payload, type, value));
        return value;
    }

    /// <summary>One input captured at <see cref="ToPayload(object?)"/> time.</summary>
    internal sealed record CapturedToPayload(object? Value, Type? RuntimeType, Payload Payload);

    /// <summary>One value captured at <see cref="ToValue(Payload, Type)"/> time.</summary>
    internal sealed record CapturedToValue(Payload Payload, Type RequestedType, object? Value);
}
