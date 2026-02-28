using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Temporalio.Extensions.Agents.IntegrationTests.Helpers;

/// <summary>
/// A test agent that throws an exception on its first N calls and returns a
/// normal echo response on subsequent calls. Used to test Temporal's activity
/// retry mechanism and error propagation.
/// </summary>
internal sealed class FailThenSucceedAIAgent : TestAgentBase
{
    private readonly int _failCount;
    private int _callCount;

    /// <param name="name">Agent name for registration.</param>
    /// <param name="failCount">Number of initial calls that throw (default 1).</param>
    public FailThenSucceedAIAgent(string name, int failCount = 1) : base(name)
    {
        _failCount = failCount;
    }

    /// <summary>Total number of times <see cref="RunCoreAsync"/> has been entered.</summary>
    public int CallCount => _callCount;

    protected override Task<AgentResponse> RunCoreAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session = null,
        AgentRunOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        int call = Interlocked.Increment(ref _callCount);

        if (call <= _failCount)
        {
            throw new InvalidOperationException(
                $"Simulated agent failure (attempt {call} of {_failCount} planned failures)");
        }

        return Task.FromResult(CreateEchoResponse(messages));
    }
}
