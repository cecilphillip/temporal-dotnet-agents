using Microsoft.Extensions.AI;
using Temporalio.Workflows;
using static Temporalio.Extensions.Agents.TemporalWorkflowExtensions;

namespace EvaluatorOptimizer;

/// <summary>
/// Orchestrating workflow that implements the Evaluator-Optimizer pattern:
/// a Generator agent iteratively produces drafts, while an Evaluator agent
/// reviews them and either approves or provides actionable feedback for revision.
/// </summary>
/// <remarks>
/// Both agents run as durable Temporal activities, so:
/// <list type="bullet">
///   <item>Each generation/evaluation turn is independently retried on failure.</item>
///   <item>If the worker crashes mid-loop, the workflow resumes from the last completed turn.</item>
///   <item>The full revision history is preserved in the workflow event history.</item>
/// </list>
/// </remarks>
[Workflow("EvaluatorOptimizer.EvaluatorOptimizerWorkflow")]
public class EvaluatorOptimizerWorkflow
{
    private const string ApprovedSignal = "APPROVED";

    /// <summary>
    /// Runs the evaluator-optimizer loop for a given <paramref name="task"/>.
    /// </summary>
    /// <param name="task">The writing or generation task description.</param>
    /// <param name="maxIterations">
    /// Maximum number of generator+evaluator cycles before returning the best draft found.
    /// Defaults to 3.
    /// </param>
    /// <returns>The final approved (or best) draft.</returns>
    [WorkflowRun]
    public async Task<string> RunAsync(string task, int maxIterations = 3)
    {
        var generator = GetAgent("Generator");
        var evaluator = GetAgent("Evaluator");

        var genSession = await generator.CreateSessionAsync();
        var evalSession = await evaluator.CreateSessionAsync();

        string draft = string.Empty;
        string feedback = string.Empty;

        for (int i = 0; i < maxIterations; i++)
        {
            // ── Generation turn ────────────────────────────────────────────
            var genPrompt = i == 0
                ? task
                : $"Revise your previous draft based on this feedback:\n\n{feedback}\n\nPrevious draft:\n{draft}";

            var genResponse = await generator.RunAsync(
                [new ChatMessage(ChatRole.User, genPrompt)],
                genSession);

            draft = genResponse.Text ?? string.Empty;

            // ── Evaluation turn ────────────────────────────────────────────
            var evalResponse = await evaluator.RunAsync(
                [new ChatMessage(ChatRole.User,
                    $"Evaluate the following draft. " +
                    $"Reply with '{ApprovedSignal}' if it is ready, " +
                    $"or give concise, specific feedback for improvement.\n\n{draft}")],
                evalSession);

            feedback = evalResponse.Text ?? string.Empty;

            if (feedback.Contains(ApprovedSignal, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }
        }

        return draft;
    }
}
