using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Temporalio.Extensions.Agents.IntegrationTests.Helpers;

/// <summary>
/// A test agent that delays on its first N calls and responds instantly on subsequent calls.
/// Used to test activity timeout and heartbeat timeout enforcement: the first attempt
/// times out (due to the long delay), Temporal retries, and the retry succeeds immediately.
/// </summary>
internal sealed class SlowThenFastAIAgent : TestAgentBase
{
    private readonly TimeSpan _slowDelay;
    private readonly int _maxSlowCalls;
    private int _callCount;

    /// <param name="name">Agent name for registration.</param>
    /// <param name="slowDelay">How long to delay on slow calls.</param>
    /// <param name="maxSlowCalls">Number of initial calls that are slow (default 1).</param>
    public SlowThenFastAIAgent(string name, TimeSpan slowDelay, int maxSlowCalls = 1)
        : base(name)
    {
        _slowDelay = slowDelay;
        _maxSlowCalls = maxSlowCalls;
    }

    /// <summary>Total number of times <see cref="RunCoreAsync"/> has been entered.</summary>
    public int CallCount => _callCount;

    protected override async Task<AgentResponse> RunCoreAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session = null,
        AgentRunOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        int call = Interlocked.Increment(ref _callCount);

        if (call <= _maxSlowCalls)
        {
            // This delay will be interrupted by the activity CancellationToken
            // when the StartToClose or Heartbeat timeout fires.
            await Task.Delay(_slowDelay, cancellationToken);
        }

        return CreateEchoResponse(messages);
    }
}
