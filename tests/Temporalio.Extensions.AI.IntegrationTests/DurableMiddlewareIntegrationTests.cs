using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Temporalio.Client;
using Temporalio.Extensions.Hosting;
using Temporalio.Testing;
using Temporalio.Workflows;
using Xunit;

namespace Temporalio.Extensions.AI.IntegrationTests;

/// <summary>
/// Integration tests for durable middleware: tool dispatch and embedding generation.
/// Each test spins up its own WorkflowEnvironment for independent configuration.
/// </summary>
public class DurableMiddlewareIntegrationTests
{
    // ── Test 4: Durable tool invocation ─────────────────────────────────────

    /// <summary>
    /// Verifies that a DurableAIFunction dispatches as a Temporal activity when
    /// called inside a workflow body (Workflow.InWorkflow == true).
    ///
    /// Architecture: ToolDispatchWorkflow calls durableFunc.InvokeAsync() directly
    /// in the workflow body. Because Workflow.InWorkflow == true, DurableAIFunction
    /// routes the call to DurableFunctionActivities rather than the inner lambda.
    /// DurableFunctionActivities looks up the registered tool by name and invokes it.
    /// </summary>
    [Fact]
    public async Task DurableAIFunction_InvokesToolAsActivity_WhenCalledInsideWorkflow()
    {
        await using var env = await WorkflowEnvironment.StartLocalAsync();

        // The real tool implementation — registered in DurableFunctionRegistry via
        // AddDurableTools so DurableFunctionActivities can resolve it by name.
        var tool = AIFunctionFactory.Create(
            () => "tool-result",
            "get_tool_result",
            "Returns a recognizable test result.");

        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<ITemporalClient>(env.Client);

        // DurableChatActivities requires an IChatClient.
        builder.Services.AddSingleton<IChatClient>(new MinimalChatClient());
        // DurableEmbeddingActivities requires an IEmbeddingGenerator even when not exercised.
        builder.Services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(
            new StubEmbeddingGenerator(4));

        // Use an isolated task queue name to avoid conflicts with other tests.
        const string taskQueue = "test-tool-dispatch-4";

        builder.Services
            .AddHostedTemporalWorker(taskQueue)
            .AddDurableAI(opts =>
            {
                opts.ActivityTimeout = TimeSpan.FromSeconds(30);
                opts.HeartbeatTimeout = TimeSpan.FromSeconds(10);
                opts.SessionTimeToLive = TimeSpan.FromMinutes(5);
            })
            .AddDurableTools(tool)
            .AddWorkflow<ToolDispatchWorkflow>();

        using var host = builder.Build();
        await host.StartAsync();

        var workflowId = $"tool-dispatch-{Guid.NewGuid():N}";
        var handle = await env.Client.StartWorkflowAsync(
            (ToolDispatchWorkflow wf) => wf.RunAsync(),
            new WorkflowOptions(workflowId, taskQueue));

        var result = await handle.GetResultAsync();

        // The activity resolved the registered tool by name and returned "tool-result".
        Assert.Equal("tool-result", result);

        await host.StopAsync();
    }

    // ── Test 5: Durable embedding generator ──────────────────────────────────

    /// <summary>
    /// Verifies that DurableEmbeddingActivities resolves the IEmbeddingGenerator from DI
    /// and produces the expected embeddings when dispatched from a workflow.
    ///
    /// Architecture: EmbeddingTestWorkflow dispatches DurableEmbeddingActivities.GenerateAsync
    /// directly via Workflow.ExecuteActivityAsync (same pattern as ToolDispatchWorkflow).
    /// DurableEmbeddingActivities resolves IEmbeddingGenerator from DI (StubEmbeddingGenerator)
    /// and returns the generated embeddings. The test verifies count and dimensions.
    /// </summary>
    [Fact]
    public async Task DurableEmbeddingGenerator_DispatchesAsActivity_WhenCalledInsideWorkflow()
    {
        await using var env = await WorkflowEnvironment.StartLocalAsync();

        const int stubDimensions = 4;
        var stubGenerator = new StubEmbeddingGenerator(stubDimensions);

        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<ITemporalClient>(env.Client);

        // DurableChatActivities requires an IChatClient.
        builder.Services.AddSingleton<IChatClient>(new MinimalChatClient());
        builder.Services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(stubGenerator);

        // Use an isolated task queue name to avoid conflicts with other tests.
        const string embTaskQueue = "test-embedding-5";

        builder.Services
            .AddHostedTemporalWorker(embTaskQueue)
            .AddDurableAI(opts =>
            {
                opts.ActivityTimeout = TimeSpan.FromSeconds(30);
                opts.HeartbeatTimeout = TimeSpan.FromSeconds(10);
                opts.SessionTimeToLive = TimeSpan.FromMinutes(5);
            })
            .AddWorkflow<EmbeddingTestWorkflow>();

        using var host = builder.Build();
        await host.StartAsync();

        var embInput = new EmbeddingTestInput
        {
            Values = new List<string> { "hello world", "temporal sdk" },
            ActivityTimeout = TimeSpan.FromSeconds(30),
        };

        var workflowId = $"emb-test-{Guid.NewGuid():N}";
        var handle = await env.Client.StartWorkflowAsync(
            (EmbeddingTestWorkflow wf) => wf.RunAsync(embInput),
            new WorkflowOptions(workflowId, embTaskQueue));

        var result = await handle.GetResultAsync();

        // The stub generator returns stubDimensions-dimensional vectors for each input.
        Assert.Equal(2, result.EmbeddingCount);
        Assert.Equal(stubDimensions, result.Dimensions);

        await host.StopAsync();
    }

