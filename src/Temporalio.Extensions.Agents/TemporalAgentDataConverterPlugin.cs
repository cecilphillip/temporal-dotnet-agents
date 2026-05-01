using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Temporalio.Client;
using Temporalio.Converters;
using Temporalio.Extensions.AI;
using Temporalio.Extensions.Hosting;

namespace Temporalio.Extensions.Agents;

/// <summary>
/// An <see cref="ITemporalClientPlugin"/> that auto-wires <see cref="TemporalAgentDataConverter"/>
/// onto the Temporal client when no custom data converter has already been set, OR when the
/// AI-library converter (<see cref="DurableAIDataConverter"/>) is currently set — the MAF
/// converter is a strict superset (it carries the same MEAI polymorphism plus the
/// agent-specific subclass discriminators).
/// </summary>
internal sealed class TemporalAgentDataConverterPlugin : ITemporalClientPlugin
{
    private readonly ILogger? _logger;

    public TemporalAgentDataConverterPlugin(ILogger? logger = null) => _logger = logger;

    /// <summary>
    /// The plugin name. Matches the namespace convention from the partner integration guide.
    /// </summary>
    public const string PluginName = "Temporalio.Extensions.Agents.DataConverter";

    public string Name => PluginName;

    public void ConfigureClient(TemporalClientOptions options) =>
        Apply(o => options.DataConverter = o, options.DataConverter);

    public Task<TemporalConnection> ConnectAsync(
        TemporalClientConnectOptions options,
        Func<TemporalClientConnectOptions, Task<TemporalConnection>> continuation) =>
        continuation(options);

    /// <summary>
    /// Applies <see cref="TemporalAgentDataConverter"/> to <see cref="TemporalClientConnectOptions"/>
    /// when the current converter is <see cref="DataConverter.Default"/> or the AI-library
    /// <see cref="DurableAIDataConverter"/>.
    /// </summary>
    internal void ApplyToConnectOptions(TemporalClientConnectOptions options) =>
        Apply(c => options.DataConverter = c, options.DataConverter);

    private void Apply(Action<DataConverter> setter, DataConverter current)
    {
        if (current == DataConverter.Default || current == DurableAIDataConverter.Instance)
        {
            setter(TemporalAgentDataConverter.Instance);
            _logger?.LogDebug(
                "TemporalAgentDataConverter applied to Temporal client (previous converter: {Type}).",
                current.GetType().Name);
        }
        else
        {
            _logger?.LogDebug(
                "DataConverter already set to a custom value ({Type}); TemporalAgentDataConverter not applied.",
                current.GetType().Name);
        }
    }
}

/// <summary>
/// Configures <see cref="TemporalClientConnectOptions"/> (used by <c>AddTemporalClient()</c>)
/// to apply <see cref="TemporalAgentDataConverter"/> via the plugin mechanism.
/// </summary>
internal sealed class TemporalAgentClientOptionsConfigurator
    : IConfigureOptions<TemporalClientConnectOptions>
{
    private readonly ILogger _logger;

    public TemporalAgentClientOptionsConfigurator(
        ILogger<TemporalAgentDataConverterPlugin> logger) => _logger = logger;

    public void Configure(TemporalClientConnectOptions options) =>
        new TemporalAgentDataConverterPlugin(_logger).ApplyToConnectOptions(options);
}

/// <summary>
/// Post-configures <see cref="TemporalWorkerServiceOptions"/> to inject the MAF data
/// converter via the plugin mechanism when the worker creates its own client.
/// </summary>
internal sealed class TemporalAgentWorkerClientConfigurator
    : IPostConfigureOptions<TemporalWorkerServiceOptions>
{
    private readonly ILogger _logger;

    public TemporalAgentWorkerClientConfigurator(
        ILogger<TemporalAgentDataConverterPlugin> logger) => _logger = logger;

    public void PostConfigure(string? name, TemporalWorkerServiceOptions options)
    {
        if (options.ClientOptions is null) return;
        var list = options.ClientOptions.Plugins?.ToList() ?? [];

        // Dedupe by Name — never push a second TemporalAgentDataConverterPlugin if one
        // is already present.
        if (list.Any(p => string.Equals(
                p.Name,
                TemporalAgentDataConverterPlugin.PluginName,
                StringComparison.Ordinal)))
        {
            return;
        }

        list.Add(new TemporalAgentDataConverterPlugin(_logger));
        options.ClientOptions.Plugins = list;
    }
}
