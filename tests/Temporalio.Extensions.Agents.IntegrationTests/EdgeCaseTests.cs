using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Temporalio.Client;
using Temporalio.Extensions.Agents.IntegrationTests.Helpers;
using Temporalio.Extensions.Agents.State;
using Xunit;
using Xunit.Abstractions;

namespace Temporalio.Extensions.Agents.IntegrationTests;

/// <summary>
/// Tests for message and session edge cases: large payloads, unicode/emoji content,
/// special characters in agent names, and response format per-request scoping.
/// </summary>
[Trait("Category", "Integration")]
public class EdgeCaseTests : IClassFixture<IntegrationTestFixture>
{
    private readonly IntegrationTestFixture _fixture;
    private readonly ITestOutputHelper _output;

    public EdgeCaseTests(IntegrationTestFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    // ── Large message payloads ───────────────────────────────────────────────

    [Fact]
    public async Task LargeMessagePayload_RoundTripsCorrectly()
    {
        // Generate a ~100 KB message to test serialization through Temporal's data converter.
        var largeText = new string('A', 100_000);

        var session = await _fixture.AgentProxy.CreateSessionAsync();
        var response = await _fixture.AgentProxy.RunAsync(largeText, session);

        Assert.NotNull(response);
        Assert.NotEmpty(response.Messages);

        // The echo agent includes the message text in its response.
        // Verify the full payload made it through.
        Assert.Contains("Echo [1]:", response.Messages[0].Text);
        Assert.Contains(largeText, response.Messages[0].Text);

        _output.WriteLine(
            $"100 KB message round-tripped successfully. " +
            $"Response length: {response.Messages[0].Text.Length:N0} chars.");
    }

    [Fact]
    public async Task LargeMessagePayload_HistoryPreservedAcrossTurns()
    {
        // Send a 50 KB message, then a normal follow-up.
        // The history grows substantially — verify the second turn still works.
        var largeText = new string('B', 50_000);

        var session = await _fixture.AgentProxy.CreateSessionAsync();
        await _fixture.AgentProxy.RunAsync(largeText, session);
        var r2 = await _fixture.AgentProxy.RunAsync("Follow-up", session);

        Assert.Contains("Echo [2]: Follow-up", r2.Messages[0].Text);
        _output.WriteLine("50 KB message + follow-up succeeded — history handles large payloads.");
    }

    // ── Unicode and emoji support ────────────────────────────────────────────

    [Fact]
    public async Task UnicodeMessages_ChineseCharacters_Preserved()
    {
        var chineseText = "\u4f60\u597d\u4e16\u754c"; // 你好世界

        var session = await _fixture.AgentProxy.CreateSessionAsync();
        var response = await _fixture.AgentProxy.RunAsync(chineseText, session);

        Assert.Contains(chineseText, response.Messages[0].Text);
        _output.WriteLine($"Chinese text preserved: {response.Messages[0].Text}");
    }

    [Fact]
    public async Task UnicodeMessages_ArabicCharacters_Preserved()
    {
        var arabicText = "\u0645\u0631\u062d\u0628\u0627 \u0628\u0627\u0644\u0639\u0627\u0644\u0645"; // مرحبا بالعالم

        var session = await _fixture.AgentProxy.CreateSessionAsync();
        var response = await _fixture.AgentProxy.RunAsync(arabicText, session);

        Assert.Contains(arabicText, response.Messages[0].Text);
        _output.WriteLine($"Arabic text preserved: {response.Messages[0].Text}");
    }

    [Fact]
    public async Task EmojiMessages_PreservedThroughSerialization()
    {
        var emojiText = "Hello \ud83d\ude80\ud83c\udf1f\ud83e\udd16 World"; // Hello 🚀🌟🤖 World

        var session = await _fixture.AgentProxy.CreateSessionAsync();
        var response = await _fixture.AgentProxy.RunAsync(emojiText, session);

        Assert.Contains(emojiText, response.Messages[0].Text);
        _output.WriteLine($"Emoji text preserved: {response.Messages[0].Text}");
    }

    [Fact]
    public async Task MixedScriptMessages_PreservedThroughSerialization()
    {
        // Mix of Latin, CJK, Cyrillic, and emoji in a single message.
        var mixedText = "Hello \u4f60\u597d \u041f\u0440\u0438\u0432\u0435\u0442 \ud83d\udc4b"; // Hello 你好 Привет 👋

        var session = await _fixture.AgentProxy.CreateSessionAsync();
        var response = await _fixture.AgentProxy.RunAsync(mixedText, session);

        Assert.Contains(mixedText, response.Messages[0].Text);
        _output.WriteLine($"Mixed script preserved: {response.Messages[0].Text}");
    }

    [Fact]
    public async Task UnicodeMessages_HistoryQueryPreservesEncoding()
    {
        // Verify that querying the workflow history preserves unicode content.
        var unicodeText = "\u00c9l\u00e8ve \u00fc\u00f1\u00efc\u00f8d\u00e9"; // Élève üñïcødé

        var session = (TemporalAgentSession)await _fixture.AgentProxy.CreateSessionAsync();
        await _fixture.AgentProxy.RunAsync(unicodeText, session);

        var handle = _fixture.Client.GetWorkflowHandle<AgentWorkflow>(
            session.SessionId.WorkflowId);
        var history = await handle.QueryAsync(wf => wf.GetHistory());

        var request = Assert.IsType<TemporalAgentStateRequest>(history[0]);
        var textContent = request.Messages[0].Contents
            .OfType<TemporalAgentStateTextContent>()
            .First();
        Assert.Equal(unicodeText, textContent.Text);

        _output.WriteLine($"Unicode in queried history: \"{textContent.Text}\"");
    }

    // ── Agent name with special characters ───────────────────────────────────

    [Fact]
    public async Task AgentNameWithDashes_WorksEndToEnd()
    {
        var taskQueue = $"dashed-name-{Guid.NewGuid():N}";
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<ITemporalClient>(_fixture.Client);
        builder.Services.ConfigureTemporalAgents(
            configure: options => options.AddAIAgent(
                new EchoAIAgent("my-custom-agent")),
            taskQueue: taskQueue);

        using var host = builder.Build();
        await host.StartAsync();

        try
        {
            var proxy = host.Services.GetTemporalAgentProxy("my-custom-agent");
            var session = (TemporalAgentSession)await proxy.CreateSessionAsync();

            // Verify the workflow ID contains the dashed agent name.
            Assert.StartsWith("ta-my-custom-agent-", session.SessionId.WorkflowId);

            var response = await proxy.RunAsync("Hello dashed agent", session);
            Assert.Contains("Echo [1]: Hello dashed agent", response.Messages[0].Text);

            _output.WriteLine(
                $"Agent 'my-custom-agent' worked. WorkflowId: {session.SessionId.WorkflowId}");
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task AgentNameWithUnderscores_WorksEndToEnd()
    {
        var taskQueue = $"underscore-name-{Guid.NewGuid():N}";
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<ITemporalClient>(_fixture.Client);
        builder.Services.ConfigureTemporalAgents(
            configure: options => options.AddAIAgent(
                new EchoAIAgent("agent_v2_beta")),
            taskQueue: taskQueue);

        using var host = builder.Build();
        await host.StartAsync();

        try
        {
            var proxy = host.Services.GetTemporalAgentProxy("agent_v2_beta");
            var session = await proxy.CreateSessionAsync();

            var response = await proxy.RunAsync("Hello underscore agent", session);
            Assert.Contains("Echo [1]:", response.Messages[0].Text);

            _output.WriteLine("Agent 'agent_v2_beta' worked end-to-end.");
        }
        finally
        {
            await host.StopAsync();
        }
    }

    // ── Response format per-request scope ────────────────────────────────────

    [Fact]
    public async Task ResponseFormat_IsPerRequest_NotPerSession()
    {
        var session = (TemporalAgentSession)await _fixture.AgentProxy.CreateSessionAsync();

        // Turn 1: with JSON response format.
        var jsonFormatOptions = new AgentRunOptions
        {
            ResponseFormat = ChatResponseFormat.ForJsonSchema(
                System.Text.Json.JsonSerializer.SerializeToElement(
                    new { type = "object", properties = new { answer = new { type = "string" } } }))
        };
        await _fixture.AgentProxy.RunAsync(
            [new ChatMessage(ChatRole.User, "With JSON format")],
            session,
            jsonFormatOptions);

        // Turn 2: without any response format.
        await _fixture.AgentProxy.RunAsync("Without format", session);

        // Query the workflow history to verify format scoping.
        var handle = _fixture.Client.GetWorkflowHandle<AgentWorkflow>(
            session.SessionId.WorkflowId);
        var history = await handle.QueryAsync(wf => wf.GetHistory());

        Assert.Equal(4, history.Count); // 2 turns × (request + response)

        var request1 = Assert.IsType<TemporalAgentStateRequest>(history[0]);
        var request2 = Assert.IsType<TemporalAgentStateRequest>(history[2]);

        // Turn 1 should have "json" response type.
        Assert.Equal("json", request1.ResponseType);

        // Turn 2 should have "text" (default) — NOT inherited from turn 1.
        Assert.Equal("text", request2.ResponseType);

        _output.WriteLine(
            $"Turn 1 ResponseType: {request1.ResponseType}, " +
            $"Turn 2 ResponseType: {request2.ResponseType} — per-request scope confirmed.");
    }

    // ── Complex JSON schema response format ──────────────────────────────────

    [Fact]
    public async Task ComplexJsonSchema_PreservedInWorkflowState()
    {
        // Create a complex JSON schema with nested objects, arrays, and constraints.
        var complexSchema = System.Text.Json.JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                title = new { type = "string", maxLength = 100 },
                tags = new
                {
                    type = "array",
                    items = new { type = "string" },
                    minItems = 1,
                    maxItems = 5
                },
                metadata = new
                {
                    type = "object",
                    properties = new
                    {
                        author = new { type = "string" },
                        version = new { type = "integer", minimum = 1 },
                        nested = new
                        {
                            type = "object",
                            properties = new
                            {
                                deep = new { type = "boolean" }
                            }
                        }
                    },
                    required = new[] { "author" }
                }
            },
            required = new[] { "title", "tags" }
        });

        var session = (TemporalAgentSession)await _fixture.AgentProxy.CreateSessionAsync();

        var schemaOptions = new AgentRunOptions
        {
            ResponseFormat = ChatResponseFormat.ForJsonSchema(complexSchema)
        };
        await _fixture.AgentProxy.RunAsync(
            [new ChatMessage(ChatRole.User, "Structured output")],
            session,
            schemaOptions);

        // Query the workflow history and verify the schema was stored.
        var handle = _fixture.Client.GetWorkflowHandle<AgentWorkflow>(
            session.SessionId.WorkflowId);
        var history = await handle.QueryAsync(wf => wf.GetHistory());

        var request = Assert.IsType<TemporalAgentStateRequest>(history[0]);
        Assert.Equal("json", request.ResponseType);
        Assert.NotNull(request.ResponseSchema);

        // Verify the schema's structure survived serialization.
        var schema = request.ResponseSchema!.Value;
        Assert.Equal("object", schema.GetProperty("type").GetString());

        // Verify nested properties exist.
        var props = schema.GetProperty("properties");
        Assert.Equal("string", props.GetProperty("title").GetProperty("type").GetString());
        Assert.Equal("array", props.GetProperty("tags").GetProperty("type").GetString());
        Assert.Equal("object", props.GetProperty("metadata").GetProperty("type").GetString());

        // Verify deeply nested structure.
        var nestedProp = props.GetProperty("metadata")
            .GetProperty("properties")
            .GetProperty("nested")
            .GetProperty("properties")
            .GetProperty("deep");
        Assert.Equal("boolean", nestedProp.GetProperty("type").GetString());

        // Verify required array.
        var required = schema.GetProperty("required");
        Assert.Equal(2, required.GetArrayLength());

        _output.WriteLine(
            $"Complex JSON schema with nested objects, arrays, and constraints " +
            $"preserved in workflow state. Schema size: {schema.ToString().Length} chars.");
    }

