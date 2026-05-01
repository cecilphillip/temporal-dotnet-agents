using Microsoft.Agents.AI;

namespace Temporalio.Extensions.Agents;

/// <summary>
/// Options for running a Temporal agent.
/// </summary>
public sealed class TemporalAgentRunOptions : AgentRunOptions
{
    /// <summary>Initializes a new instance of the <see cref="TemporalAgentRunOptions"/> class.</summary>
    public TemporalAgentRunOptions()
    {
    }

    private TemporalAgentRunOptions(TemporalAgentRunOptions options) : base(options)
    {
        this.EnableToolCalls = options.EnableToolCalls;
        this.EnableToolNames = options.EnableToolNames is not null
            ? new List<string>(options.EnableToolNames)
            : null;
        this.IsFireAndForget = options.IsFireAndForget;
        this.CorrelationId = options.CorrelationId;
    }

    /// <summary>Gets or sets whether to enable tool calls. Defaults to <c>true</c>.</summary>
    public bool EnableToolCalls { get; set; } = true;

    /// <summary>
    /// Gets or sets the collection of tool names to enable.
    /// If <see langword="null"/>, all tools are enabled.
    /// </summary>
    public IList<string>? EnableToolNames { get; set; }

    /// <summary>
    /// Gets or sets whether to fire and forget the request.
    /// When <c>true</c>, the proxy sends a signal and returns immediately with an empty response.
    /// </summary>
    public bool IsFireAndForget { get; set; }

    /// <summary>
    /// Gets or sets the optional caller-supplied correlation ID for this run.
    /// </summary>
    /// <remarks>
    /// <para>
    /// When <see langword="null"/> or empty, the proxy auto-generates a fresh GUID
    /// (<c>Guid.NewGuid().ToString("N")</c>). Workflow-context callers (<see cref="TemporalAIAgent"/>)
    /// generate via <c>Workflow.NewGuid().ToString("N")</c> for replay determinism.
    /// </para>
    /// <para>
    /// This is per-turn (each <c>RunAsync</c> call), not per-session. Use it to thread upstream
    /// HTTP/gRPC trace IDs into agent execution for cross-system log correlation.
    /// </para>
    /// </remarks>
    public string? CorrelationId { get; set; }

    /// <inheritdoc/>
    public override AgentRunOptions Clone() => new TemporalAgentRunOptions(this);
}
