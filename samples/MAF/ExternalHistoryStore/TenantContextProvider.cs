using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace ExternalHistoryStore;

/// <summary>
/// MAF-level (Layer 2) <see cref="AIContextProvider"/> that injects per-turn tenant
/// metadata into the LLM call as a system <see cref="ChatMessage"/>.
///
/// <para>Tenants are identified by a <c>TenantId</c> stamped on
/// <see cref="ChatMessage.AdditionalProperties"/> by the workflow before each agent
/// invocation. Inside the activity, this provider scans the input messages for the most
/// recent message bearing that property, looks up the tenant in the DI-injected
/// <see cref="TenantDirectory"/>, and returns an <see cref="AIContext"/> containing
/// one tenant-scoped system message.</para>
///
/// <para>This provider does not know about the workflow-level <see cref="Temporalio.Extensions.Agents.HistoryStore.IAgentHistoryStore"/>;
/// the two abstractions are independent — see this sample's README for the layered
/// architecture diagram.</para>
/// </summary>
public sealed class TenantContextProvider : AIContextProvider
{
    /// <summary>
    /// Key used on <see cref="ChatMessage.AdditionalProperties"/> to identify the tenant
    /// for the current call. The workflow stamps this on its outgoing user message.
    /// </summary>
    public const string TenantIdProperty = "TenantId";

    private readonly TenantDirectory _directory;
    private long _invokingCalls;

    public TenantContextProvider(TenantDirectory directory)
        : base(provideInputMessageFilter: null,
               storeInputRequestMessageFilter: null,
               storeInputResponseMessageFilter: null)
    {
        _directory = directory ?? throw new ArgumentNullException(nameof(directory));
    }

    /// <summary>Sample-only: number of times this provider's <c>InvokingAsync</c> has fired.</summary>
    public long InvokingCalls => Interlocked.Read(ref _invokingCalls);

    /// <inheritdoc/>
    protected override ValueTask<AIContext> ProvideAIContextAsync(
        InvokingContext context,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _invokingCalls);

        var tenantId = ExtractActiveTenantId(context.AIContext.Messages);
        if (tenantId is null)
        {
            // No tenant stamped — return an empty AIContext (no extra messages, no tools).
            return new ValueTask<AIContext>(new AIContext());
        }

        var tenant = _directory.TryGet(tenantId);
        if (tenant is null)
        {
            // Unknown tenant — degrade gracefully rather than failing the LLM call.
            return new ValueTask<AIContext>(new AIContext());
        }

        var systemText = $"Active tenant: {tenant.Name} (tier {tenant.Tier}). " +
                         $"SLA: {tenant.SlaPercent:F1}%. Preferred language: {tenant.Lang}.";

        return new ValueTask<AIContext>(new AIContext
        {
            Messages = [new ChatMessage(ChatRole.System, systemText)],
        });
    }

    private static string? ExtractActiveTenantId(IEnumerable<ChatMessage>? messages)
    {
        if (messages is null)
        {
            return null;
        }

        // Walk most-recent-first so the latest tenant wins when a session is reused
        // across tenants (uncommon but the right semantic for ambient state).
        foreach (var msg in messages.Reverse())
        {
            if (msg.AdditionalProperties is { } props
                && props.TryGetValue(TenantIdProperty, out var raw)
                && raw is string s
                && !string.IsNullOrWhiteSpace(s))
            {
                return s;
            }
        }

        return null;
    }
}
