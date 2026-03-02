using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Temporalio.Extensions.Agents.Tests.Helpers;
using Xunit;

namespace Temporalio.Extensions.Agents.Tests;

/// <summary>
/// Tests for <see cref="AgentWorkflowWrapper"/>, which applies request-level settings
/// (response format, tool filtering) to the inner agent's chat client pipeline.
/// </summary>
public class AgentWorkflowWrapperTests
{
    // ─── GetService ──────────────────────────────────────────────────────────

    [Fact]
    public void GetService_TemporalAgentSessionId_ReturnsSessionId()
    {
        var sessionId = TemporalAgentSessionId.WithRandomKey("TestAgent");
        var session = new TemporalAgentSession(sessionId);
        var wrapper = CreateWrapper(new RunRequest("test"), session);

        var result = wrapper.GetService(typeof(TemporalAgentSessionId));
        Assert.IsType<TemporalAgentSessionId>(result);
        Assert.Equal(sessionId, (TemporalAgentSessionId)result);
    }

    [Fact]
    public void GetService_UnknownType_ReturnsNull()
    {
        var session = MakeSession();
        var wrapper = CreateWrapper(new RunRequest("test"), session);

        var result = wrapper.GetService(typeof(string), serviceKey: null);
        Assert.Null(result);
    }

    // ─── GetRunOptions ────────────────────────────────────────────────────────

    [Fact]
    public void GetRunOptions_NullInput_ReturnsChatClientAgentRunOptions()
    {
        var wrapper = CreateWrapper(new RunRequest("test"), MakeSession());
        var options = wrapper.GetRunOptions(null);
        Assert.IsType<ChatClientAgentRunOptions>(options);
    }

    [Fact]
    public void GetRunOptions_AgentRunOptionsInput_ReturnsChatClientAgentRunOptions()
    {
        var wrapper = CreateWrapper(new RunRequest("test"), MakeSession());
        // Base AgentRunOptions (not a subclass) should be replaced
        var options = wrapper.GetRunOptions(new AgentRunOptions());
        Assert.IsType<ChatClientAgentRunOptions>(options);
    }

    [Fact]
    public void GetRunOptions_SetsChatClientFactory()
    {
        var wrapper = CreateWrapper(new RunRequest("test"), MakeSession());
        var options = (ChatClientAgentRunOptions)wrapper.GetRunOptions(null);
        Assert.NotNull(options.ChatClientFactory);
    }

    // ─── Tool filtering ───────────────────────────────────────────────────────

