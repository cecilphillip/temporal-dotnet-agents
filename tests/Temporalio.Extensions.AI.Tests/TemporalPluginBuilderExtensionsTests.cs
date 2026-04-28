#pragma warning disable TAI001

using FakeItEasy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Temporalio.Client;
using Temporalio.Converters;
using Temporalio.Extensions.Hosting;
using Temporalio.Worker;
using Xunit;

namespace Temporalio.Extensions.AI.Tests;

public class TemporalPluginBuilderExtensionsTests
{
    // ── Helpers ───────────────────────────────────────────────────────────

    /// <summary>
    /// Fires all registered IConfigureOptions for TemporalWorkerServiceOptions on a fresh instance.
    /// Named configurators are fired with their own registered name (obtained via reflection) so
    /// the action runs regardless of the internal options-name format used by the Hosting library.
    /// </summary>
    private static TemporalWorkerServiceOptions BuildWorkerOptions(IServiceProvider provider)
    {
        var opts = new TemporalWorkerServiceOptions();
        foreach (var svc in provider.GetServices<IConfigureOptions<TemporalWorkerServiceOptions>>())
        {
            if (svc is IConfigureNamedOptions<TemporalWorkerServiceOptions> named)
            {
                // ConfigureNamedOptions<T> has a public Name property; use it to fire the action
                // for its registered name rather than Options.DefaultName ("").
                var name = svc.GetType().GetProperty("Name")?.GetValue(svc) as string;
                named.Configure(name, opts);
            }
            else
            {
                svc.Configure(opts);
            }
        }
        return opts;
    }

    // ── AddWorkerPlugin ───────────────────────────────────────────────────

    [Fact]
    public void AddWorkerPlugin_SinglePlugin_AppearsInOptions()
    {
        var services = new ServiceCollection();
        var plugin = A.Fake<ITemporalWorkerPlugin>();
        A.CallTo(() => plugin.Name).Returns("test-worker-plugin");

        services.AddHostedTemporalWorker("my-queue").AddWorkerPlugin(plugin);

        var opts = BuildWorkerOptions(services.BuildServiceProvider());

        Assert.NotNull(opts.Plugins);
        Assert.Contains(plugin, opts.Plugins);
    }

    [Fact]
    public void AddWorkerPlugin_MultiplePlugins_PreservesOrder()
    {
        var services = new ServiceCollection();
        var plugin1 = A.Fake<ITemporalWorkerPlugin>();
        var plugin2 = A.Fake<ITemporalWorkerPlugin>();
        A.CallTo(() => plugin1.Name).Returns("plugin-1");
        A.CallTo(() => plugin2.Name).Returns("plugin-2");

        services.AddHostedTemporalWorker("my-queue")
            .AddWorkerPlugin(plugin1)
            .AddWorkerPlugin(plugin2);

        var opts = BuildWorkerOptions(services.BuildServiceProvider());
        var plugins = opts.Plugins!.ToList();

        Assert.Equal(2, plugins.Count);
        Assert.Same(plugin1, plugins[0]);
        Assert.Same(plugin2, plugins[1]);
    }

    [Fact]
    public void AddWorkerPlugin_NullPlugin_Throws()
    {
        var services = new ServiceCollection();
        var builder = services.AddHostedTemporalWorker("my-queue");
        Assert.Throws<ArgumentNullException>(() => builder.AddWorkerPlugin(null!));
    }

    // ── AddClientPlugin (worker builder) ─────────────────────────────────

    [Fact]
    public void AddClientPlugin_WorkerBuilder_ClientOptionsNull_DoesNotThrow()
    {
        var services = new ServiceCollection();
        var plugin = A.Fake<ITemporalClientPlugin>();
        A.CallTo(() => plugin.Name).Returns("test-client-plugin");

        // 1-arg overload — no ClientOptions; action should be a no-op
        var builder = services.AddHostedTemporalWorker("my-queue");
        var ex = Record.Exception(() => builder.AddClientPlugin(plugin));
        Assert.Null(ex);

        // Firing configurators on an options instance with null ClientOptions should not throw
        var opts = BuildWorkerOptions(services.BuildServiceProvider());
        Assert.Null(opts.ClientOptions);
    }

    [Fact]
    public void AddClientPlugin_WorkerBuilder_ClientOptionsSet_AppendsPlugin()
    {
        var services = new ServiceCollection();
        var plugin = A.Fake<ITemporalClientPlugin>();
        A.CallTo(() => plugin.Name).Returns("test-client-plugin");

        // 3-arg overload sets ClientOptions
        services.AddHostedTemporalWorker("localhost:7233", "default", "my-queue")
            .AddClientPlugin(plugin);

        var opts = BuildWorkerOptions(services.BuildServiceProvider());

        Assert.NotNull(opts.ClientOptions);
        Assert.NotNull(opts.ClientOptions.Plugins);
        Assert.Contains(plugin, opts.ClientOptions.Plugins);
    }

    // ── AddClientPlugin (OptionsBuilder<TemporalClientConnectOptions>) ───

    [Fact]
    public void AddClientPlugin_OptionsBuilder_AppendsPlugin()
    {
        var services = new ServiceCollection();
        var plugin = A.Fake<ITemporalClientPlugin>();
        A.CallTo(() => plugin.Name).Returns("test-client-plugin");

        services.AddOptions<TemporalClientConnectOptions>()
            .AddClientPlugin(plugin);

        var opts = services.BuildServiceProvider()
            .GetRequiredService<IOptions<TemporalClientConnectOptions>>().Value;

        Assert.NotNull(opts.Plugins);
        Assert.Contains(plugin, opts.Plugins);
    }

