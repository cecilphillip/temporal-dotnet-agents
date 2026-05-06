using System.ComponentModel;
using Microsoft.Extensions.AI;

namespace Temporalio.Extensions.Agents.Tests.StepMode;

/// <summary>
/// Test fixture for the per-tool-activity (step mode) feature. Wraps a tool that
/// records every invocation: call count, raw arguments, and the wall-clock time
/// the call started. Behavior is configurable via <see cref="Behavior"/> for
/// retry, idempotency, and crash-safety scenarios.
/// </summary>
/// <remarks>
/// <para>
/// Constructed via <see cref="Build"/> which returns the underlying
/// <see cref="AIFunction"/> ready to be registered with <c>AddDurableTools(...)</c>.
/// The <see cref="RecordingTool"/> instance retains a reference to the call log
/// so tests can assert against it after the run.
/// </para>
/// <para>
/// The <see cref="CallCount"/> uses <see cref="Interlocked.Increment(ref int)"/>
/// so it is safe to read across threads — important for the crash-safety test
/// that asserts the tool ran exactly once across worker restarts.
/// </para>
/// </remarks>
internal sealed class RecordingTool
{
    private int _callCount;
    private readonly List<RecordedInvocation> _invocations = [];
    private readonly object _gate = new();

    /// <summary>Function name as exposed to the LLM and to <c>DurableFunctionRegistry</c>.</summary>
    public required string Name { get; init; }

    /// <summary>Description shown to the LLM in the tool schema.</summary>
    public string Description { get; init; } = "Recording tool used by step-mode tests.";

    /// <summary>Configurable behavior — controls success/failure semantics.</summary>
    public RecordingToolBehavior Behavior { get; init; } = RecordingToolBehavior.AlwaysSucceed;

    /// <summary>Result returned on success (default: a deterministic echo).</summary>
    public Func<string, string>? SuccessResultFactory { get; init; }

    /// <summary>Total times <see cref="InvokeAsync"/> has been entered.</summary>
    public int CallCount => Volatile.Read(ref _callCount);

    /// <summary>Snapshot of every recorded invocation, in arrival order.</summary>
    public IReadOnlyList<RecordedInvocation> Invocations
    {
        get
        {
            lock (_gate)
                return _invocations.ToArray();
        }
    }

    /// <summary>Builds the durable-tool-compatible <see cref="AIFunction"/>.</summary>
    public AIFunction Build() =>
        AIFunctionFactory.Create(
            (string input) => InvokeAsync(input),
            new AIFunctionFactoryOptions { Name = Name, Description = Description });

    private async Task<string> InvokeAsync([Description("Free-form tool input.")] string input)
    {
        var n = Interlocked.Increment(ref _callCount);
        var invocation = new RecordedInvocation(
            CallNumber: n,
            Input: input,
            StartedAt: DateTimeOffset.UtcNow,
            ManagedThreadId: Environment.CurrentManagedThreadId);

        lock (_gate)
            _invocations.Add(invocation);

        await Task.Yield();

        switch (Behavior)
        {
            case RecordingToolBehavior.AlwaysSucceed:
                break;
            case RecordingToolBehavior.AlwaysFail:
                throw new InvalidOperationException(
                    $"RecordingTool '{Name}' configured to always fail (call #{n}).");
            case RecordingToolBehavior.FailOnceThenSucceed:
                if (n == 1)
                    throw new InvalidOperationException(
                        $"RecordingTool '{Name}' configured to fail once (call #{n}).");
                break;
        }

        return SuccessResultFactory?.Invoke(input) ?? $"{Name}({input})";
    }

    internal sealed record RecordedInvocation(
        int CallNumber,
        string Input,
        DateTimeOffset StartedAt,
        int ManagedThreadId);
}

/// <summary>Failure modes a <see cref="RecordingTool"/> can simulate.</summary>
internal enum RecordingToolBehavior
{
    /// <summary>Tool returns successfully on every invocation.</summary>
    AlwaysSucceed,

    /// <summary>Tool throws on every invocation — drives "no retry on MaximumAttempts=1" tests.</summary>
    AlwaysFail,

    /// <summary>Tool throws on call #1 and succeeds thereafter — drives default-retry tests.</summary>
    FailOnceThenSucceed,
}