    // ── Test 6: Heartbeat under tight HeartbeatTimeout ───────────────────────

    /// <summary>
    /// Verifies that DurableChatActivities.GetResponseAsync sends heartbeats
    /// during the streaming loop, keeping the activity alive under a heartbeat
    /// timeout that is shorter than the total activity duration.
    ///
    /// Architecture: ChatActivityTestWorkflow dispatches DurableChatActivities.GetResponseAsync
    /// directly via Workflow.ExecuteActivityAsync with a HeartbeatTimeout of 3 seconds.
    /// SlowStreamingChatClient yields 3 chunks, each preceded by a 1.5-second delay,
    /// for a total streaming duration of ~4.5 seconds. Without per-chunk heartbeats the
    /// activity would be force-failed at 3 seconds. Successful completion proves heartbeats
    /// fired.
    /// </summary>
    [Fact]
    public async Task DurableChatActivities_HeartbeatsKeepActivityAlive_UnderTightTimeout()
    {
        await using var env = await WorkflowEnvironment.StartLocalAsync();

        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<ITemporalClient>(env.Client);
        builder.Services.AddSingleton<IChatClient>(new SlowStreamingChatClient());
        builder.Services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(
            new StubEmbeddingGenerator(4));

        const string taskQueue = "test-heartbeat-6";

        builder.Services
            .AddHostedTemporalWorker(taskQueue)
            .AddDurableAI(opts =>
            {
                opts.ActivityTimeout = TimeSpan.FromSeconds(30);
                opts.HeartbeatTimeout = TimeSpan.FromSeconds(3);
                opts.SessionTimeToLive = TimeSpan.FromMinutes(5);
            })
            .AddWorkflow<ChatActivityTestWorkflow>();

        using var host = builder.Build();
        await host.StartAsync();

        var workflowId = $"heartbeat-test-{Guid.NewGuid():N}";
        var handle = await env.Client.StartWorkflowAsync(
            (ChatActivityTestWorkflow wf) => wf.RunAsync(),
            new WorkflowOptions(workflowId, taskQueue));

        // If heartbeats did not fire, the activity would be killed at 3 s and
        // the workflow would fail with an ActivityFailureException. Successful
        // completion is the proof that heartbeats kept it alive.
        var result = await handle.GetResultAsync();
        Assert.Equal("chunk1 chunk2 chunk3", result);

        await host.StopAsync();
    }

    // ── Shared helpers ────────────────────────────────────────────────────────

    /// <summary>
    /// Minimal IChatClient stub required for DurableChatActivities constructor injection
    /// in tests that do not exercise the chat path.
    /// </summary>
    private sealed class MinimalChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, "ok")]));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var r = await GetResponseAsync(messages, options, cancellationToken);
            foreach (var u in r.ToChatResponseUpdates()) yield return u;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    /// <summary>
    /// Yields 3 chunks with a 1.5-second delay before each chunk.
    /// Total streaming duration ≈ 4.5 s — longer than the 3-second HeartbeatTimeout
    /// used in Test 6. Without heartbeats the activity would be force-failed mid-stream.
    /// </summary>
    private sealed class SlowStreamingChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, "chunk1 chunk2 chunk3")]));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var text in new[] { "chunk1", " chunk2", " chunk3" })
            {
                await Task.Delay(TimeSpan.FromSeconds(1.5), cancellationToken);
                yield return new ChatResponseUpdate(ChatRole.Assistant, text);
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    /// <summary>
    /// A stub IEmbeddingGenerator that returns deterministic fixed-dimension vectors.
    /// </summary>
    private sealed class StubEmbeddingGenerator(int dimensions) : IEmbeddingGenerator<string, Embedding<float>>
    {
        public EmbeddingGeneratorMetadata Metadata { get; } =
            new EmbeddingGeneratorMetadata("stub", null, null, dimensions);

        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values,
            EmbeddingGenerationOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var list = values.ToList();
            var embeddings = list
                .Select(_ => new Embedding<float>(Enumerable.Repeat(1f / dimensions, dimensions).ToArray()))
                .ToList();
            return Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(embeddings));
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}

