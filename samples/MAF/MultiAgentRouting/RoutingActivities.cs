using Temporalio.Activities;

namespace MultiAgentRouting;

/// <summary>
/// Activities that classify an incoming user request and return the name of the
/// best-matching specialist agent.
///
/// Running routing logic here (rather than inline in the workflow) means:
///   • The routing decision is recorded in Temporal's event history.
///   • A worker crash after classification won't re-run the classifier.
///   • The decision is visible and auditable in the Temporal Web UI.
///   • The activity is retried automatically on transient failures.
/// </summary>
public class RoutingActivities
{
    private static readonly Dictionary<string, string[]> AgentKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        ["WeatherAgent"]    = ["weather", "forecast", "rain", "snow", "temperature", "climate", "wind", "storm", "sunny", "humidity"],
        ["BillingAgent"]    = ["invoice", "charge", "payment", "billing", "refund", "fee", "subscription", "cost", "price", "receipt"],
        ["TechSupportAgent"] = ["crash", "error", "bug", "issue", "problem", "broken", "fix", "exception", "null", "not working", "slow"],
    };

    /// <summary>
    /// Inspects <paramref name="userQuestion"/> and returns the name of the
    /// best-matching registered specialist agent.
    /// Falls back to <c>WeatherAgent</c> when no keywords match.
    /// </summary>
    [Activity]
    public string ClassifyRequest(string userQuestion)
    {
        if (string.IsNullOrWhiteSpace(userQuestion))
        {
            return "WeatherAgent";
        }

        var scores = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var (agentName, keywords) in AgentKeywords)
        {
            var score = keywords.Count(kw =>
                userQuestion.Contains(kw, StringComparison.OrdinalIgnoreCase));
            scores[agentName] = score;
        }

        var best = scores.MaxBy(kv => kv.Value);

        // If no keywords matched (all scores are 0), default to TechSupportAgent
        // as a general-purpose fallback.
        return best.Value > 0 ? best.Key : "TechSupportAgent";
    }
}
