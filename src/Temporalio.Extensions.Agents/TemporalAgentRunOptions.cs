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

    /// <inheritdoc/>
    public override AgentRunOptions Clone() => new TemporalAgentRunOptions(this);
}
