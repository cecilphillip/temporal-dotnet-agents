using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Temporalio.Extensions.AI;
using Temporalio.Extensions.Agents.HistoryStore;
using Temporalio.Extensions.Agents.State;
using Temporalio.Extensions.Agents.Workflows;
using Xunit;

namespace Temporalio.Extensions.Agents.Tests.HistoryStore;

/// <summary>
/// Unit-level coverage for Feature 1 (External History Store) cases that complement
/// <see cref="AgentHistoryStoreTests"/>. These tests focus on assertions that don't
/// require an embedded Temporal server:
/// <list type="bullet">
///   <item>Payload-level proof that <c>ConversationHistory</c> is omitted from the
///         serialized <see cref="ExecuteAgentInput"/> when external storage is on.</item>
///   <item>Round-trip of MEAI <see cref="FunctionCallContent"/> /
///         <see cref="FunctionResultContent"/> through
///         <see cref="TemporalAgentDataConverter"/> — risk-validation gate.</item>
///   <item>Strip-hook behavior on the workflow base — preserving MAF subclass-specific
///         fields (<c>OrchestrationId</c>, <c>ResponseSchema</c>, <c>Usage</c>).</item>
///   <item>Multi-turn ordering against <see cref="FakeAgentHistoryStore"/>.</item>
///   <item><c>ReduceHistoryInStoreAsync</c> trim contract.</item>
/// </list>
/// </summary>
public class ExternalHistoryStoreTests
{
    // ── Payload-level "ConversationHistory is omitted" assertions ──────────────

