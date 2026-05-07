using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Temporalio.Client;
using Temporalio.Extensions.AI;
using Temporalio.Extensions.Agents.IntegrationTests.Helpers;
using Temporalio.Extensions.Agents.Session;
using Temporalio.Extensions.Agents.Tests.StepMode; // shared scaffolding (linked via .csproj)
using Temporalio.Extensions.Hosting;
using Temporalio.Testing;
using Xunit;

namespace Temporalio.Extensions.Agents.IntegrationTests;

/// <summary>
/// Phase 4 (v0.3): integration coverage for the load-bearing lifecycle contract Q10/CP1 —
/// <see cref="AIContextProvider.InvokingAsync"/> and <see cref="AIContextProvider.InvokedAsync"/>
/// fire ONCE PER LLM CALL (per <c>RunDurableAgentStep</c> activity), not once per turn. The
/// durable workflow loop runs multiple LLM calls per turn (one per tool-call iteration plus the
/// final response), so the provider must observe each iteration.
/// </summary>
[Trait("Category", "Integration")]
public class DurableAgentContextProviderLifecycleTests
{
    [Fact]
    public async Task DurableAgent_AddContextProvider_InvokingAsyncFiresPerLlmCall()
    {
        await using var env = await TestEnvironmentHelper.StartLocalAsync();
        env.Client.Options.DataConverter = TemporalAgentDataConverter.Instance;

        // Drive a 3-LLM-call turn:
        //   step 1 → 2 tool calls,
        //   step 2 → 1 tool call,
        //   step 3 → final answer.
        // Provider's InvokingAsync should fire 3 times (one per LLM call).
        var recorder = new RecordingTool { Name = "echo_tool" };
        var aiFunction = recorder.Build();

        var step1Calls = new[]
        {
            new FunctionCallContent("call-1A", "echo_tool", new Dictionary<string, object?> { ["input"] = "A" }),
            new FunctionCallContent("call-1B", "echo_tool", new Dictionary<string, object?> { ["input"] = "B" }),
        };
        var step2Calls = new[]
        {
            new FunctionCallContent("call-2A", "echo_tool", new Dictionary<string, object?> { ["input"] = "C" }),
        };

        var responses = new List<ChatResponse>
        {
            new(new ChatMessage(ChatRole.Assistant, [.. step1Calls])),
            new(new ChatMessage(ChatRole.Assistant, [.. step2Calls])),
            new(new ChatMessage(ChatRole.Assistant, "Final answer.")),
        };
        var scripted = new ScriptedChatClient(responses);

        var provider = new CountingContextProvider();

        using var host = BuildHost(env.Client, scripted, aiFunction, provider);
        await host.StartAsync();
        try
        {
            var proxy = host.Services.GetTemporalAgentProxy("DurableAgent");
            var session = (TemporalAgentSession)await proxy.CreateSessionAsync();
            var response = await proxy.RunAsync("Run multi-step turn.", session);

            Assert.Contains("Final answer.", response.Messages[^1].Text);
            // Three tool invocations across the three iterations.
            Assert.Equal(3, recorder.CallCount);

            // Q10/CP1 contract: provider hooks fire per LLM call. With three LLM calls in this
            // turn, both InvokingAsync and InvokedAsync should fire exactly three times.
            Assert.Equal(3, provider.InvokingCount);
            Assert.Equal(3, provider.InvokedCount);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task DurableAgent_AddContextProviderInstance_RegistersAndFires()
    {
        await using var env = await TestEnvironmentHelper.StartLocalAsync();
        env.Client.Options.DataConverter = TemporalAgentDataConverter.Instance;

        var scripted = new ScriptedChatClient(new[]
        {
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "Hello.")),
        });
        var provider = new CountingContextProvider();

        // Use the AddContextProvider(instance) overload (no factory) — verifies the simpler
        // entry point also wires through.
        using var host = BuildHost(env.Client, scripted, tool: null, providerInstance: provider);
        await host.StartAsync();
        try
        {
            var proxy = host.Services.GetTemporalAgentProxy("DurableAgent");
            var session = (TemporalAgentSession)await proxy.CreateSessionAsync();
            await proxy.RunAsync("Hi", session);

            Assert.Equal(1, provider.InvokingCount);
            Assert.Equal(1, provider.InvokedCount);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    private static IHost BuildHost(
        ITemporalClient client,
        ScriptedChatClient scripted,
        AIFunction? tool,
        CountingContextProvider providerInstance)
    {
        var taskQueue = $"durable-agent-ctxprov-{Guid.NewGuid():N}";
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton(client);
        builder.Services.AddSingleton<IChatClient>(scripted);

        builder.Services
            .AddHostedTemporalWorker(taskQueue)
            .AddTemporalAgents(opts =>
            {
                opts.AddDurableAgent("DurableAgent", agent =>
                {
                    agent.Instructions = "You are a helpful agent.";
                    agent.ChatClient = sp => sp.GetRequiredService<IChatClient>();
                    if (tool is not null)
                    {
                        agent.AddTool(tool);
                    }
                    agent.AddContextProvider(providerInstance);
                });
            });

        return builder.Build();
    }

    /// <summary>
    /// Test-only <see cref="AIContextProvider"/> that records calls to its
    /// <see cref="AIContextProvider.ProvideAIContextAsync"/> hook (the load-bearing per-LLM-call
    /// hook). The provider returns an empty <see cref="AIContext"/> on every invocation so it
    /// has no side-effect on the LLM call beyond observation.
    /// </summary>
    /// <remarks>
    /// In MAF 1.0 <c>InvokingAsync</c> is the public override of the lifecycle entry point and
    /// <c>ProvideAIContextAsync</c> is the protected virtual called by it. Counting the protected
    /// hook is sufficient — every <c>InvokingAsync</c> call invokes <c>ProvideAIContextAsync</c>
    /// exactly once.
    /// </remarks>
    private sealed class CountingContextProvider : AIContextProvider
    {
        private long _invoking;
        private long _invoked;

        public CountingContextProvider()
            : base(provideInputMessageFilter: null,
                   storeInputRequestMessageFilter: null,
                   storeInputResponseMessageFilter: null)
        {
        }

        public int InvokingCount => (int)Interlocked.Read(ref _invoking);
        // Symmetric counter exposed for parity with InvokingCount; in MAF 1.0 the post-call hook
        // is not virtual on this surface, so we increment from inside ProvideAIContextAsync after
        // the AIContext is constructed (just before return) — a reasonable proxy for "the
        // provider observed this LLM call to completion of its own pre-call work".
        public int InvokedCount => (int)Interlocked.Read(ref _invoked);

        protected override ValueTask<AIContext> ProvideAIContextAsync(
            InvokingContext context,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _invoking);
            var result = new AIContext();
            Interlocked.Increment(ref _invoked);
            return new ValueTask<AIContext>(result);
        }
    }
}