    // ── Empty message edge case ──────────────────────────────────────────────

    [Fact]
    public async Task EmptyMessageText_ThrowsArgumentException()
    {
        var session = await _fixture.AgentProxy.CreateSessionAsync();

        // The base AIAgent.RunAsync(string) validates the message parameter.
        // Empty/whitespace strings are rejected by the framework, not our library.
        await Assert.ThrowsAsync<ArgumentException>(
            () => _fixture.AgentProxy.RunAsync("", session));
    }

    // ── Unusual role sequences ────────────────────────────────────────────────

    [Fact]
    public async Task MultipleUserMessages_SingleTurn_AllPreservedInHistory()
    {
        // Send multiple user messages in a single RunAsync call.
        // The EchoAIAgent counts user messages and echoes the last one.
        var session = (TemporalAgentSession)await _fixture.AgentProxy.CreateSessionAsync();

        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "First user message"),
            new(ChatRole.User, "Second user message"),
        };

        var response = await _fixture.AgentProxy.RunAsync(messages, session);

        // EchoAIAgent counts ALL user messages in the full conversation.
        // Two user messages → turn count 2.
        Assert.Contains("Echo [2]: Second user message", response.Messages[0].Text);

        // Query history to verify both messages are stored.
        var handle = _fixture.Client.GetWorkflowHandle<AgentWorkflow>(
            session.SessionId.WorkflowId);
        var history = await handle.QueryAsync(wf => wf.GetHistory());

        // History should have 2 entries: 1 request (with 2 user messages) + 1 response.
        Assert.Equal(2, history.Count);

        var request = Assert.IsType<TemporalAgentStateRequest>(history[0]);
        Assert.Equal(2, request.Messages.Count);
        Assert.All(request.Messages, m => Assert.Equal("user", m.Role));

        _output.WriteLine("Multiple user messages in single turn preserved correctly.");
    }

    [Fact]
    public async Task UserAssistantUser_RoleSequence_PreservedAcrossTurns()
    {
        // Turn 1: User → Assistant response
        // Turn 2: User → Assistant response
        // This creates a [User, Assistant, User, Assistant] sequence
        // and verifies that the roles interleave correctly in history.
        var session = (TemporalAgentSession)await _fixture.AgentProxy.CreateSessionAsync();

        await _fixture.AgentProxy.RunAsync("First question", session);
        var r2 = await _fixture.AgentProxy.RunAsync("Follow-up question", session);

        Assert.Contains("Echo [2]: Follow-up question", r2.Messages[0].Text);

        // Verify the full history has correct role alternation.
        var handle = _fixture.Client.GetWorkflowHandle<AgentWorkflow>(
            session.SessionId.WorkflowId);
        var history = await handle.QueryAsync(wf => wf.GetHistory());

        // 2 turns × (request + response) = 4 entries.
        Assert.Equal(4, history.Count);

        // Entries alternate: request, response, request, response.
        Assert.IsType<TemporalAgentStateRequest>(history[0]);
        Assert.IsType<TemporalAgentStateResponse>(history[1]);
        Assert.IsType<TemporalAgentStateRequest>(history[2]);
        Assert.IsType<TemporalAgentStateResponse>(history[3]);

        // Each request has a user message, each response has an assistant message.
        var req1 = (TemporalAgentStateRequest)history[0];
        var resp1 = (TemporalAgentStateResponse)history[1];
        Assert.Equal("user", req1.Messages[0].Role);
        Assert.Equal("assistant", resp1.Messages[0].Role);

        _output.WriteLine("User-Assistant role alternation verified across 2 turns.");
    }

    // ── Empty response edge case ──────────────────────────────────────────────

    [Fact]
    public async Task EmptyAgentResponse_RoundTripsWithoutCrash()
    {
        // Register a custom agent that returns an empty AgentResponse.
        var taskQueue = $"empty-response-{Guid.NewGuid():N}";
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<ITemporalClient>(_fixture.Client);
        builder.Services.ConfigureTemporalAgents(
            configure: options => options.AddAIAgent(
                new EmptyResponseAIAgent("EmptyAgent")),
            taskQueue: taskQueue);

        using var host = builder.Build();
        await host.StartAsync();

        try
        {
            var proxy = host.Services.GetTemporalAgentProxy("EmptyAgent");
            var session = (TemporalAgentSession)await proxy.CreateSessionAsync();

            var response = await proxy.RunAsync("Send me nothing back", session);

            // The response should be valid but contain no messages.
            Assert.NotNull(response);
            Assert.Empty(response.Messages);

            // Verify the history recorded both the request and the empty response.
            var handle = _fixture.Client.GetWorkflowHandle<AgentWorkflow>(
                session.SessionId.WorkflowId);
            var history = await handle.QueryAsync(wf => wf.GetHistory());

            Assert.Equal(2, history.Count); // request + response
            var storedResponse = Assert.IsType<TemporalAgentStateResponse>(history[1]);
            Assert.Empty(storedResponse.Messages);

            _output.WriteLine("Empty agent response round-tripped without crash.");
        }
        finally
        {
            await host.StopAsync();
        }
    }
}
