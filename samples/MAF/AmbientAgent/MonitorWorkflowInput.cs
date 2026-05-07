namespace AmbientAgent;

/// <summary>
/// Input for <see cref="MonitorWorkflow"/>. Supports continue-as-new by carrying
/// the rolling buffer and total readings count forward.
/// </summary>
public record MonitorWorkflowInput
{
    /// <summary>Workflow ID of the alert workflow to signal when anomalies are detected.</summary>
    public required string AlertWorkflowId { get; init; }

    /// <summary>Number of readings to collect before triggering an LLM analysis.</summary>
    public int AnalysisInterval { get; init; } = 5;

    /// <summary>Maximum number of readings to keep in the rolling buffer.</summary>
    public int MaxBufferSize { get; init; } = 50;

    /// <summary>Buffer carried forward from a previous run (continue-as-new).</summary>
    public List<HealthCheckData> CarriedBuffer { get; init; } = [];

    /// <summary>Total readings processed across all runs (carried forward).</summary>
    public int CarriedTotalReadings { get; init; }

    /// <summary>Count of readings since the last analysis, carried from the previous run.</summary>
    public int CarriedReadingsSinceLastAnalysis { get; init; }
}
