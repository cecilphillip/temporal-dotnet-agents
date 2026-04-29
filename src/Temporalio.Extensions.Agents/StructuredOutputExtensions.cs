using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Temporalio.Extensions.Agents.Session;
using Temporalio.Extensions.Agents.Workflows;

namespace Temporalio.Extensions.Agents;

/// <summary>
/// Extension methods that add typed structured output deserialization to agent types.
/// Strips markdown code fences and retries with error context when deserialization fails.
/// </summary>
public static class StructuredOutputExtensions
{
    /// <summary>
    /// Runs the agent and deserializes the text response into <typeparamref name="T"/>.
    /// Strips markdown code fences before deserialization. When deserialization fails,
    /// retries with error context so the LLM can self-correct.
    /// </summary>
    public static async Task<T> RunAsync<T>(
        this TemporalAIAgent agent,
        IList<ChatMessage> messages,
        AgentSession? session = null,
        StructuredOutputOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new StructuredOutputOptions();
        var workingMessages = new List<ChatMessage>(messages);

        for (int attempt = 0; attempt <= options.MaxRetries; attempt++)
        {
            var response = await agent.RunAsync(
                workingMessages, session, options: null, cancellationToken);

            var text = response.Text;
            if (string.IsNullOrWhiteSpace(text))
            {
                throw new JsonException("Agent returned an empty response; cannot deserialize.");
            }

            var stripped = MarkdownCodeFenceHelper.StripMarkdownCodeFences(text);

            try
            {
                return JsonSerializer.Deserialize<T>(stripped, options.JsonSerializerOptions)
                    ?? throw new JsonException($"Deserialization returned null for type '{typeof(T).Name}'.");
            }
            catch (JsonException ex) when (attempt < options.MaxRetries)
            {
                // Append error context so the LLM can self-correct on the next attempt.
                if (options.IncludeErrorContext)
                {
                    workingMessages.Add(new ChatMessage(ChatRole.Assistant, text));
                    workingMessages.Add(new ChatMessage(ChatRole.User,
                        $"Your response could not be parsed as valid JSON. Error: {ex.Message}\n" +
                        $"Please respond with ONLY valid JSON matching the expected schema for type '{typeof(T).Name}'. " +
                        "Do not wrap it in markdown code fences."));
                }
            }
        }

        // Unreachable — the final attempt throws without catching.
        throw new InvalidOperationException("Structured output retry loop exited unexpectedly.");
    }

    /// <summary>
    /// Runs the agent proxy and deserializes the text response into <typeparamref name="T"/>.
    /// Strips markdown code fences before deserialization. When deserialization fails,
    /// retries with error context so the LLM can self-correct.
    /// </summary>
    public static async Task<T> RunAsync<T>(
        this AIAgent agent,
        IList<ChatMessage> messages,
        AgentSession session,
        StructuredOutputOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new StructuredOutputOptions();
        var workingMessages = new List<ChatMessage>(messages);

        for (int attempt = 0; attempt <= options.MaxRetries; attempt++)
        {
            var response = await agent.RunAsync(
                workingMessages, session, options: null, cancellationToken);

            var text = response.Text;
            if (string.IsNullOrWhiteSpace(text))
            {
                throw new JsonException("Agent returned an empty response; cannot deserialize.");
            }

            var stripped = MarkdownCodeFenceHelper.StripMarkdownCodeFences(text);

            try
            {
                return JsonSerializer.Deserialize<T>(stripped, options.JsonSerializerOptions)
                    ?? throw new JsonException($"Deserialization returned null for type '{typeof(T).Name}'.");
            }
            catch (JsonException ex) when (attempt < options.MaxRetries)
            {
                if (options.IncludeErrorContext)
                {
                    workingMessages.Add(new ChatMessage(ChatRole.Assistant, text));
                    workingMessages.Add(new ChatMessage(ChatRole.User,
                        $"Your response could not be parsed as valid JSON. Error: {ex.Message}\n" +
                        $"Please respond with ONLY valid JSON matching the expected schema for type '{typeof(T).Name}'. " +
                        "Do not wrap it in markdown code fences."));
                }
            }
        }

        throw new InvalidOperationException("Structured output retry loop exited unexpectedly.");
    }

    /// <summary>
    /// Runs the agent via <see cref="ITemporalAgentClient"/> and deserializes the text response
    /// into <typeparamref name="T"/>. Strips markdown code fences before deserialization.
    /// When deserialization fails, retries with error context so the LLM can self-correct.
    /// </summary>
    public static async Task<T> RunAgentAsync<T>(
        this ITemporalAgentClient client,
        TemporalAgentSessionId sessionId,
        RunRequest request,
        StructuredOutputOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new StructuredOutputOptions();
        var workingMessages = new List<ChatMessage>(request.Messages);

        for (int attempt = 0; attempt <= options.MaxRetries; attempt++)
        {
            var currentRequest = attempt == 0
                ? request
                : new RunRequest(workingMessages, request.ResponseFormat, request.EnableToolCalls, request.EnableToolNames)
                {
                    CorrelationId = Guid.NewGuid().ToString("N"),
                    OrchestrationId = request.OrchestrationId,
                };

            var response = await client.RunAgentAsync(sessionId, currentRequest, cancellationToken);

            var text = response.Text;
            if (string.IsNullOrWhiteSpace(text))
            {
                throw new JsonException("Agent returned an empty response; cannot deserialize.");
            }

            var stripped = MarkdownCodeFenceHelper.StripMarkdownCodeFences(text);

            try
            {
                return JsonSerializer.Deserialize<T>(stripped, options.JsonSerializerOptions)
                    ?? throw new JsonException($"Deserialization returned null for type '{typeof(T).Name}'.");
            }
            catch (JsonException ex) when (attempt < options.MaxRetries)
            {
                if (options.IncludeErrorContext)
                {
                    workingMessages.Add(new ChatMessage(ChatRole.Assistant, text));
                    workingMessages.Add(new ChatMessage(ChatRole.User,
                        $"Your response could not be parsed as valid JSON. Error: {ex.Message}\n" +
                        $"Please respond with ONLY valid JSON matching the expected schema for type '{typeof(T).Name}'. " +
                        "Do not wrap it in markdown code fences."));
                }
            }
        }

        throw new InvalidOperationException("Structured output retry loop exited unexpectedly.");
    }
}
