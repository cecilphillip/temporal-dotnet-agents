using System.Collections.Generic;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Temporalio.Common;
using Temporalio.Extensions.AI;
using Temporalio.Extensions.Agents.HistoryStore;

namespace Temporalio.Extensions.Agents;

/// <summary>
/// Immutable snapshot of a <see cref="DurableAgentBuilder"/> taken at the end of the
/// <c>TemporalAgentsOptions.AddDurableAgent</c> configure delegate. Phase 2 stores this on
/// <see cref="TemporalAgentsOptions"/>; Phase 3 reads it from the workflow loop.
/// </summary>
internal sealed record DurableAgentRegistration(
    string Name,
    string? Description,
    string? Instructions,
    Func<IServiceProvider, IChatClient> ChatClient,
    ChatOptions? ChatOptions,
    IReadOnlyList<DurableToolRegistration> Tools,
    IReadOnlyList<Func<IServiceProvider, AIContextProvider>> ContextProviderFactories,
    Func<IServiceProvider, IAgentHistoryStore>? HistoryStore,
    TimeSpan? TimeToLive,
    TimeSpan? ApprovalTimeout,
    TimeSpan? ActivityTimeout,
    TimeSpan? HeartbeatTimeout,
    RetryPolicy? RetryPolicy,
    int? MaxEntryCount,
    int MaxToolCallsPerTurn,
    Func<IList<DurableSessionEntry>, IList<DurableSessionEntry>>? HistoryReducer);
