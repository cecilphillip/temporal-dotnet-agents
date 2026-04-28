#pragma warning disable TAI001

using FakeItEasy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Temporalio.Client;
using Temporalio.Extensions.Hosting;
using Temporalio.Worker;
using Temporalio.Workflows;
using Xunit;

namespace Temporalio.Extensions.AI.Tests;

public class DurableAIPluginTests
{
    // ── Helpers ───────────────────────────────────────────────────────────

    private static TemporalWorkerServiceOptions BuildWorkerServiceOptions(IServiceProvider provider)
    {
        var opts = new TemporalWorkerServiceOptions();
        foreach (var svc in provider.GetServices<IConfigureOptions<TemporalWorkerServiceOptions>>())
        {
            if (svc is IConfigureNamedOptions<TemporalWorkerServiceOptions> named)
            {
                var name = svc.GetType().GetProperty("Name")?.GetValue(svc) as string;
                named.Configure(name, opts);
            }
            else
            {
                svc.Configure(opts);
            }
        }
        foreach (var svc in provider.GetServices<IPostConfigureOptions<TemporalWorkerServiceOptions>>())
        {
            svc.PostConfigure(string.Empty, opts);
        }
        return opts;
    }

    private static TemporalWorkerOptions FreshWorkerOptions() =>
        new() { TaskQueue = "test-queue" };

    // ── Name ──────────────────────────────────────────────────────────────

    [Fact]
    public void Name_ReturnsExpectedConstant()
    {
        var plugin = new DurableAIPlugin();
        Assert.Equal("Temporalio.Extensions.AI.DurableAIPlugin", plugin.Name);
        Assert.Equal(DurableAIPlugin.PluginName, plugin.Name);
    }

    // ── Constructor overloads ────────────────────────────────────────────

