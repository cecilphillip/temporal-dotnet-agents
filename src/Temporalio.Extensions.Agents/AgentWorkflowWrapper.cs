using System.Runtime.CompilerServices;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Temporalio.Extensions.Agents.Session;
using Temporalio.Extensions.Agents.Workflows;

namespace Temporalio.Extensions.Agents;

/// <summary>
/// Wraps the real <see cref="AIAgent"/> inside a Temporal activity, applying request-specific
/// settings (response format, tool filtering). Equivalent to <c>EntityAgentWrapper</c>.
/// </summary>
internal sealed class AgentWorkflowWrapper(
    AIAgent innerAgent,
    RunRequest runRequest,
    TemporalAgentSession session,
    IServiceProvider? services = null) : DelegatingAIAgent(innerAgent)
{
    private readonly RunRequest _runRequest = runRequest;
    private readonly TemporalAgentSession _session = session;
    private readonly IServiceProvider? _services = services;

    protected override string? IdCore => _session.SessionId.WorkflowId;

    protected override async Task<AgentResponse> RunCoreAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session = null,
        AgentRunOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var response = await base.RunCoreAsync(
            messages,
            session,
            GetRunOptions(options),
            cancellationToken);

        response.AgentId = this.Id;
        return response;
    }

    protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session = null,
        AgentRunOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var update in base.RunCoreStreamingAsync(
            messages,
            session,
            GetRunOptions(options),
            cancellationToken))
        {
            update.AgentId = this.Id;
            yield return update;
        }
    }

    public override object? GetService(Type serviceType, object? serviceKey = null)
    {
        if (serviceType == typeof(TemporalAgentSessionId))
        {
            return _session.SessionId;
        }

        object? result = null;
        if (_services is not null)
        {
            result = (serviceKey is not null && _services is IKeyedServiceProvider ksp)
                ? ksp.GetKeyedService(serviceType, serviceKey)
                : _services.GetService(serviceType);
        }

        return result ?? base.GetService(serviceType, serviceKey);
    }

    /// <summary>Builds run options that apply the <see cref="RunRequest"/>'s tool/format settings.</summary>
    internal AgentRunOptions GetRunOptions(AgentRunOptions? options = null)
    {
        if (options is null || options.GetType() == typeof(AgentRunOptions))
        {
            options = new ChatClientAgentRunOptions();
        }

        if (options is not ChatClientAgentRunOptions chatAgentRunOptions)
        {
            throw new NotSupportedException(
                $"AgentWorkflowWrapper only supports null, {nameof(AgentRunOptions)}, or {nameof(ChatClientAgentRunOptions)} run options. " +
                $"Got: {options?.GetType().FullName ?? "null"}.");
        }

        Func<IChatClient, IChatClient>? originalFactory = chatAgentRunOptions.ChatClientFactory;

        chatAgentRunOptions.ChatClientFactory = chatClient =>
        {
            ChatClientBuilder builder = chatClient.AsBuilder();
            if (originalFactory is not null)
            {
                builder.Use(originalFactory);
            }

            return builder.ConfigureOptions(newOptions =>
            {
                // Apply response format override from the request.
                if (_runRequest.ResponseFormat is not null)
                {
                    newOptions.ResponseFormat = _runRequest.ResponseFormat;
                }

                // Apply tool filtering from the request.
                if (_runRequest.EnableToolCalls)
                {
                    IList<AITool>? tools = chatAgentRunOptions.ChatOptions?.Tools;
                    if (tools is not null && _runRequest.EnableToolNames?.Count > 0)
                    {
                        newOptions.Tools = [.. tools.Where(tool =>
                            _runRequest.EnableToolNames.Contains(tool.Name))];
                    }
                }
                else
                {
                    newOptions.Tools = null;
                }
            }).Build();
        };

        return options;
    }
}
