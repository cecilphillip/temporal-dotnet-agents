using Microsoft.Extensions.AI;
using Temporalio.Converters;
using Temporalio.Extensions.AI;
using Xunit;

namespace Temporalio.Extensions.Agents.Tests;

/// <summary>
/// Target-behavior safety net for the Layer 1 refactor: verifies that
/// <see cref="DurableAIDataConverter.Instance"/> round-trips MEAI <see cref="ChatMessage"/>
/// payloads with full <see cref="AIContent"/> polymorphism preserved.
///
/// These tests document the post-refactor target surface — they pass on the current
/// codebase because <c>DurableAIDataConverter</c> already handles
/// <c>ChatMessage</c>/<c>AIContent</c> polymorphism via <see cref="AIJsonUtilities.DefaultOptions"/>,
/// and they survive Phases 2 and 3 unchanged because they exercise MEAI types directly,
/// never the legacy TA-custom content hierarchy that was deleted in Phase 2.
/// </summary>
public class ChatMessagePolymorphismTests
{
    private static readonly IPayloadConverter s_converter = DurableAIDataConverter.Instance.PayloadConverter;

    private static ChatMessage RoundTrip(ChatMessage original)
    {
        var payload = s_converter.ToPayload(original);
        return (ChatMessage)s_converter.ToValue(payload, typeof(ChatMessage))!;
    }

    private static T SingleContent<T>(ChatMessage msg) where T : AIContent
    {
        Assert.Single(msg.Contents);
        return Assert.IsType<T>(msg.Contents[0]);
    }

    // ─── Single-content round-trips (one per AIContent subtype) ─────────────

    [Fact]
    public void RoundTrip_TextContent_PreservesText()
    {
        var original = new ChatMessage(ChatRole.User, [new TextContent("hello world")]);
        var roundTripped = RoundTrip(original);

        Assert.Equal(ChatRole.User, roundTripped.Role);
        var text = SingleContent<TextContent>(roundTripped);
        Assert.Equal("hello world", text.Text);
    }

    [Fact]
    public void RoundTrip_FunctionCallContent_PreservesCallIdNameAndArguments()
    {
        var original = new ChatMessage(ChatRole.Assistant, [
            new FunctionCallContent(
                callId: "call-123",
                name: "get_weather",
                arguments: new Dictionary<string, object?>
                {
                    ["city"] = "Seattle",
                    ["unit"] = "celsius"
                })
        ]);

        var roundTripped = RoundTrip(original);

        Assert.Equal(ChatRole.Assistant, roundTripped.Role);
        var fc = SingleContent<FunctionCallContent>(roundTripped);
        Assert.Equal("call-123", fc.CallId);
        Assert.Equal("get_weather", fc.Name);
        Assert.NotNull(fc.Arguments);
        Assert.Equal(2, fc.Arguments.Count);
        Assert.Contains("city", fc.Arguments.Keys);
        Assert.Contains("unit", fc.Arguments.Keys);
    }

    [Fact]
    public void RoundTrip_FunctionResultContent_PreservesCallIdAndResult()
    {
        var original = new ChatMessage(ChatRole.Tool, [
            new FunctionResultContent(callId: "call-123", result: "72°F and sunny")
        ]);

        var roundTripped = RoundTrip(original);

        Assert.Equal(ChatRole.Tool, roundTripped.Role);
        var fr = SingleContent<FunctionResultContent>(roundTripped);
        Assert.Equal("call-123", fr.CallId);
        Assert.NotNull(fr.Result);
        Assert.Contains("72°F", fr.Result.ToString());
    }

    [Fact]
    public void RoundTrip_ErrorContent_PreservesMessage()
    {
        var original = new ChatMessage(ChatRole.Assistant, [
            new ErrorContent("something went wrong") { ErrorCode = "E_BAD" }
        ]);

        var roundTripped = RoundTrip(original);

        var err = SingleContent<ErrorContent>(roundTripped);
        Assert.Equal("something went wrong", err.Message);
        Assert.Equal("E_BAD", err.ErrorCode);
    }

    [Fact]
    public void RoundTrip_UsageContent_PreservesTokenCounts()
    {
        var original = new ChatMessage(ChatRole.Assistant, [
            new UsageContent(new UsageDetails
            {
                InputTokenCount = 100,
                OutputTokenCount = 50,
                TotalTokenCount = 150
            })
        ]);

        var roundTripped = RoundTrip(original);

        var usage = SingleContent<UsageContent>(roundTripped);
        Assert.Equal(100, usage.Details.InputTokenCount);
        Assert.Equal(50, usage.Details.OutputTokenCount);
        Assert.Equal(150, usage.Details.TotalTokenCount);
    }

    [Fact]
    public void RoundTrip_DataContent_PreservesBytesAndMediaType()
    {
        // Small synthetic byte payload — large enough to assert non-trivially.
        var bytes = new byte[] { 0x01, 0x02, 0x03, 0x04, 0xFF };
        var original = new ChatMessage(ChatRole.User, [
            new DataContent(bytes, mediaType: "application/octet-stream")
        ]);

        var roundTripped = RoundTrip(original);

        var data = SingleContent<DataContent>(roundTripped);
        Assert.Equal("application/octet-stream", data.MediaType);
        Assert.Equal(bytes, data.Data.ToArray());
    }

    [Fact]
    public void RoundTrip_HostedFileContent_PreservesFileId()
    {
        var original = new ChatMessage(ChatRole.User, [
            new HostedFileContent("file-abc-123")
        ]);

        var roundTripped = RoundTrip(original);

        var file = SingleContent<HostedFileContent>(roundTripped);
        Assert.Equal("file-abc-123", file.FileId);
    }