    [Fact]
    public void Constructor_NullConfigureDelegate_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new DurableAIPlugin((Action<DurableExecutionOptions>)null!));
    }

    [Fact]
    public void Constructor_NullOptions_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new DurableAIPlugin((DurableExecutionOptions)null!));
    }

    [Fact]
    public void Constructor_ConfigureDelegate_AppliedToFreshOptions()
    {
        var plugin = new DurableAIPlugin(opts =>
        {
            opts.TaskQueue = "explicit-queue";
            opts.ActivityTimeout = TimeSpan.FromSeconds(42);
        });
        Assert.Equal("explicit-queue", plugin.Options.TaskQueue);
        Assert.Equal(TimeSpan.FromSeconds(42), plugin.Options.ActivityTimeout);
    }

    [Fact]
    public void Constructor_ExplicitOptions_HoldsReference()
    {
        var opts = new DurableExecutionOptions { TaskQueue = "q" };
        var plugin = new DurableAIPlugin(opts);
        Assert.Same(opts, plugin.Options);
    }

    // ── ConfigureWorker ───────────────────────────────────────────────────

    [Fact]
    public void ConfigureWorker_RegistersDurableChatWorkflow_WhenNotPresent()
    {
        var plugin = new DurableAIPlugin();
        var opts = FreshWorkerOptions();

        Assert.DoesNotContain(opts.Workflows, w => w.Type == typeof(DurableChatWorkflow));

        plugin.ConfigureWorker(opts);

        Assert.Contains(opts.Workflows, w => w.Type == typeof(DurableChatWorkflow));
    }

    [Fact]
    public void ConfigureWorker_DoesNotDoubleRegister_WhenWorkflowAlreadyPresent()
    {
        var plugin = new DurableAIPlugin();
        var opts = FreshWorkerOptions();
        opts.AddWorkflow<DurableChatWorkflow>();
        var initialCount = opts.Workflows.Count(w => w.Type == typeof(DurableChatWorkflow));

        plugin.ConfigureWorker(opts);

        var finalCount = opts.Workflows.Count(w => w.Type == typeof(DurableChatWorkflow));
        Assert.Equal(initialCount, finalCount);
        Assert.Equal(1, finalCount);
    }

    [Fact]
    public void ConfigureWorker_RegisterDefaultWorkflowFalse_DoesNotAddWorkflow()
    {
        var plugin = new DurableAIPlugin(opts => opts.RegisterDefaultWorkflow = false);
        var opts = FreshWorkerOptions();

        plugin.ConfigureWorker(opts);

        Assert.DoesNotContain(opts.Workflows, w => w.Type == typeof(DurableChatWorkflow));
    }

    [Fact]
    public void ConfigureWorker_TaskQueueEmpty_PopulatedFromOptions()
    {
        var plugin = new DurableAIPlugin(opts => opts.TaskQueue = "from-options");
        var opts = new TemporalWorkerOptions(); // empty TaskQueue

        plugin.ConfigureWorker(opts);

        Assert.Equal("from-options", opts.TaskQueue);
    }

    [Fact]
    public void ConfigureWorker_TaskQueueAlreadySet_NotOverwritten()
    {
        var plugin = new DurableAIPlugin(opts => opts.TaskQueue = "from-options");
        var opts = new TemporalWorkerOptions { TaskQueue = "preset" };

        plugin.ConfigureWorker(opts);

        Assert.Equal("preset", opts.TaskQueue);
    }

    [Fact]
    public void ConfigureWorker_NullOptions_Throws()
    {
        var plugin = new DurableAIPlugin();
        Assert.Throws<ArgumentNullException>(() => plugin.ConfigureWorker(null!));
    }

    // ── RunWorkerAsync passthrough ───────────────────────────────────────

    [Fact]
    public async Task RunWorkerAsync_IsPassthrough()
    {
        var plugin = new DurableAIPlugin();
        // We can't easily construct a real TemporalWorker here, but we can
        // verify the continuation gets invoked with the supplied arguments.
        TemporalWorker capturedWorker = null!;
        CancellationToken capturedToken = default;
        var sentinel = new object();

        Task<object> Continuation(TemporalWorker w, CancellationToken t)
        {
            capturedWorker = w;
            capturedToken = t;
            return Task.FromResult(sentinel);
        }

        // Pass null worker — plugin must not touch it.
        var cts = new CancellationTokenSource();
        var result = await plugin.RunWorkerAsync<object>(null!, Continuation, cts.Token);

        Assert.Same(sentinel, result);
        Assert.Null(capturedWorker);
        Assert.Equal(cts.Token, capturedToken);
    }

    [Fact]
    public async Task RunWorkerAsync_NullContinuation_Throws()
    {
        var plugin = new DurableAIPlugin();
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => plugin.RunWorkerAsync<object>(null!, null!, CancellationToken.None));
    }

    // ── ConfigureReplayer ─────────────────────────────────────────────────

    [Fact]
    public void ConfigureReplayer_AddsDurableChatWorkflow_WhenNotPresent()
    {
        var plugin = new DurableAIPlugin();
        var opts = new WorkflowReplayerOptions();

        Assert.DoesNotContain(opts.Workflows, w => w.Type == typeof(DurableChatWorkflow));

        plugin.ConfigureReplayer(opts);

        Assert.Contains(opts.Workflows, w => w.Type == typeof(DurableChatWorkflow));
    }

    [Fact]
    public void ConfigureReplayer_DoesNotDoubleAdd_WhenAlreadyPresent()
    {
        var plugin = new DurableAIPlugin();
        var opts = new WorkflowReplayerOptions();
        opts.AddWorkflow<DurableChatWorkflow>();

        plugin.ConfigureReplayer(opts);

        Assert.Equal(1, opts.Workflows.Count(w => w.Type == typeof(DurableChatWorkflow)));
    }

    [Fact]
    public void ConfigureReplayer_RegisterDefaultWorkflowFalse_DoesNotAdd()
    {
        var plugin = new DurableAIPlugin(opts => opts.RegisterDefaultWorkflow = false);
        var opts = new WorkflowReplayerOptions();

        plugin.ConfigureReplayer(opts);

        Assert.DoesNotContain(opts.Workflows, w => w.Type == typeof(DurableChatWorkflow));
    }

    [Fact]
    public void ConfigureReplayer_NullOptions_Throws()
    {
        var plugin = new DurableAIPlugin();
        Assert.Throws<ArgumentNullException>(() => plugin.ConfigureReplayer(null!));
    }

    // ── AddWorkerPlugin(DurableAIPlugin) DI registration ─────────────────

    [Fact]
    public void AddWorkerPlugin_DurableAIPlugin_RegistersOptionsSingleton()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHostedTemporalWorker("my-queue")
            .AddWorkerPlugin(new DurableAIPlugin());

        Assert.Contains(services, sd => sd.ServiceType == typeof(DurableExecutionOptions));
    }

    [Fact]
    public void AddWorkerPlugin_DurableAIPlugin_RegistersFunctionRegistry()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHostedTemporalWorker("my-queue")
            .AddWorkerPlugin(new DurableAIPlugin());

        Assert.Contains(services, sd => sd.ServiceType == typeof(DurableFunctionRegistry));
    }

    [Fact]
    public void AddWorkerPlugin_DurableAIPlugin_RegistersSessionClient()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHostedTemporalWorker("my-queue")
            .AddWorkerPlugin(new DurableAIPlugin());

        Assert.Contains(services, sd => sd.ServiceType == typeof(DurableChatSessionClient));
        Assert.Contains(services, sd => sd.ServiceType == typeof(IDurableChatSessionClient));
    }

    [Fact]
    public void AddWorkerPlugin_DurableAIPlugin_RegistersConfiguratorsLikeAddDurableAI()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHostedTemporalWorker("my-queue")
            .AddWorkerPlugin(new DurableAIPlugin());

        Assert.Contains(services, sd =>
            sd.ServiceType == typeof(IConfigureOptions<TemporalClientConnectOptions>) &&
            sd.ImplementationType == typeof(DurableAIClientOptionsConfigurator));

        Assert.Contains(services, sd =>
            sd.ServiceType == typeof(IPostConfigureOptions<TemporalWorkerServiceOptions>) &&
            sd.ImplementationType == typeof(DurableAIWorkerClientConfigurator));
    }

    [Fact]
    public void AddWorkerPlugin_DurableAIPlugin_AppendsPluginToWorkerPluginChain()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var plugin = new DurableAIPlugin();
        services.AddHostedTemporalWorker("my-queue").AddWorkerPlugin(plugin);

        var opts = BuildWorkerServiceOptions(services.BuildServiceProvider());

        Assert.NotNull(opts.Plugins);
        Assert.Contains((ITemporalWorkerPlugin)plugin, opts.Plugins);
    }

    [Fact]
    public void AddWorkerPlugin_DurableAIPlugin_NullPlugin_Throws()
    {
        var services = new ServiceCollection();
        var builder = services.AddHostedTemporalWorker("my-queue");
        Assert.Throws<ArgumentNullException>(
            () => builder.AddWorkerPlugin((DurableAIPlugin)null!));
    }

    // ── Equivalence: AddDurableAI vs AddWorkerPlugin(DurableAIPlugin) ────

    [Fact]
    public void RegistrationPaths_ProduceEquivalentDIState()
    {
        // Path A: AddDurableAI()
        var servicesA = new ServiceCollection();
        servicesA.AddLogging();
        servicesA.AddHostedTemporalWorker("my-queue").AddDurableAI();

        // Path B: AddWorkerPlugin(new DurableAIPlugin())
        var servicesB = new ServiceCollection();
        servicesB.AddLogging();
        servicesB.AddHostedTemporalWorker("my-queue").AddWorkerPlugin(new DurableAIPlugin());

        // Compare the relevant ServiceDescriptors by service type.
        var relevantTypes = new[]
        {
            typeof(DurableExecutionOptions),
            typeof(DurableFunctionRegistry),
            typeof(DurableChatSessionClient),
            typeof(IDurableChatSessionClient),
            typeof(IConfigureOptions<TemporalClientConnectOptions>),
            typeof(IPostConfigureOptions<TemporalWorkerServiceOptions>),
        };

        foreach (var type in relevantTypes)
        {
            var inA = servicesA.Where(sd => sd.ServiceType == type).ToList();
            var inB = servicesB.Where(sd => sd.ServiceType == type).ToList();
            Assert.True(inA.Count > 0, $"Path A missing registration for {type}.");
            Assert.True(inB.Count > 0, $"Path B missing registration for {type}.");
        }
    }

    // ── DataConverter dedupe in PostConfigure ────────────────────────────

    [Fact]
    public void DurableAIWorkerClientConfigurator_DoesNotPushDuplicate()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHostedTemporalWorker("localhost:7233", "default", "my-queue")
            .AddDurableAI();

        var opts = BuildWorkerServiceOptions(services.BuildServiceProvider());

        // Fire the IPostConfigureOptions a second time on the same options to
        // simulate a double-application path. The dedupe must hold.
        var provider = services.BuildServiceProvider();
        foreach (var svc in provider.GetServices<IPostConfigureOptions<TemporalWorkerServiceOptions>>())
        {
            svc.PostConfigure(string.Empty, opts);
        }

        Assert.NotNull(opts.ClientOptions);
        var converterPluginCount = opts.ClientOptions!.Plugins?
            .Count(p => string.Equals(p.Name, "Temporalio.Extensions.AI.DataConverter", StringComparison.Ordinal)) ?? 0;
        Assert.Equal(1, converterPluginCount);
    }
}