    /// <summary>
    /// Test 1.4 (unit-level variant): the <see cref="ExecuteAgentInput.ConversationHistory"/>
    /// property carries <c>[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]</c>.
    /// In external-store mode the workflow always passes <c>null</c>, so the serialized
    /// activity-input payload must not contain the property at all. This is the load-bearing
    /// PII / O(n^2) event-log mitigation for the entire feature — verify it byte-level.
    /// </summary>
    [Fact]
    public void ExecuteAgentInput_WithExternalStore_OmitsConversationHistoryFromPayload()
    {
        var capturing = new CapturingPayloadConverter();

        var input = new ExecuteAgentInput(
            agentName: "agent",
            request: new RunRequest("hi") { CorrelationId = "c1" },
            conversationHistory: null,
            useExternalStore: true);

        var payload = capturing.ToPayload(input);
        var json = Encoding.UTF8.GetString(payload.Data.ToByteArray());

        Assert.DoesNotContain("ConversationHistory", json, StringComparison.Ordinal);
        Assert.DoesNotContain("conversationHistory", json, StringComparison.Ordinal);
        Assert.Contains("UseExternalStore", json, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Test 1.1 / 1.4 negative case: when <c>UseExternalStore = false</c> (default), the
    /// serialized payload still includes <c>ConversationHistory</c>. Pins the byte-identical
    /// behavior promise for callers who do not opt in.
    /// </summary>
    [Fact]
    public void ExecuteAgentInput_WithoutExternalStore_RetainsConversationHistoryInPayload()
    {
        var capturing = new CapturingPayloadConverter();
        var history = new List<DurableSessionEntry>
        {
            DurableSessionRequest.FromMessages([new ChatMessage(ChatRole.User, "hi")]),
        };

        var input = new ExecuteAgentInput(
            agentName: "agent",
            request: new RunRequest("hi") { CorrelationId = "c1" },
            conversationHistory: history,
            useExternalStore: false);

        var payload = capturing.ToPayload(input);
        var json = Encoding.UTF8.GetString(payload.Data.ToByteArray());

        Assert.Contains("ConversationHistory", json, StringComparison.OrdinalIgnoreCase);
        Assert.False(input.UseExternalStore);
    }

    /// <summary>
    /// Per <c>OfInputType&lt;T&gt;()</c> the capturing converter exposes every captured
    /// instance for inspection — assert the in-memory CLR view also has
    /// <c>ConversationHistory == null</c> in external-store mode.
    /// </summary>
    [Fact]
    public void ExecuteAgentInput_CapturedClrInstance_HasNullConversationHistory_WhenExternalStoreOn()
    {
        var capturing = new CapturingPayloadConverter();

        capturing.ToPayload(new ExecuteAgentInput(
            "agent",
            new RunRequest("first") { CorrelationId = "c1" },
            conversationHistory: null,
            useExternalStore: true));
        capturing.ToPayload(new ExecuteAgentInput(
            "agent",
            new RunRequest("second") { CorrelationId = "c2" },
            conversationHistory: null,
            useExternalStore: true));

        var captured = capturing.OfInputType<ExecuteAgentInput>().ToList();
        Assert.Equal(2, captured.Count);
        Assert.All(captured, i => Assert.Null(i.ConversationHistory));
        Assert.All(captured, i => Assert.True(i.UseExternalStore));
    }

    // ── Risk-gate 1: FunctionCallContent / FunctionResultContent round-trip ────

    /// <summary>
    /// Test 1.11 + 2.12: <see cref="DurableSessionEntry"/> with
    /// <see cref="FunctionCallContent"/> and <see cref="FunctionResultContent"/> messages
    /// round-trips through <see cref="TemporalAgentDataConverter"/> with the polymorphic
    /// <c>$type</c> discriminators preserved. This proves the same converter that ships
    /// today supports the new Feature-1 / Feature-2 surface area.
    /// </summary>
    [Fact]
    public void DurableSessionEntry_WithFunctionCallContent_RoundTripsThroughDataConverter()
    {
        var assistantMsg = new ChatMessage(
            ChatRole.Assistant,
            [new FunctionCallContent(callId: "call-1", name: "lookup", arguments: new Dictionary<string, object?>
            {
                ["q"] = "weather",
            })]);
        var toolMsg = new ChatMessage(
            ChatRole.Tool,
            [new FunctionResultContent(callId: "call-1", result: "sunny")]);

        var entry = new AgentSessionRequest
        {
            CorrelationId = "corr-fc",
            CreatedAt = DateTimeOffset.UtcNow,
            Messages = [assistantMsg, toolMsg],
            OrchestrationId = "orch-fc",
        };

        var converter = TemporalAgentDataConverter.Instance.PayloadConverter;
        var payload = converter.ToPayload(entry);
        var roundTripped = (DurableSessionEntry?)converter.ToValue(payload, typeof(DurableSessionEntry));

        Assert.NotNull(roundTripped);
        var typed = Assert.IsType<AgentSessionRequest>(roundTripped);
        Assert.Equal("corr-fc", typed.CorrelationId);
        Assert.Equal("orch-fc", typed.OrchestrationId);
        Assert.Equal(2, typed.Messages.Count);

        var fc = Assert.IsType<FunctionCallContent>(typed.Messages[0].Contents[0]);
        Assert.Equal("call-1", fc.CallId);
        Assert.Equal("lookup", fc.Name);

        var fr = Assert.IsType<FunctionResultContent>(typed.Messages[1].Contents[0]);
        Assert.Equal("call-1", fr.CallId);
        Assert.Equal("sunny", fr.Result?.ToString());
    }

    // ── Strip-hook behavior (Tank's deviation #1 from plan) ────────────────────

    /// <summary>
    /// Verifies the workflow's MAF-specific override of <c>StripMessagesFromEntry</c>
    /// preserves <see cref="AgentSessionRequest.OrchestrationId"/>,
    /// <see cref="AgentSessionRequest.ResponseType"/>, and
    /// <see cref="AgentSessionRequest.ResponseSchema"/> while clearing
    /// <c>Messages</c>. These fields drive routing / structured-output behavior on
    /// the next replay; losing them silently would corrupt long-lived sessions.
    /// </summary>
    [Fact]
    public void StripMessagesFromEntry_AgentSessionRequest_PreservesSubclassFields()
    {
        var schema = JsonSerializer.SerializeToElement(new { type = "object" });
        var original = new AgentSessionRequest
        {
            CorrelationId = "corr-1",
            CreatedAt = DateTimeOffset.UtcNow,
            Messages = [new ChatMessage(ChatRole.User, "secret content")],
            OrchestrationId = "orch-1",
            ResponseType = "json",
            ResponseSchema = schema,
        };

        var stripped = (AgentSessionRequest)new TestableAgentWorkflow().PublicStripMessagesFromEntry(original);

        Assert.Empty(stripped.Messages);
        Assert.Equal("corr-1", stripped.CorrelationId);
        Assert.Equal(original.CreatedAt, stripped.CreatedAt);
        Assert.Equal("orch-1", stripped.OrchestrationId);
        Assert.Equal("json", stripped.ResponseType);
        Assert.NotNull(stripped.ResponseSchema);
        // schema content survives the strip
        Assert.Equal(
            JsonSerializer.Serialize(schema),
            JsonSerializer.Serialize(stripped.ResponseSchema!.Value));
    }

    /// <summary>
    /// Mirror coverage for the response-side strip: <see cref="AgentSessionResponse.Usage"/>
    /// must survive (the activity-emitted token counts feed dashboards / cost accounting),
    /// while message content is dropped.
    /// </summary>
    [Fact]
    public void StripMessagesFromEntry_AgentSessionResponse_PreservesUsage()
    {
        var usage = new UsageDetails
        {
            InputTokenCount = 42,
            OutputTokenCount = 17,
            TotalTokenCount = 59,
        };
        var original = new AgentSessionResponse
        {
            CorrelationId = "corr-1",
            CreatedAt = DateTimeOffset.UtcNow,
            Messages = [new ChatMessage(ChatRole.Assistant, "secret response")],
            Usage = usage,
        };

        var stripped = (AgentSessionResponse)new TestableAgentWorkflow().PublicStripMessagesFromEntry(original);

        Assert.Empty(stripped.Messages);
        Assert.Equal("corr-1", stripped.CorrelationId);
        Assert.Equal(original.CreatedAt, stripped.CreatedAt);
        Assert.NotNull(stripped.Usage);
        Assert.Equal(42, stripped.Usage!.InputTokenCount);
        Assert.Equal(17, stripped.Usage.OutputTokenCount);
        Assert.Equal(59, stripped.Usage.TotalTokenCount);
    }

    /// <summary>
    /// Strip-hook fallthrough: a non-MAF subtype of <see cref="DurableSessionEntry"/>
    /// must still be stripped by the base implementation (assistant messages cleared,
    /// correlation/timestamp preserved). This confirms the MAF override pattern of
    /// "branch on MAF types, then delegate to base for the rest" works.
    /// </summary>
    [Fact]
    public void StripMessagesFromEntry_BaseDurableSessionRequest_FallsThroughToBaseImpl()
    {
        var baseEntry = DurableSessionRequest.FromMessages(
            [new ChatMessage(ChatRole.User, "raw text")]);
        var stripped = new TestableAgentWorkflow().PublicStripMessagesFromEntry(baseEntry);

        var asReq = Assert.IsType<DurableSessionRequest>(stripped);
        Assert.Empty(asReq.Messages);
        Assert.Equal(baseEntry.CorrelationId, asReq.CorrelationId);
    }

    /// <summary>
    /// <c>ShouldStripMessagesFromHistoryEntry</c> tracks the workflow input's
    /// <c>UseExternalStore</c> flag. Verify both sides of the toggle.
    /// </summary>
    [Fact]
    public void ShouldStripMessagesFromHistoryEntry_TracksUseExternalStoreFlag()
    {
        var wf = new TestableAgentWorkflow();

        wf.SetInputForTest(useExternalStore: false);
        Assert.False(wf.PublicShouldStripMessagesFromHistoryEntry());

        wf.SetInputForTest(useExternalStore: true);
        Assert.True(wf.PublicShouldStripMessagesFromHistoryEntry());
    }

    // ── FakeAgentHistoryStore ordering / call-recording ────────────────────────

    /// <summary>
    /// Direct assertion of the planned activity contract: turn 1 calls
    /// <see cref="IAgentHistoryStore.LoadAsync"/> first (returns empty), then
    /// <see cref="IAgentHistoryStore.AppendAsync"/> with the new request + response entries.
    /// Turn 2's <c>LoadAsync</c> sees turn-1's appended entries. Append calls preserve
    /// chronological order across turns.
    /// </summary>
    [Fact]
    public async Task FakeAgentHistoryStore_TwoTurnRoundTrip_PreservesOrderAcrossTurns()
    {
        IAgentHistoryStore store = new FakeAgentHistoryStore();
        const string sessionId = "ta-agent-session-1";

        // Turn 1 — empty store
        var loaded1 = await store.LoadAsync(sessionId);
        Assert.Empty(loaded1);

        var t1Req = new AgentSessionRequest
        {
            CorrelationId = "c1",
            CreatedAt = DateTimeOffset.UtcNow,
            Messages = [new ChatMessage(ChatRole.User, "u1")],
        };
        var t1Resp = new AgentSessionResponse
        {
            CorrelationId = "c1",
            CreatedAt = DateTimeOffset.UtcNow,
            Messages = [new ChatMessage(ChatRole.Assistant, "a1")],
        };
        await store.AppendAsync(sessionId, [t1Req, t1Resp]);

        // Turn 2 — store has turn-1's entries
        var loaded2 = await store.LoadAsync(sessionId);
        Assert.Equal(2, loaded2.Count);
        Assert.Equal("c1", loaded2[0].CorrelationId);
        Assert.Equal("c1", loaded2[1].CorrelationId);
        Assert.IsType<AgentSessionRequest>(loaded2[0]);
        Assert.IsType<AgentSessionResponse>(loaded2[1]);

        var t2Req = new AgentSessionRequest
        {
            CorrelationId = "c2",
            CreatedAt = DateTimeOffset.UtcNow,
            Messages = [new ChatMessage(ChatRole.User, "u2")],
        };
        var t2Resp = new AgentSessionResponse
        {
            CorrelationId = "c2",
            CreatedAt = DateTimeOffset.UtcNow,
            Messages = [new ChatMessage(ChatRole.Assistant, "a2")],
        };
        await store.AppendAsync(sessionId, [t2Req, t2Resp]);

        // Final view: all four entries in chronological order
        var final = await store.LoadAsync(sessionId);
        Assert.Equal(4, final.Count);
        Assert.Equal("c1", final[0].CorrelationId);
        Assert.Equal("c1", final[1].CorrelationId);
        Assert.Equal("c2", final[2].CorrelationId);
        Assert.Equal("c2", final[3].CorrelationId);

        // Recorded operations: Load, Append, Load, Append, Load
        var fake = (FakeAgentHistoryStore)store;
        var ops = fake.Calls.Select(c => c.Operation).ToList();
        Assert.Equal(
            [
                HistoryStoreOperation.Load,
                HistoryStoreOperation.Append,
                HistoryStoreOperation.Load,
                HistoryStoreOperation.Append,
                HistoryStoreOperation.Load,
            ],
            ops);
        Assert.Equal(2, fake.AppendCount);
        Assert.Equal(3, fake.LoadCount);
    }

    /// <summary>
    /// The plan's "concurrent turns serialize via the workflow mutex" claim is implicit on
    /// the workflow side. At the store level the relevant guarantee is: when two
    /// <c>AppendAsync</c> calls are issued sequentially (as the mutex enforces), their
    /// observed entry order matches the call order. This test verifies the fake's
    /// append-ordering behavior — the workflow-side serialization is covered by the
    /// existing mutex tests in <c>AgentWorkflowWrapperTests</c>.
    /// </summary>
    [Fact]
    public async Task FakeAgentHistoryStore_AppendOrdering_IsStableAcrossSequentialCalls()
    {
        IAgentHistoryStore store = new FakeAgentHistoryStore();
        const string sessionId = "ta-ordering";

        for (int i = 0; i < 5; i++)
        {
            var req = new AgentSessionRequest
            {
                CorrelationId = $"c{i}",
                CreatedAt = DateTimeOffset.UtcNow,
                Messages = [new ChatMessage(ChatRole.User, $"u{i}")],
            };
            await store.AppendAsync(sessionId, [req]);
        }

        var entries = await store.LoadAsync(sessionId);
        Assert.Equal(5, entries.Count);
        for (int i = 0; i < 5; i++)
        {
            Assert.Equal($"c{i}", entries[i].CorrelationId);
        }
    }

    /// <summary>
    /// <c>ReplaceAsync</c> wholesale-replaces the session (used by
    /// <c>ReduceHistoryInStoreAsync</c> at continue-as-new time).
    /// </summary>
    [Fact]
    public async Task FakeAgentHistoryStore_ReplaceAsync_OverwritesPriorEntries()
    {
        IAgentHistoryStore store = new FakeAgentHistoryStore();
        const string sessionId = "ta-replace";

        // Seed 4 entries
        for (int i = 0; i < 4; i++)
        {
            await store.AppendAsync(sessionId,
            [
                new AgentSessionRequest
                {
                    CorrelationId = $"c{i}",
                    CreatedAt = DateTimeOffset.UtcNow,
                    Messages = [new ChatMessage(ChatRole.User, $"u{i}")],
                }
            ]);
        }

        // Replace with last 2
        var loaded = await store.LoadAsync(sessionId);
        var trimmed = loaded.Skip(loaded.Count - 2).ToList();
        await store.ReplaceAsync(sessionId, trimmed);

        var afterReplace = await store.LoadAsync(sessionId);
        Assert.Equal(2, afterReplace.Count);
        Assert.Equal("c2", afterReplace[0].CorrelationId);
        Assert.Equal("c3", afterReplace[1].CorrelationId);

        Assert.Equal(1, ((FakeAgentHistoryStore)store).ReplaceCount);
    }

    // ── Migration-path / input contract ────────────────────────────────────────

    /// <summary>
    /// Test 1.8 (unit-level invariant): the activity branch is gated on
    /// <see cref="ExecuteAgentInput.UseExternalStore"/> — NOT on
    /// <see cref="TemporalAgentsOptions.UseExternalHistory"/>. So a workflow that was
    /// started before the upgrade carries <c>UseExternalStore = false</c> and continues
    /// through the in-memory branch even after the worker is redeployed with
    /// <c>UseExternalHistory = true</c>. This test pins the input contract that makes the
    /// migration safe.
    /// </summary>
    [Fact]
    public void ExecuteAgentInput_UseExternalStoreFlag_IsLoadBearingPerInvocation()
    {
        var legacyInput = new ExecuteAgentInput(
            "agent",
            new RunRequest("hi") { CorrelationId = "c1" },
            conversationHistory:
            [
                DurableSessionRequest.FromMessages([new ChatMessage(ChatRole.User, "hi")]),
            ],
            useExternalStore: false);
        var newInput = new ExecuteAgentInput(
            "agent",
            new RunRequest("hi") { CorrelationId = "c1" },
            conversationHistory: null,
            useExternalStore: true);

        Assert.False(legacyInput.UseExternalStore);
        Assert.NotNull(legacyInput.ConversationHistory);
        Assert.True(newInput.UseExternalStore);
        Assert.Null(newInput.ConversationHistory);
    }

    // ── Subclass that exposes protected hooks on AgentWorkflow ────────────────

    /// <summary>
    /// Test-only subclass that exposes <see cref="AgentWorkflow.StripMessagesFromEntry"/>
    /// and <see cref="AgentWorkflow.ShouldStripMessagesFromHistoryEntry"/> for assertion
    /// without standing up the workflow runtime. We also expose a hook to set the
    /// internal MAF input directly (the production <c>RunAsync</c> populates it; tests
    /// short-circuit).
    /// </summary>
    private sealed class TestableAgentWorkflow : AgentWorkflow
    {
        public DurableSessionEntry PublicStripMessagesFromEntry(DurableSessionEntry entry) =>
            StripMessagesFromEntry(entry);

        public bool PublicShouldStripMessagesFromHistoryEntry() =>
            ShouldStripMessagesFromHistoryEntry();

        public void SetInputForTest(bool useExternalStore)
        {
            // The base's _input field is private; assign through the typed AgentWorkflowInput
            // via reflection. Keeping this isolated to the test fixture.
            var field = typeof(AgentWorkflow).GetField(
                "_input",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            Assert.NotNull(field);
            field!.SetValue(this, new AgentWorkflowInput
            {
                AgentName = "agent",
                TaskQueue = "tq",
                UseExternalStore = useExternalStore,
            });
        }
    }
}