    [Fact]
    public void RoundTrip_HostedVectorStoreContent_PreservesVectorStoreId()
    {
        var original = new ChatMessage(ChatRole.User, [
            new HostedVectorStoreContent("vs-xyz-789")
        ]);

        var roundTripped = RoundTrip(original);

        var vs = SingleContent<HostedVectorStoreContent>(roundTripped);
        Assert.Equal("vs-xyz-789", vs.VectorStoreId);
    }

    [Fact]
    public void RoundTrip_TextReasoningContent_PreservesText()
    {
        var original = new ChatMessage(ChatRole.Assistant, [
            new TextReasoningContent("step 1: consider the inputs; step 2: pick the answer")
        ]);

        var roundTripped = RoundTrip(original);

        var reasoning = SingleContent<TextReasoningContent>(roundTripped);
        Assert.Equal("step 1: consider the inputs; step 2: pick the answer", reasoning.Text);
    }

    [Fact]
    public void RoundTrip_UriContent_PreservesUriAndMediaType()
    {
        var original = new ChatMessage(ChatRole.User, [
            new UriContent("https://example.com/picture.png", mediaType: "image/png")
        ]);

        var roundTripped = RoundTrip(original);

        var uri = SingleContent<UriContent>(roundTripped);
        Assert.Equal(new Uri("https://example.com/picture.png"), uri.Uri);
        Assert.Equal("image/png", uri.MediaType);
    }

    // ─── Bug-fix lock-in: AdditionalProperties round-trips ──────────────────

    [Fact]
    public void RoundTrip_ChatMessage_PreservesAdditionalProperties()
    {
        // Pre-Layer 1, the deleted TA-custom message wrapper silently dropped
        // AdditionalProperties. With direct ChatMessage storage they are now
        // preserved end-to-end through DurableAIDataConverter.
        var original = new ChatMessage(ChatRole.User, [new TextContent("hi")])
        {
            AuthorName = "alice",
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                ["customKey"] = "customValue",
                ["numericKey"] = 42L,
            },
        };

        var roundTripped = RoundTrip(original);

        Assert.Equal("alice", roundTripped.AuthorName);
        Assert.NotNull(roundTripped.AdditionalProperties);
        Assert.True(roundTripped.AdditionalProperties.ContainsKey("customKey"));
        Assert.True(roundTripped.AdditionalProperties.ContainsKey("numericKey"));
    }

    // ─── Mixed-content list round-trip ──────────────────────────────────────

    [Fact]
    public void RoundTrip_ChatMessageList_PreservesPolymorphismAcrossAllContentTypes()
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, [new TextContent("you are a helpful assistant")]),
            new(ChatRole.User, [
                new TextContent("what is the weather?"),
                new UriContent("https://example.com/map.png", mediaType: "image/png"),
            ]),
            new(ChatRole.Assistant, [
                new TextReasoningContent("the user wants weather info"),
                new FunctionCallContent("call-1", "get_weather",
                    new Dictionary<string, object?> { ["city"] = "Seattle" }),
            ]),
            new(ChatRole.Tool, [
                new FunctionResultContent("call-1", "72°F"),
            ]),
            new(ChatRole.Assistant, [
                new TextContent("It is 72°F in Seattle."),
                new UsageContent(new UsageDetails { TotalTokenCount = 200 }),
            ]),
        };

        var payload = s_converter.ToPayload(messages);
        var roundTripped = (IReadOnlyList<ChatMessage>)s_converter.ToValue(
            payload, typeof(IReadOnlyList<ChatMessage>))!;

        Assert.NotNull(roundTripped);
        Assert.Equal(5, roundTripped.Count);

        // Message 0: system + text
        Assert.Equal(ChatRole.System, roundTripped[0].Role);
        Assert.IsType<TextContent>(roundTripped[0].Contents[0]);

        // Message 1: user + text + uri
        Assert.Equal(ChatRole.User, roundTripped[1].Role);
        Assert.Equal(2, roundTripped[1].Contents.Count);
        Assert.IsType<TextContent>(roundTripped[1].Contents[0]);
        var uri = Assert.IsType<UriContent>(roundTripped[1].Contents[1]);
        Assert.Equal(new Uri("https://example.com/map.png"), uri.Uri);

        // Message 2: assistant + reasoning + function call
        Assert.Equal(ChatRole.Assistant, roundTripped[2].Role);
        Assert.Equal(2, roundTripped[2].Contents.Count);
        Assert.IsType<TextReasoningContent>(roundTripped[2].Contents[0]);
        var fc = Assert.IsType<FunctionCallContent>(roundTripped[2].Contents[1]);
        Assert.Equal("call-1", fc.CallId);
        Assert.Equal("get_weather", fc.Name);

        // Message 3: tool + function result
        Assert.Equal(ChatRole.Tool, roundTripped[3].Role);
        var fr = Assert.IsType<FunctionResultContent>(roundTripped[3].Contents[0]);
        Assert.Equal("call-1", fr.CallId);

        // Message 4: assistant + text + usage
        Assert.Equal(ChatRole.Assistant, roundTripped[4].Role);
        Assert.Equal(2, roundTripped[4].Contents.Count);
        Assert.IsType<TextContent>(roundTripped[4].Contents[0]);
        var usage = Assert.IsType<UsageContent>(roundTripped[4].Contents[1]);
        Assert.Equal(200, usage.Details.TotalTokenCount);
    }
}