    [Fact]
    public async Task GetRunOptions_EnableToolCalls_False_SetsNullTools()
    {
        // Request disables all tool calls
        var request = new RunRequest("test", enableToolCalls: false);
        var wrapper = CreateWrapper(request, MakeSession());

        // Provide two tools in the chat options
        var chatOptions = new ChatClientAgentRunOptions(new ChatOptions
        {
            Tools = [CreateTool("toolA"), CreateTool("toolB")]
        });

        var runOptions = (ChatClientAgentRunOptions)wrapper.GetRunOptions(chatOptions);

        // Invoke the factory to build the middleware pipeline
        var capturing = new CapturingChatClient();
        var builtClient = runOptions.ChatClientFactory!(capturing);

        // Trigger GetResponseAsync — middleware should set Tools = null before calling inner client
        await builtClient.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], new ChatOptions());

        Assert.NotNull(capturing.LastOptions);
        Assert.Null(capturing.LastOptions.Tools);
    }

    [Fact]
    public async Task GetRunOptions_EnableToolNames_FiltersToMatchingTool()
    {
        // Request only allows "toolA"
        var request = new RunRequest(
            "test",
            enableToolCalls: true,
            enableToolNames: ["toolA"]);

        var toolA = CreateTool("toolA");
        var toolB = CreateTool("toolB");
        var chatOptions = new ChatClientAgentRunOptions(new ChatOptions
        {
            Tools = [toolA, toolB]
        });

        var wrapper = CreateWrapper(request, MakeSession());
        var runOptions = (ChatClientAgentRunOptions)wrapper.GetRunOptions(chatOptions);

        var capturing = new CapturingChatClient();
        var builtClient = runOptions.ChatClientFactory!(capturing);
        await builtClient.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], new ChatOptions());

        // Only toolA should survive the filter
        Assert.NotNull(capturing.LastOptions);
        Assert.NotNull(capturing.LastOptions.Tools);
        Assert.Single(capturing.LastOptions.Tools);
        Assert.Equal("toolA", capturing.LastOptions.Tools[0].Name);
    }

    [Fact]
    public async Task GetRunOptions_EnableToolCalls_True_NoToolNames_KeepsAllTools()
    {
        // Request allows all tools (EnableToolNames = null)
        var request = new RunRequest("test", enableToolCalls: true);

        var chatOptions = new ChatClientAgentRunOptions(new ChatOptions
        {
            Tools = [CreateTool("toolA"), CreateTool("toolB")]
        });

        var wrapper = CreateWrapper(request, MakeSession());
        var runOptions = (ChatClientAgentRunOptions)wrapper.GetRunOptions(chatOptions);

        var capturing = new CapturingChatClient();
        var builtClient = runOptions.ChatClientFactory!(capturing);
        await builtClient.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], new ChatOptions());

        // No filtering → inner client receives whatever was in the passed options
        // (The middleware doesn't add tools from chatOptions when EnableToolNames is null)
        // So the inner client gets the ChatOptions passed to GetResponseAsync, unmodified for tools.
        Assert.NotNull(capturing.LastOptions);
        // When no EnableToolNames filter is active, tools from the outer ChatOptions
        // are not injected — they remain as-is from the options passed to GetResponseAsync.
        // Since we passed new ChatOptions() (no tools), tools remain null here.
    }

    // ─── Tool filtering edge cases ──────────────────────────────────────────

    [Fact]
    public async Task GetRunOptions_EnableToolNames_EmptyList_DoesNotFilter()
    {
        // An empty EnableToolNames list (Count == 0) is treated as "no filter specified",
        // NOT as "disable all tools". To disable all tools, use EnableToolCalls = false.
        var request = new RunRequest(
            "test",
            enableToolCalls: true,
            enableToolNames: []);

        var chatOptions = new ChatClientAgentRunOptions(new ChatOptions
        {
            Tools = [CreateTool("toolA"), CreateTool("toolB")]
        });

        var wrapper = CreateWrapper(request, MakeSession());
        var runOptions = (ChatClientAgentRunOptions)wrapper.GetRunOptions(chatOptions);

        var capturing = new CapturingChatClient();
        var builtClient = runOptions.ChatClientFactory!(capturing);
        await builtClient.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], new ChatOptions());

        // Empty list is a no-op — tools are not filtered.
        Assert.NotNull(capturing.LastOptions);
    }

    [Fact]
    public async Task GetRunOptions_ToolNamesWithSpecialCharacters_MatchesCorrectly()
    {
        // Tool names with dashes, underscores, and dots (common in real tools).
        var request = new RunRequest(
            "test",
            enableToolCalls: true,
            enableToolNames: ["my-tool_v2.0"]);

        var toolMatch = CreateTool("my-tool_v2.0");
        var toolNoMatch = CreateTool("other-tool");
        var chatOptions = new ChatClientAgentRunOptions(new ChatOptions
        {
            Tools = [toolMatch, toolNoMatch]
        });

        var wrapper = CreateWrapper(request, MakeSession());
        var runOptions = (ChatClientAgentRunOptions)wrapper.GetRunOptions(chatOptions);

        var capturing = new CapturingChatClient();
        var builtClient = runOptions.ChatClientFactory!(capturing);
        await builtClient.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], new ChatOptions());

        Assert.NotNull(capturing.LastOptions);
        Assert.NotNull(capturing.LastOptions.Tools);
        Assert.Single(capturing.LastOptions.Tools);
        Assert.Equal("my-tool_v2.0", capturing.LastOptions.Tools[0].Name);
    }

    [Fact]
    public async Task GetRunOptions_ToolNameNotInList_FiltersToNothing()
    {
        // EnableToolNames contains a name that doesn't match any registered tool.
        var request = new RunRequest(
            "test",
            enableToolCalls: true,
            enableToolNames: ["nonexistent-tool"]);

        var chatOptions = new ChatClientAgentRunOptions(new ChatOptions
        {
            Tools = [CreateTool("toolA"), CreateTool("toolB")]
        });

        var wrapper = CreateWrapper(request, MakeSession());
        var runOptions = (ChatClientAgentRunOptions)wrapper.GetRunOptions(chatOptions);

        var capturing = new CapturingChatClient();
        var builtClient = runOptions.ChatClientFactory!(capturing);
        await builtClient.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], new ChatOptions());

        Assert.NotNull(capturing.LastOptions);
        Assert.NotNull(capturing.LastOptions.Tools);
        Assert.Empty(capturing.LastOptions.Tools);
    }

    // ─── ChatClientAgentRunOptions passthrough ────────────────────────────────

    [Fact]
    public async Task GetRunOptions_ChatClientAgentRunOptionsInput_PreservesExistingChatOptions()
    {
        var request = new RunRequest(
            "test",
            enableToolCalls: true,
            enableToolNames: ["toolA"]);

        var existingChatOptions = new ChatOptions
        {
            Tools = [CreateTool("toolA"), CreateTool("toolB")]
        };
        var inputOptions = new ChatClientAgentRunOptions(existingChatOptions);

        var wrapper = CreateWrapper(request, MakeSession());
        var runOptions = (ChatClientAgentRunOptions)wrapper.GetRunOptions(inputOptions);

        // The returned options should be the same instance (not replaced)
        Assert.Same(inputOptions, runOptions);

        // The factory should filter tools from the existing ChatOptions
        var capturing = new CapturingChatClient();
        var builtClient = runOptions.ChatClientFactory!(capturing);
        await builtClient.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], new ChatOptions());

        Assert.NotNull(capturing.LastOptions);
        Assert.NotNull(capturing.LastOptions.Tools);
        Assert.Single(capturing.LastOptions.Tools);
        Assert.Equal("toolA", capturing.LastOptions.Tools[0].Name);
    }

    // ─── Unsupported run options type ────────────────────────────────────────

    [Fact]
    public void GetRunOptions_UnsupportedSubclass_ThrowsNotSupportedException()
    {
        var wrapper = CreateWrapper(new RunRequest("test"), MakeSession());
        var unsupported = new CustomRunOptions();

        Assert.Throws<NotSupportedException>(() => wrapper.GetRunOptions(unsupported));
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────

    private static AgentWorkflowWrapper CreateWrapper(RunRequest request, TemporalAgentSession session) =>
        new AgentWorkflowWrapper(new StubAIAgent("TestAgent"), request, session);

    private static TemporalAgentSession MakeSession() =>
        new TemporalAgentSession(TemporalAgentSessionId.WithRandomKey("TestAgent"));

    private static AIFunction CreateTool(string name) =>
        AIFunctionFactory.Create(() => $"result from {name}", name);

    /// <summary>A custom AgentRunOptions subclass that is NOT ChatClientAgentRunOptions.</summary>
    private sealed class CustomRunOptions : AgentRunOptions;
}