    [Fact]
    public void AddClientPlugin_OptionsBuilder_MultiplePlugins_PreservesOrder()
    {
        var services = new ServiceCollection();
        var plugin1 = A.Fake<ITemporalClientPlugin>();
        var plugin2 = A.Fake<ITemporalClientPlugin>();
        A.CallTo(() => plugin1.Name).Returns("plugin-1");
        A.CallTo(() => plugin2.Name).Returns("plugin-2");

        services.AddOptions<TemporalClientConnectOptions>()
            .AddClientPlugin(plugin1)
            .AddClientPlugin(plugin2);

        var opts = services.BuildServiceProvider()
            .GetRequiredService<IOptions<TemporalClientConnectOptions>>().Value;

        var plugins = opts.Plugins!.ToList();
        Assert.Equal(2, plugins.Count);
        Assert.Same(plugin1, plugins[0]);
        Assert.Same(plugin2, plugins[1]);
    }

    // ── DurableAIDataConverterPlugin ─────────────────────────────────────

    [Fact]
    public void DurableAIDataConverterPlugin_DefaultConverter_SetsInstance()
    {
        var plugin = new DurableAIDataConverterPlugin();
        var options = new TemporalClientOptions();

        Assert.Equal(DataConverter.Default, options.DataConverter);

        plugin.ConfigureClient(options);

        Assert.Same(DurableAIDataConverter.Instance, options.DataConverter);
    }

    [Fact]
    public void DurableAIDataConverterPlugin_CustomConverter_IsPreserved()
    {
        var plugin = new DurableAIDataConverterPlugin();
        var customConverter = new DataConverter(new DefaultPayloadConverter(), new DefaultFailureConverter());
        var options = new TemporalClientOptions { DataConverter = customConverter };

        plugin.ConfigureClient(options);

        Assert.Same(customConverter, options.DataConverter);
    }

    [Fact]
    public void DurableAIDataConverterPlugin_ConfigureClient_IsIdempotent_WhenCalledTwice()
    {
        // Pins the contract that calling AddDurableAI() AND .AddClientPlugin(new DurableAIDataConverterPlugin())
        // is safe — a future refactor cannot silently regress this.
        var plugin = new DurableAIDataConverterPlugin();
        var options = new TemporalClientOptions();

        Assert.Equal(DataConverter.Default, options.DataConverter);

        plugin.ConfigureClient(options);
        var afterFirst = options.DataConverter;
        Assert.Same(DurableAIDataConverter.Instance, afterFirst);

        plugin.ConfigureClient(options);

        // Second call must leave the converter unchanged — not replaced, not nulled,
        // not swapped for a new instance.
        Assert.Same(DurableAIDataConverter.Instance, options.DataConverter);
        Assert.Same(afterFirst, options.DataConverter);
    }

    [Fact]
    public void DurableAIDataConverterPlugin_ConfigureClient_DoesNotOverrideUserConverter()
    {
        // If the user has already set a non-default converter, ConfigureClient must
        // never override it — neither on the first call nor on a repeated call.
        var plugin = new DurableAIDataConverterPlugin();
        var userConverter = new DataConverter(new DefaultPayloadConverter(), new DefaultFailureConverter());
        var options = new TemporalClientOptions { DataConverter = userConverter };

        plugin.ConfigureClient(options);
        Assert.Same(userConverter, options.DataConverter);

        plugin.ConfigureClient(options);
        Assert.Same(userConverter, options.DataConverter);
    }

    // ── AddDurableAI configurator registration ────────────────────────────

    [Fact]
    public void AddDurableAI_RegistersClientOptionsConfigurator()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHostedTemporalWorker("my-queue").AddDurableAI();

        Assert.Contains(services, sd =>
            sd.ServiceType == typeof(IConfigureOptions<TemporalClientConnectOptions>) &&
            sd.ImplementationType == typeof(DurableAIClientOptionsConfigurator));
    }

    [Fact]
    public void AddDurableAI_RegistersWorkerClientConfigurator()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHostedTemporalWorker("my-queue").AddDurableAI();

        Assert.Contains(services, sd =>
            sd.ServiceType == typeof(IPostConfigureOptions<TemporalWorkerServiceOptions>) &&
            sd.ImplementationType == typeof(DurableAIWorkerClientConfigurator));
    }

    [Fact]
    public void AddDurableAI_CalledTwice_ConfiguratorRegisteredOnce()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var builder = services.AddHostedTemporalWorker("my-queue");
        builder.AddDurableAI();
        builder.AddDurableAI();

        var clientConfiguratorCount = services.Count(sd =>
            sd.ServiceType == typeof(IConfigureOptions<TemporalClientConnectOptions>) &&
            sd.ImplementationType == typeof(DurableAIClientOptionsConfigurator));

        var workerConfiguratorCount = services.Count(sd =>
            sd.ServiceType == typeof(IPostConfigureOptions<TemporalWorkerServiceOptions>) &&
            sd.ImplementationType == typeof(DurableAIWorkerClientConfigurator));

        Assert.Equal(1, clientConfiguratorCount);
        Assert.Equal(1, workerConfiguratorCount);
    }
}
