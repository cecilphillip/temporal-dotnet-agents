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

        return builder.Use(innerClient => new DurableChatClient(innerClient, options));
    }
}
