using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Temporalio.Extensions.Hosting;

namespace Temporalio.Extensions.AI;

/// <summary>
/// Extension methods for registering durable AI services.
/// </summary>
public static class DurableAIServiceCollectionExtensions
{
    /// <summary>
    /// Registers the durable AI workflow, activities, and support services on a Temporal worker.
    /// </summary>
    /// <param name="builder">The worker options builder returned by AddHostedTemporalWorker.</param>
    /// <param name="configure">Optional delegate to configure <see cref="DurableExecutionOptions"/>.</param>
    /// <returns>The same builder for further chaining.</returns>
    /// <remarks>
    /// <para>
    /// Before calling this method, register an <see cref="IChatClient"/> in the service collection.
    /// The idiomatic MEAI pattern uses <c>AddChatClient</c>, which returns a
    /// <see cref="Microsoft.Extensions.AI.ChatClientBuilder"/> for chaining middleware:
    /// </para>
    /// <code>
    /// builder.Services
    ///     .AddChatClient(innerClient)
    ///     .UseFunctionInvocation()
    ///     .Build();
    /// </code>
    /// <para>
    /// <see cref="DurableChatActivities"/> constructor-injects the <b>unkeyed</b> <see cref="IChatClient"/>.
    /// If using <c>AddKeyedChatClient</c> for multiple clients, also register an unkeyed alias.
    /// </para>
    /// <para>
    /// <see cref="DurableAIDataConverter"/> is automatically applied to the Temporal client when
    /// using <c>AddTemporalClient(address, ns)</c> or the 3-arg <c>AddHostedTemporalWorker(address, ns, queue)</c>
    /// overload that creates its own client. When creating the client manually via
    /// <c>TemporalClient.ConnectAsync</c> and registering it with <c>AddSingleton</c>, you must
    /// still set <c>DataConverter = DurableAIDataConverter.Instance</c> explicitly.
    /// </para>
    /// </remarks>
    public static ITemporalWorkerServiceOptionsBuilder AddDurableAI(
        this ITemporalWorkerServiceOptionsBuilder builder,
        Action<DurableExecutionOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var options = new DurableExecutionOptions
        {
            TaskQueue = builder.TaskQueue
        };
        configure?.Invoke(options);
        options.Validate();

        DurableAIRegistrar.Register(builder.Services, builder, options);

        return builder;
    }

    /// <summary>
    /// Registers one or more <see cref="AIFunction"/> tools for durable execution.
    /// Each tool can be resolved by name inside <see cref="DurableFunctionActivities"/>
    /// when invoked via <see cref="DurableAIFunctionExtensions.AsDurable"/> inside a workflow.
    /// </summary>
    /// <param name="builder">The worker options builder returned by <see cref="AddDurableAI"/>.</param>
    /// <param name="tools">The tools to register.</param>
    /// <returns>The same builder for further chaining.</returns>
    /// <remarks>
    /// Call this after <see cref="AddDurableAI"/> to register tools that will be dispatched
    /// as individual Temporal activities when wrapped with <c>AsDurable()</c> inside a workflow:
    /// <code>
    /// builder.Services
    ///     .AddHostedTemporalWorker("my-task-queue")
    ///     .AddDurableAI()
    ///     .AddDurableTools(weatherTool, stockTool);
    /// </code>
    /// </remarks>
    public static ITemporalWorkerServiceOptionsBuilder AddDurableTools(
        this ITemporalWorkerServiceOptionsBuilder builder,
        params AIFunction[] tools)
    {
        ArgumentNullException.ThrowIfNull(builder);

        if (!builder.Services.Any(d => d.ServiceType == typeof(DurableExecutionOptions)))
        {
            throw new InvalidOperationException(
                "AddDurableTools requires AddDurableAI to be called first on the same worker builder.");
        }

        var services = builder.Services;

        // Register each tool in the registry via a configure callback.
        foreach (var tool in tools)
        {
            ArgumentNullException.ThrowIfNull(tool);
            services.AddSingleton<Action<DurableFunctionRegistry>>(
                registry => registry.Register(tool));
        }

        return builder;
    }
}

/// <summary>
/// Registry for <see cref="AIFunction"/> instances that can be invoked durably.
/// </summary>
internal sealed class DurableFunctionRegistry : Dictionary<string, AIFunction>, IReadOnlyDictionary<string, AIFunction>
{
    public DurableFunctionRegistry(IEnumerable<Action<DurableFunctionRegistry>>? configurators = null)
        : base(StringComparer.OrdinalIgnoreCase)
    {
        if (configurators is null) return;

        foreach (var configure in configurators)
        {
            configure(this);
        }
    }

    public void Register(AIFunction function)
    {
        ArgumentNullException.ThrowIfNull(function);
        this[function.Name] = function;
    }
}
