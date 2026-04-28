using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Temporalio.Client;
using Temporalio.Converters;
using Temporalio.Extensions.Hosting;

namespace Temporalio.Extensions.AI;

/// <summary>
/// An <see cref="ITemporalClientPlugin"/> that auto-wires <see cref="DurableAIDataConverter"/>
/// onto the Temporal client when no custom data converter has already been set.
/// </summary>
internal sealed class DurableAIDataConverterPlugin : ITemporalClientPlugin
{
    private readonly ILogger? _logger;

    public DurableAIDataConverterPlugin(ILogger? logger = null) => _logger = logger;

    /// <summary>
    /// The plugin name. Exposed as a constant so other registrars can dedupe
    /// without hard-coding the string in multiple places.
    /// </summary>
    internal const string PluginName = "Temporalio.Extensions.AI.DataConverter";

    public string Name => PluginName;

    public void ConfigureClient(TemporalClientOptions options)
    {
        if (options.DataConverter == DataConverter.Default)
        {
            options.DataConverter = DurableAIDataConverter.Instance;
            _logger?.LogDebug(
                "DurableAIDataConverter applied to TemporalClient (DataConverter was default).");
        }
        else
        {
            _logger?.LogDebug(
                "DataConverter already set to {Type}; DurableAIDataConverter not applied.",
                options.DataConverter.GetType().Name);
        }
    }

    public Task<TemporalConnection> ConnectAsync(
        TemporalClientConnectOptions options,
        Func<TemporalClientConnectOptions, Task<TemporalConnection>> continuation) =>
        continuation(options);

    /// <summary>
    /// Applies <see cref="DurableAIDataConverter"/> to <see cref="TemporalClientConnectOptions"/>
    /// if <see cref="DataConverter.Default"/> is still set. Called by the <c>IConfigureOptions</c>
    /// path where the connect options are configured independently of the client options.
    /// </summary>
    internal void ApplyToConnectOptions(TemporalClientConnectOptions options)
    {
        if (options.DataConverter == DataConverter.Default)
        {
            options.DataConverter = DurableAIDataConverter.Instance;
            _logger?.LogDebug(
                "DurableAIDataConverter applied to TemporalClientConnectOptions (DataConverter was default).");
        }
        else
        {
            _logger?.LogDebug(
                "DataConverter already set to {Type}; DurableAIDataConverter not applied.",
                options.DataConverter.GetType().Name);
        }
    }
}

/// <summary>
/// Configures <see cref="TemporalClientConnectOptions"/> (used by <c>AddTemporalClient()</c>)
/// to apply <see cref="DurableAIDataConverter"/> via the plugin mechanism.
/// </summary>
internal sealed class DurableAIClientOptionsConfigurator
    : IConfigureOptions<TemporalClientConnectOptions>
{
    private readonly ILogger _logger;

    public DurableAIClientOptionsConfigurator(
        ILogger<DurableAIDataConverterPlugin> logger) => _logger = logger;

    public void Configure(TemporalClientConnectOptions options) =>
        new DurableAIDataConverterPlugin(_logger).ApplyToConnectOptions(options);
}

/// <summary>
/// Post-configures <see cref="TemporalWorkerServiceOptions"/> (used by the 3-arg
/// <c>AddHostedTemporalWorker</c> overload) to inject <see cref="DurableAIDataConverter"/>
/// via the plugin mechanism when the worker service creates its own client.
/// </summary>
internal sealed class DurableAIWorkerClientConfigurator
    : IPostConfigureOptions<TemporalWorkerServiceOptions>
{
    private readonly ILogger _logger;

    public DurableAIWorkerClientConfigurator(
        ILogger<DurableAIDataConverterPlugin> logger) => _logger = logger;

    public void PostConfigure(string? name, TemporalWorkerServiceOptions options)
    {
        if (options.ClientOptions is null) return;
        var list = options.ClientOptions.Plugins?.ToList() ?? [];

        // Dedupe by Name — never push a second DurableAIDataConverterPlugin if one
        // is already present (e.g., the user manually added it via .AddClientPlugin
        // or another registration path already wired one in).
        if (list.Any(p => string.Equals(
                p.Name,
                DurableAIDataConverterPlugin.PluginName,
                StringComparison.Ordinal)))
        {
            return;
        }

        list.Add(new DurableAIDataConverterPlugin(_logger));
        options.ClientOptions.Plugins = list;
    }
}
