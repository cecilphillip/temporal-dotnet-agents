using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Temporalio.Client;
using Temporalio.Extensions.Hosting;
using Temporalio.Testing;
using Xunit;

namespace Temporalio.Extensions.AI.IntegrationTests;

/// <summary>
/// Integration tests for the keyed <see cref="IChatClient"/> DI resolution feature.
/// Each test spins up its own <see cref="WorkflowEnvironment"/> and hosted worker so that
/// the <see cref="DurableExecutionOptions.DefaultChatClientKey"/> can be configured per-test.
/// </summary>
public class DurableChatKeyedClientIntegrationTests
{
    private const string TaskQueue = "integration-test-ai-keyed";
    private const string DefaultKey = "default-client";
    private const string OtherKey = "other-client";

    [Fact]
    public async Task KeyedClient_WorkerUsesDefaultKey_WhenRegisteredWithKeyedClientOnly()
    {
        await using var env = await WorkflowEnvironment.StartLocalAsync();

        var defaultClient = new LabeledChatClient("default");
        var otherClient = new LabeledChatClient("other");

        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<ITemporalClient>(env.Client);

        // Register two keyed clients — no unkeyed registration.
        builder.Services.AddKeyedSingleton<IChatClient>(DefaultKey, defaultClient);
        builder.Services.AddKeyedSingleton<IChatClient>(OtherKey, otherClient);

        builder.Services
            .AddHostedTemporalWorker(TaskQueue)
            .AddDurableAI(opts =>
            {
                opts.ActivityTimeout = TimeSpan.FromSeconds(30);
                opts.HeartbeatTimeout = TimeSpan.FromSeconds(10);
                opts.SessionTimeToLive = TimeSpan.FromMinutes(5);
                opts.DefaultChatClientKey = DefaultKey;
            });

        using var host = builder.Build();
        await host.StartAsync();

        var sessionClient = host.Services.GetRequiredService<DurableChatSessionClient>();

        // --- No per-call key: should route to DefaultKey client ---
        var conversationId = $"keyed-default-{Guid.NewGuid():N}";
        var response = await sessionClient.ChatAsync(
            conversationId,
            [new ChatMessage(ChatRole.User, "hello")]);

        Assert.NotNull(response);
        // LabeledChatClient echoes "label: <message>" so we can assert which client replied.
        Assert.Contains("default", response.Messages[0].Text);

        // --- Per-call override: should route to OtherKey client ---
        var otherConversationId = $"keyed-other-{Guid.NewGuid():N}";
        var overrideOptions = new ChatOptions().WithChatClientKey(OtherKey);

        var otherResponse = await sessionClient.ChatAsync(
            otherConversationId,
            [new ChatMessage(ChatRole.User, "hello")],
            overrideOptions);

        Assert.NotNull(otherResponse);
        Assert.Contains("other", otherResponse.Messages[0].Text);

        await host.StopAsync();
    }

    /// <summary>
    /// A minimal IChatClient stub that prefixes all responses with a label
    /// so integration tests can identify which client handled a request.
    /// </summary>
    private sealed class LabeledChatClient(string label) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var lastMessage = messages
                .LastOrDefault(m => m.Role == ChatRole.User)?.Text ?? "(empty)";

            var response = new ChatResponse(
                [new ChatMessage(ChatRole.Assistant, $"{label}: {lastMessage}")])
            {
                ModelId = "test-model",
                Usage = new UsageDetails
                {
                    InputTokenCount = lastMessage.Length,
                    OutputTokenCount = lastMessage.Length + label.Length + 2,
                },
            };
            return Task.FromResult(response);
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var response = await GetResponseAsync(messages, options, cancellationToken);
            foreach (var update in response.ToChatResponseUpdates())
                yield return update;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