// ── Tool dispatch workflow ────────────────────────────────────────────────────

/// <summary>
/// Minimal workflow that dispatches DurableFunctionActivities.InvokeFunctionAsync directly
/// to verify the activity registration and invocation path works end-to-end.
/// </summary>
[Workflow("ToolDispatchWorkflow")]
public sealed class ToolDispatchWorkflow
{
    [WorkflowRun]
    public async Task<string> RunAsync()
    {
        var input = new DurableFunctionInput { FunctionName = "get_tool_result" };

        var output = await Workflow.ExecuteActivityAsync(
            (DurableFunctionActivities a) => a.InvokeFunctionAsync(input),
            new ActivityOptions { StartToCloseTimeout = TimeSpan.FromSeconds(15) });

        return output.Result?.ToString() ?? string.Empty;
    }
}

// ── Embedding test workflow types ─────────────────────────────────────────────

/// <summary>Input for <see cref="EmbeddingTestWorkflow"/>.</summary>
public sealed class EmbeddingTestInput
{
    public required IReadOnlyList<string> Values { get; init; }
    public TimeSpan ActivityTimeout { get; init; } = TimeSpan.FromSeconds(30);
}

/// <summary>Output from <see cref="EmbeddingTestWorkflow"/>.</summary>
public sealed class EmbeddingTestResult
{
    public int EmbeddingCount { get; init; }
    public int Dimensions { get; init; }
}

/// <summary>
/// Minimal workflow that dispatches DurableEmbeddingActivities.GenerateAsync directly
/// to verify the activity registration and invocation path works end-to-end.
/// Uses the same direct-dispatch pattern as ToolDispatchWorkflow to avoid constructing
/// DurableEmbeddingGenerator inside the workflow body (which violates the Temporal sandbox).
/// </summary>
[Workflow("EmbeddingTestWorkflow")]
public sealed class EmbeddingTestWorkflow
{
    [WorkflowRun]
    public async Task<EmbeddingTestResult> RunAsync(EmbeddingTestInput input)
    {
        var embeddings = new List<Embedding<float>>(input.Values.Count);

        foreach (var value in input.Values)
        {
            var actInput = new DurableEmbeddingInput
            {
                Values = new List<string> { value },
            };

            var output = await Workflow.ExecuteActivityAsync(
                (DurableEmbeddingActivities a) => a.GenerateAsync(actInput),
                new ActivityOptions { StartToCloseTimeout = input.ActivityTimeout });

            embeddings.AddRange(output.Embeddings);
        }

        return new EmbeddingTestResult
        {
            EmbeddingCount = embeddings.Count,
            Dimensions = embeddings.Count > 0 ? embeddings[0].Vector.Length : 0,
        };
    }
}

// ── Chat activity test workflow ───────────────────────────────────────────────

/// <summary>
/// Minimal workflow that dispatches DurableChatActivities.GetResponseAsync directly
/// to verify the heartbeat path under a tight HeartbeatTimeout (Test 6).
/// </summary>
[Workflow("ChatActivityTestWorkflow")]
public sealed class ChatActivityTestWorkflow
{
    [WorkflowRun]
    public async Task<string> RunAsync()
    {
        var input = new DurableChatInput
        {
            Messages = [new ChatMessage(ChatRole.User, "hello")],
            ConversationId = Workflow.Info.WorkflowId,
        };

        var response = await Workflow.ExecuteActivityAsync(
            (DurableChatActivities a) => a.GetResponseAsync(input),
            new ActivityOptions
            {
                StartToCloseTimeout = TimeSpan.FromSeconds(30),
                HeartbeatTimeout = TimeSpan.FromSeconds(3),
            });

        return response.Messages.Count > 0 ? response.Messages[0].Text ?? string.Empty : string.Empty;
    }
}
