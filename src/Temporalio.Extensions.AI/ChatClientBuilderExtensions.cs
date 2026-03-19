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

    /// <summary>
    /// Adds durable chat reduction to the pipeline. The full conversation history is preserved
    /// in Temporal workflow state while the inner reducer provides a sliding window to the LLM.
    /// </summary>
    /// <param name="builder">The chat client builder.</param>
    /// <param name="innerReducer">
    /// The reducer that performs actual reduction (e.g., <c>new MessageCountingChatReducer(20)</c>).
    /// </param>
    /// <returns>The builder for further chaining.</returns>
    public static ChatClientBuilder UseDurableReduction(
        this ChatClientBuilder builder,
        IChatReducer innerReducer)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(innerReducer);

        var durableReducer = new DurableChatReducer(innerReducer);
        return builder.UseChatReducer(durableReducer);
    }
}
