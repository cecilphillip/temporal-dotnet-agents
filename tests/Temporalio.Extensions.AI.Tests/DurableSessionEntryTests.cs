using System.Text.Json;
using Microsoft.Extensions.AI;
using Temporalio.Converters;
using Xunit;

namespace Temporalio.Extensions.AI.Tests;

/// <summary>
/// Phase 1 round-trip tests for <see cref="DurableSessionEntry"/> and its concrete
/// subclasses, exercising serialization through <see cref="DurableAIDataConverter.Instance"/>.
/// </summary>
public class DurableSessionEntryTests
{
    private static readonly IPayloadConverter s_converter = DurableAIDataConverter.Instance.PayloadConverter;

    private static T RoundTrip<T>(T original) where T : class
    {
        var payload = s_converter.ToPayload(original);
        return (T)s_converter.ToValue(payload, typeof(T))!;
    }

    // ─── DurableSessionRequest round-trip ───────────────────────────────────

    [Fact]
    public void DurableSessionRequest_RoundTrip_PreservesCoreFieldsAndPolymorphicMessages()
    {
        var createdAt = new DateTimeOffset(2026, 4, 30, 12, 0, 0, TimeSpan.Zero);
        var original = new DurableSessionRequest
        {
            CorrelationId = "corr-req-1",
            CreatedAt = createdAt,
            Messages = new List<ChatMessage>
            {
                new(ChatRole.User, [new TextContent("hello")]),
                new(ChatRole.Assistant, [
                    new FunctionCallContent("call-1", "get_weather",
                        new Dictionary<string, object?> { ["city"] = "Seattle" })
                ]),
            },
        };

        var roundTripped = RoundTrip(original);

        Assert.Equal("corr-req-1", roundTripped.CorrelationId);
        Assert.Equal(createdAt, roundTripped.CreatedAt);
        Assert.Equal(2, roundTripped.Messages.Count);
        Assert.IsType<TextContent>(roundTripped.Messages[0].Contents[0]);
        var fc = Assert.IsType<FunctionCallContent>(roundTripped.Messages[1].Contents[0]);
        Assert.Equal("call-1", fc.CallId);
        Assert.Equal("get_weather", fc.Name);
        Assert.NotNull(fc.Arguments);
        Assert.Contains("city", fc.Arguments.Keys);
    }

    // ─── DurableSessionResponse round-trip ──────────────────────────────────

    [Fact]
    public void DurableSessionResponse_RoundTrip_PreservesCoreFieldsAndUsage()
    {
        var createdAt = new DateTimeOffset(2026, 4, 30, 12, 5, 0, TimeSpan.Zero);
        var original = new DurableSessionResponse
        {
            CorrelationId = "corr-resp-1",
            CreatedAt = createdAt,
            Messages = new List<ChatMessage>
            {
                new(ChatRole.Assistant, [new TextContent("It's 72°F in Seattle.")]),
            },
            Usage = new UsageDetails
            {
                InputTokenCount = 100,
                OutputTokenCount = 50,
                TotalTokenCount = 150,
            },
        };

        var roundTripped = RoundTrip(original);

        Assert.Equal("corr-resp-1", roundTripped.CorrelationId);
        Assert.Equal(createdAt, roundTripped.CreatedAt);
        Assert.Single(roundTripped.Messages);
        Assert.Equal(ChatRole.Assistant, roundTripped.Messages[0].Role);
        Assert.NotNull(roundTripped.Usage);
        Assert.Equal(100, roundTripped.Usage!.InputTokenCount);
        Assert.Equal(50, roundTripped.Usage.OutputTokenCount);
        Assert.Equal(150, roundTripped.Usage.TotalTokenCount);
    }

    // ─── DurableSessionResponse.Text accessor ───────────────────────────────

    [Fact]
    public void DurableSessionResponse_Text_ReturnsLastAssistantMessageText()
    {
        var response = new DurableSessionResponse
        {
            CorrelationId = "corr-1",
            CreatedAt = DateTimeOffset.UtcNow,
            Messages = new List<ChatMessage>
            {
                new(ChatRole.User, [new TextContent("hi")]),
                new(ChatRole.Assistant, [new TextContent("first reply")]),
                new(ChatRole.Tool, [new FunctionResultContent("call-1", "ok")]),
                new(ChatRole.Assistant, [new TextContent("final answer")]),
            },
        };

        Assert.Equal("final answer", response.Text);
    }

    [Fact]
    public void DurableSessionResponse_Text_ReturnsEmptyWhenNoAssistantMessage()
    {
        var response = new DurableSessionResponse
        {
            CorrelationId = "corr-1",
            CreatedAt = DateTimeOffset.UtcNow,
            Messages = new List<ChatMessage>
            {
                new(ChatRole.User, [new TextContent("hi")]),
                new(ChatRole.Tool, [new FunctionResultContent("call-1", "ok")]),
            },
        };

        Assert.Equal(string.Empty, response.Text);
    }

