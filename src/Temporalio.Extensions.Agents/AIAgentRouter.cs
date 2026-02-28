using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Temporalio.Extensions.Agents;

/// <summary>
/// Default <see cref="IAgentRouter"/> implementation that calls an AI model-backed
/// <see cref="AIAgent"/> to classify which registered agent should handle a request.
/// </summary>
/// <remarks>
/// The router agent receives a compact prompt listing agent names + descriptions and
/// is instructed to respond with the single best-matching agent name. The response is
/// parsed with a fuzzy match fallback to tolerate minor formatting variation.
/// <para>
/// Register a router agent via <see cref="TemporalAgentsOptions.SetRouterAgent"/>; the
/// <see cref="AIAgentRouter"/> is registered automatically when a router agent is present.
/// </para>
/// </remarks>
public sealed class AIAgentRouter(AIAgent routerAgent) : IAgentRouter
{
    /// <inheritdoc/>
    public async Task<string> RouteAsync(
        IList<ChatMessage> messages,
        IEnumerable<AgentDescriptor> agents,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(agents);

        var descriptors = agents.ToList();
        if (descriptors.Count == 0)
        {
            throw new InvalidOperationException(
                "No agent descriptors are registered. Call AddAgentDescriptor() on TemporalAgentsOptions for each routable agent.");
        }

        var agentList = string.Join("\n", descriptors.Select(a => $"- {a.Name}: {a.Description}"));
        var lastUserMessage = messages
            .LastOrDefault(m => m.Role == ChatRole.User)?.Text
            ?? messages.LastOrDefault()?.Text
            ?? string.Empty;

        var routingMessages = new List<ChatMessage>
        {
            new(ChatRole.User,
                $"Available agents:\n{agentList}\n\n" +
                $"User message: {lastUserMessage}\n\n" +
                "Respond with ONLY the agent name, nothing else.")
        };

        var session = await routerAgent.CreateSessionAsync(cancellationToken).ConfigureAwait(false);
        var response = await routerAgent
            .RunAsync(routingMessages, session, null, cancellationToken)
            .ConfigureAwait(false);

        var responseText = response.Text?.Trim() ?? string.Empty;
        var validNames = descriptors
            .Select(a => a.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Exact match (most likely case when the model follows instructions).
        if (validNames.Contains(responseText))
        {
            return responseText;
        }

        // Fuzzy fallback: find any valid name contained in the response text.
        var match = validNames
            .FirstOrDefault(n => responseText.Contains(n, StringComparison.OrdinalIgnoreCase));

        if (match is not null)
        {
            return match;
        }

        throw new InvalidOperationException(
            $"Router agent returned an unrecognized agent name: '{responseText}'. " +
            $"Valid names: {string.Join(", ", validNames)}");
    }
}
