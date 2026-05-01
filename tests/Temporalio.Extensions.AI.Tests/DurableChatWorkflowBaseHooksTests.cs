using Microsoft.Extensions.AI;
using Temporalio.Workflows;
using Xunit;

namespace Temporalio.Extensions.AI.Tests;

/// <summary>
/// Unit tests for <see cref="DurableChatWorkflowBase{TOutput}"/> protected hooks added in
/// Layer 3 Phase 1 — <c>InitializeTurnCount</c> (Decision #3) and <c>UpsertCustomSearchAttributes</c>
/// (Decision #4). These hooks are exercised by sub-classing the abstract base in a test-only
/// derived type that exposes the protected members.
/// </summary>
public class DurableChatWorkflowBaseHooksTests
{
    /// <summary>
    /// Test subclass that exposes the protected hooks for direct unit testing without
    /// spinning up a full workflow runtime.
    /// </summary>
    private sealed class TestableWorkflow : DurableChatWorkflowBase<ChatResponse>
    {
        public int CustomUpsertCallCount { get; private set; }

        public int InvokeInitializeTurnCount(IReadOnlyList<DurableSessionEntry> carriedHistory)
            => InitializeTurnCount(carriedHistory);

        public void InvokeUpsertCustomSearchAttributes()
            => UpsertCustomSearchAttributes();

        protected override DurableSessionResponse BuildResponseEntry(
            string correlationId, ChatResponse output, DateTimeOffset createdAt)
            => DurableSessionResponse.FromChatResponse(correlationId, output, createdAt);

        protected override Task<ChatResponse> ExecuteTurnAsync(
            ActivityOptions activityOptions,
            DurableSessionRequest requestEntry,
            ChatOptions? chatOptions)
            => Task.FromResult(new ChatResponse());

        protected override ContinueAsNewException CreateContinueAsNewException(
            DurableChatWorkflowInput input)
            => throw new InvalidOperationException("Not used in this test.");

        protected override void UpsertCustomSearchAttributes()
        {
            CustomUpsertCallCount++;
        }
    }

    private sealed class DefaultHooksWorkflow : DurableChatWorkflowBase<ChatResponse>
    {
        public int InvokeInitializeTurnCount(IReadOnlyList<DurableSessionEntry> carriedHistory)
            => InitializeTurnCount(carriedHistory);

        public void InvokeUpsertCustomSearchAttributes()
            => UpsertCustomSearchAttributes();

        protected override DurableSessionResponse BuildResponseEntry(
            string correlationId, ChatResponse output, DateTimeOffset createdAt)
            => DurableSessionResponse.FromChatResponse(correlationId, output, createdAt);

        protected override Task<ChatResponse> ExecuteTurnAsync(
            ActivityOptions activityOptions,
            DurableSessionRequest requestEntry,
            ChatOptions? chatOptions)
            => Task.FromResult(new ChatResponse());

        protected override ContinueAsNewException CreateContinueAsNewException(
            DurableChatWorkflowInput input)
            => throw new InvalidOperationException("Not used in this test.");
    }

    // ── InitializeTurnCount ─────────────────────────────────────────────────

    [Fact]
    public void InitializeTurnCount_WithEmptyHistory_ReturnsZero()
    {
        var wf = new TestableWorkflow();
        Assert.Equal(0, wf.InvokeInitializeTurnCount(Array.Empty<DurableSessionEntry>()));
    }

    [Fact]
    public void InitializeTurnCount_CountsResponseEntriesOnly_NotRequestEntries()
    {
        // 3 turns = 3 requests + 3 responses. Initial turn count should be 3 (the response count),
        // not 6 (the total entry count). This preserves monotonic growth across CAN transitions
        // per Layer 3 Decision #3.
        var ts = new DateTimeOffset(2026, 4, 30, 0, 0, 0, TimeSpan.Zero);
        var history = new List<DurableSessionEntry>
        {
            new DurableSessionRequest { CorrelationId = "c1", CreatedAt = ts },
            new DurableSessionResponse { CorrelationId = "c1", CreatedAt = ts },
            new DurableSessionRequest { CorrelationId = "c2", CreatedAt = ts },
            new DurableSessionResponse { CorrelationId = "c2", CreatedAt = ts },
            new DurableSessionRequest { CorrelationId = "c3", CreatedAt = ts },
            new DurableSessionResponse { CorrelationId = "c3", CreatedAt = ts },
        };

        var wf = new TestableWorkflow();
        Assert.Equal(3, wf.InvokeInitializeTurnCount(history));
    }

    [Fact]
    public void InitializeTurnCount_HandlesUnbalancedHistory_OrphanRequestNotCounted()
    {
        // A pending in-flight request (no response yet) should not count toward turns —
        // the count tracks completed turns.
        var ts = DateTimeOffset.UtcNow;
        var history = new List<DurableSessionEntry>
        {
            new DurableSessionRequest { CorrelationId = "c1", CreatedAt = ts },
            new DurableSessionResponse { CorrelationId = "c1", CreatedAt = ts },
            new DurableSessionRequest { CorrelationId = "c2", CreatedAt = ts }, // orphan
        };

        var wf = new TestableWorkflow();
        Assert.Equal(1, wf.InvokeInitializeTurnCount(history));
    }

    // ── UpsertCustomSearchAttributes ────────────────────────────────────────

    [Fact]
    public void UpsertCustomSearchAttributes_DefaultIsNoOp()
    {
        // The default implementation should not throw and should not require workflow context —
        // verifies that subclasses that don't need custom attributes pay nothing for the hook.
        var wf = new DefaultHooksWorkflow();
        wf.InvokeUpsertCustomSearchAttributes();
        // Reaching this line without throwing is the assertion.
    }

    [Fact]
    public void UpsertCustomSearchAttributes_SubclassOverride_IsInvoked()
    {
        var wf = new TestableWorkflow();
        Assert.Equal(0, wf.CustomUpsertCallCount);

        wf.InvokeUpsertCustomSearchAttributes();
        Assert.Equal(1, wf.CustomUpsertCallCount);

        wf.InvokeUpsertCustomSearchAttributes();
        Assert.Equal(2, wf.CustomUpsertCallCount);
    }
}