    [Fact]
    public void DurableSessionResponse_Text_ReturnsEmptyForEmptyMessages()
    {
        var response = new DurableSessionResponse
        {
            CorrelationId = "corr-1",
            CreatedAt = DateTimeOffset.UtcNow,
        };

        Assert.Equal(string.Empty, response.Text);
    }

    // ─── Mixed-type list round-trip preserves $type discriminators ──────────

    [Fact]
    public void DurableSessionEntryList_RoundTrip_PreservesPolymorphicDiscriminators()
    {
        var entries = new List<DurableSessionEntry>
        {
            new DurableSessionRequest
            {
                CorrelationId = "corr-1",
                CreatedAt = new DateTimeOffset(2026, 4, 30, 0, 0, 0, TimeSpan.Zero),
                Messages = new List<ChatMessage>
                {
                    new(ChatRole.User, [new TextContent("Q")]),
                },
            },
            new DurableSessionResponse
            {
                CorrelationId = "corr-1",
                CreatedAt = new DateTimeOffset(2026, 4, 30, 0, 0, 1, TimeSpan.Zero),
                Messages = new List<ChatMessage>
                {
                    new(ChatRole.Assistant, [new TextContent("A")]),
                },
                Usage = new UsageDetails { TotalTokenCount = 42 },
            },
            new DurableSessionRequest
            {
                CorrelationId = "corr-2",
                CreatedAt = new DateTimeOffset(2026, 4, 30, 0, 0, 2, TimeSpan.Zero),
                Messages = new List<ChatMessage>
                {
                    new(ChatRole.User, [new TextContent("Q2")]),
                },
            },
        };

        var payload = s_converter.ToPayload(entries);
        var roundTripped = (IReadOnlyList<DurableSessionEntry>)s_converter.ToValue(
            payload, typeof(IReadOnlyList<DurableSessionEntry>))!;

        Assert.NotNull(roundTripped);
        Assert.Equal(3, roundTripped.Count);

        var req1 = Assert.IsType<DurableSessionRequest>(roundTripped[0]);
        Assert.Equal("corr-1", req1.CorrelationId);

        var resp = Assert.IsType<DurableSessionResponse>(roundTripped[1]);
        Assert.Equal("corr-1", resp.CorrelationId);
        Assert.NotNull(resp.Usage);
        Assert.Equal(42, resp.Usage!.TotalTokenCount);

        var req2 = Assert.IsType<DurableSessionRequest>(roundTripped[2]);
        Assert.Equal("corr-2", req2.CorrelationId);
    }

    [Fact]
    public void DurableSessionEntry_SerializedJson_UsesAiRequestAndAiResponseDiscriminators()
    {
        // Lock in the wire-format constants — these are embedded in workflow event history
        // forever and must not drift.
        var request = new DurableSessionRequest
        {
            CorrelationId = "c",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        var response = new DurableSessionResponse
        {
            CorrelationId = "c",
            CreatedAt = DateTimeOffset.UtcNow,
        };

        var entries = new List<DurableSessionEntry> { request, response };

        var payload = s_converter.ToPayload(entries);
        // Payload data is the raw JSON bytes for JSON-encoded payloads.
        var json = System.Text.Encoding.UTF8.GetString(payload.Data.ToByteArray());

        // Tolerate either compact or pretty-printed JSON output.
        Assert.Matches("\"\\$type\"\\s*:\\s*\"ai_request\"", json);
        Assert.Matches("\"\\$type\"\\s*:\\s*\"ai_response\"", json);
    }

    // ─── AdditionalProperties round-trip ────────────────────────────────────

    [Fact]
    public void DurableSessionEntry_AdditionalProperties_RoundTrip()
    {
        // Construct a JSON document containing an unknown property and deserialize through
        // DurableSessionEntry's polymorphic resolver. The unknown property should land in
        // AdditionalProperties and survive a round-trip back to JSON.
        var json = """
        {
          "$type": "ai_request",
          "CorrelationId": "corr-extra",
          "CreatedAt": "2026-04-30T00:00:00+00:00",
          "Messages": [],
          "customKey": "customValue",
          "numericKey": 42
        }
        """;

        var entry = JsonSerializer.Deserialize<DurableSessionEntry>(
            json, DurableAIJsonUtilities.DefaultOptions);

        var req = Assert.IsType<DurableSessionRequest>(entry);
        Assert.Equal("corr-extra", req.CorrelationId);
        Assert.NotNull(req.AdditionalProperties);
        Assert.True(req.AdditionalProperties!.ContainsKey("customKey"));
        Assert.True(req.AdditionalProperties.ContainsKey("numericKey"));

        // Round-trip through the payload converter and verify AdditionalProperties survives.
        var roundTripped = RoundTrip(req);
        Assert.NotNull(roundTripped.AdditionalProperties);
        Assert.True(roundTripped.AdditionalProperties!.ContainsKey("customKey"));
        Assert.True(roundTripped.AdditionalProperties.ContainsKey("numericKey"));
    }

    // ─── Factory: DurableSessionRequest.FromMessages ────────────────────────

    [Fact]
    public void FromMessages_ProducesExpectedShape_WithMessageTimestamps()
    {
        var msgTime = new DateTimeOffset(2026, 4, 1, 0, 0, 0, TimeSpan.Zero);
        var laterMsgTime = msgTime.AddMinutes(5);
        var fallback = new DateTimeOffset(2026, 12, 31, 0, 0, 0, TimeSpan.Zero);

        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, [new TextContent("first")]) { CreatedAt = laterMsgTime },
            new(ChatRole.User, [new TextContent("second")]) { CreatedAt = msgTime },
        };

