using Microsoft.Extensions.AI;

namespace Temporalio.Extensions.AI;

/// <summary>
/// Extension methods for <see cref="ChatClientBuilder"/> to add durable execution middleware.
/// </summary>
public static class ChatClientBuilderExtensions
{
    /// <summary>
    /// Adds durable execution middleware to the chat client pipeline.
    /// When the pipeline is used inside a Temporal workflow, LLM calls are automatically
    /// dispatched as Temporal activities with retry, timeout, and crash recovery.
    /// </summary>
    /// <remarks>
    /// <see cref="DurableExecutionOptions.TaskQueue"/> must be set via the <paramref name="configure"/>
    /// delegate. Validation runs eagerly at pipeline build time so misconfiguration surfaces as a
    /// startup error rather than a runtime failure inside the workflow.
    /// </remarks>
    /// <param name="builder">The chat client builder.</param>
    /// <param name="configure">Optional delegate to configure <see cref="DurableExecutionOptions"/>.</param>
    /// <returns>The builder for further chaining.</returns>
    public static ChatClientBuilder UseDurableExecution(
        this ChatClientBuilder builder,
        Action<DurableExecutionOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var options = new DurableExecutionOptions();
        configure?.Invoke(options);
        options.Validate();

        return builder.Use(innerClient => new DurableChatClient(innerClient, options));
    }
}
