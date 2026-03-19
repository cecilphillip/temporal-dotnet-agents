using Microsoft.Extensions.AI;
using Temporalio.Workflows;

namespace Temporalio.Extensions.AI;

/// <summary>
/// An <see cref="IChatReducer"/> that stores the full (unreduced) conversation history
/// in Temporal workflow state and delegates the actual reduction logic to an inner reducer.
/// </summary>
/// <remarks>
/// <para>
/// Use this reducer when you need both:
/// <list type="bullet">
///   <item>A sliding context window for the LLM (via the inner reducer)</item>
///   <item>Full conversation persistence (via the workflow's history state)</item>
/// </list>
/// </para>
/// <para>
/// When running outside a Temporal workflow, the reducer delegates directly to the
/// inner reducer without storing anything — it behaves as a transparent pass-through.
/// </para>
/// </remarks>
public sealed class DurableChatReducer : IChatReducer
{
    private readonly IChatReducer _innerReducer;

    // Full history is stored here when running inside a workflow.
    // This list is only mutated from workflow code (deterministic context).
    private readonly List<ChatMessage> _fullHistory = [];

    /// <summary>
    /// Initializes a new instance of the <see cref="DurableChatReducer"/> class.
    /// </summary>
    /// <param name="innerReducer">
    /// The reducer that performs actual reduction logic (e.g., <c>MessageCountingChatReducer</c>).
    /// </param>
    public DurableChatReducer(IChatReducer innerReducer)
    {
        ArgumentNullException.ThrowIfNull(innerReducer);
        _innerReducer = innerReducer;
    }

    /// <summary>
    /// Gets the full (unreduced) conversation history.
    /// Only populated when running inside a Temporal workflow.
    /// </summary>
    public IReadOnlyList<ChatMessage> FullHistory => _fullHistory;

    /// <summary>
    /// Reduces the messages using the inner reducer.
    /// When inside a workflow, the full (unreduced) history is preserved before reduction.
    /// </summary>
    public async Task<IEnumerable<ChatMessage>> ReduceAsync(
        IEnumerable<ChatMessage> messages,
        CancellationToken cancellationToken)
    {
        var messageList = messages as IList<ChatMessage> ?? messages.ToList();

        if (Workflow.InWorkflow)
        {
            // Store any new messages not yet in our full history.
            // Messages accumulate over turns — we only add the delta.
            if (messageList.Count > _fullHistory.Count)
            {
                for (int i = _fullHistory.Count; i < messageList.Count; i++)
                {
                    _fullHistory.Add(messageList[i]);
                }
            }
        }

        // Delegate the actual reduction to the inner reducer.
        return await _innerReducer.ReduceAsync(messageList, cancellationToken)
            .ConfigureAwait(false);
    }
}