        var entry = DurableSessionRequest.FromMessages(messages, "corr-fm-1", fallback);

        Assert.Equal("corr-fm-1", entry.CorrelationId);
        // Should pick the minimum of the message timestamps, not the fallback.
        Assert.Equal(msgTime, entry.CreatedAt);
        Assert.Equal(2, entry.Messages.Count);
    }

    [Fact]
    public void FromMessages_FallsBackToTimestamp_WhenMessagesHaveNoCreatedAt()
    {
        var fallback = new DateTimeOffset(2026, 4, 30, 0, 0, 0, TimeSpan.Zero);

        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, [new TextContent("hi")]),
        };

        var entry = DurableSessionRequest.FromMessages(messages, "corr-fm-2", fallback);

        Assert.Equal("corr-fm-2", entry.CorrelationId);
        Assert.Equal(fallback, entry.CreatedAt);
        Assert.Single(entry.Messages);
    }

    [Fact]
    public void FromMessages_FallsBackToTimestamp_WhenMessageListEmpty()
    {
        var fallback = new DateTimeOffset(2026, 4, 30, 0, 0, 0, TimeSpan.Zero);

        var entry = DurableSessionRequest.FromMessages(
            messages: Array.Empty<ChatMessage>(),
            correlationId: "corr-fm-3",
            timestamp: fallback);

        Assert.Equal("corr-fm-3", entry.CorrelationId);
        Assert.Equal(fallback, entry.CreatedAt);
        Assert.Empty(entry.Messages);
    }

    [Fact]
    public void FromMessages_ThrowsWhenCorrelationIdNullOrEmpty()
    {
        var messages = new List<ChatMessage> { new(ChatRole.User, "hi") };

        Assert.Throws<ArgumentException>(() =>
            DurableSessionRequest.FromMessages(messages, "", DateTimeOffset.UtcNow));

        Assert.Throws<ArgumentException>(() =>
            DurableSessionRequest.FromMessages(messages, null!, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void FromMessages_ThrowsWhenMessagesNull()
    {
        Assert.Throws<ArgumentNullException>(() =>
            DurableSessionRequest.FromMessages(null!, "corr", DateTimeOffset.UtcNow));
    }

    // ─── Factory: DurableSessionResponse.FromChatResponse ───────────────────

    [Fact]
    public void FromChatResponse_ProducesExpectedShape_PreservingUsageAndCreatedAt()
    {
        var responseCreatedAt = new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero);
        var fallback = new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero);

        var chatResponse = new ChatResponse(
            new ChatMessage(ChatRole.Assistant, [new TextContent("an answer")]))
        {
            CreatedAt = responseCreatedAt,
            Usage = new UsageDetails
            {
                InputTokenCount = 10,
                OutputTokenCount = 20,
                TotalTokenCount = 30,
            },
        };

        var entry = DurableSessionResponse.FromChatResponse("corr-resp-1", chatResponse, fallback);

        Assert.Equal("corr-resp-1", entry.CorrelationId);
        Assert.Equal(responseCreatedAt, entry.CreatedAt);
        Assert.Single(entry.Messages);
        Assert.Equal(ChatRole.Assistant, entry.Messages[0].Role);
        Assert.NotNull(entry.Usage);
        Assert.Equal(30, entry.Usage!.TotalTokenCount);
        Assert.Equal("an answer", entry.Text);
    }

    [Fact]
    public void FromChatResponse_FallsBackToTimestamp_WhenNoCreatedAtAvailable()
    {
        var fallback = new DateTimeOffset(2026, 4, 30, 0, 0, 0, TimeSpan.Zero);

        var chatResponse = new ChatResponse(
            new ChatMessage(ChatRole.Assistant, [new TextContent("ans")]));

        var entry = DurableSessionResponse.FromChatResponse("corr-fb", chatResponse, fallback);

        Assert.Equal(fallback, entry.CreatedAt);
        Assert.Null(entry.Usage);
    }

    [Fact]
    public void FromChatResponse_ThrowsWhenResponseNull()
    {
        Assert.Throws<ArgumentNullException>(() =>
            DurableSessionResponse.FromChatResponse("c", null!, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void FromChatResponse_ThrowsWhenCorrelationIdNullOrEmpty()
    {
        var chatResponse = new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok"));

        Assert.Throws<ArgumentException>(() =>
            DurableSessionResponse.FromChatResponse("", chatResponse, DateTimeOffset.UtcNow));

        Assert.Throws<ArgumentException>(() =>
            DurableSessionResponse.FromChatResponse(null!, chatResponse, DateTimeOffset.UtcNow));
    }
}
