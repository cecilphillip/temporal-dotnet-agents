using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Temporalio.Workflows;
using static Temporalio.Extensions.Agents.TemporalWorkflowExtensions;

namespace AmbientAgent;

/// <summary>
/// Ambient monitoring workflow that ingests health-check signals, periodically
/// calls an LLM to analyze trends, and signals an alert workflow on anomalies.
/// Supports continue-as-new for indefinite operation.
/// </summary>
[Workflow("AmbientAgent.MonitorWorkflow")]
public class MonitorWorkflow
{
    // M2: Queue<T> gives O(1) enqueue/dequeue; List<T>.RemoveAt(0) was O(n).
    private readonly Queue<HealthCheckData> _buffer = new();
    private readonly Queue<string> _recentAnalyses = new();

    // s6: Static readonly avoids allocating ActivityOptions on every loop iteration.
    private static readonly ActivityOptions AlertActivityOptions =
        new() { StartToCloseTimeout = TimeSpan.FromSeconds(30) };

    private int _totalReadings;
    private int _readingsSinceLastAnalysis;
    private bool _shutdownRequested;
    private MonitorWorkflowInput _input = null!;

    [WorkflowRun]
    public async Task RunAsync(MonitorWorkflowInput input)
    {
        _input = input;

        // Restore state carried forward from a previous run (continue-as-new).
        foreach (var reading in input.CarriedBuffer)  // M2: Enqueue into Queue<T>
            _buffer.Enqueue(reading);
        _totalReadings = input.CarriedTotalReadings;
        _readingsSinceLastAnalysis = input.CarriedReadingsSinceLastAnalysis;  // M1

        while (!_shutdownRequested)
        {
            // Wait until we have enough new readings for an analysis pass, or shutdown.
            // m6: Pass Workflow.CancellationToken so the wait is cancelled cleanly on shutdown.
            var conditionMet = await Workflow.WaitConditionAsync(
                () => _shutdownRequested
                      || _readingsSinceLastAnalysis >= input.AnalysisInterval
                      || Workflow.ContinueAsNewSuggested,
                timeout: TimeSpan.FromHours(1),
                cancellationToken: Workflow.CancellationToken)
                .ConfigureAwait(true);  // C1

            if (_shutdownRequested)
                break;

            // C3: Defer continue-as-new when a full analysis batch is ready — otherwise
            // the pending analysis would be silently dropped on the next run start.
            if (Workflow.ContinueAsNewSuggested && _readingsSinceLastAnalysis < input.AnalysisInterval)
            {
                // M1: Carry _readingsSinceLastAnalysis forward so the restored run does
                // not re-count readings that were already counted before the transition.
                throw Workflow.CreateContinueAsNewException(
                    (MonitorWorkflow wf) => wf.RunAsync(new MonitorWorkflowInput
                    {
                        AlertWorkflowId = input.AlertWorkflowId,
                        AnalysisInterval = input.AnalysisInterval,
                        MaxBufferSize = input.MaxBufferSize,
                        CarriedBuffer = _buffer.ToList(),
                        CarriedTotalReadings = _totalReadings,
                        CarriedReadingsSinceLastAnalysis = _readingsSinceLastAnalysis  // M1
                    }));
            }

            // m4: On timeout (conditionMet == false), the interval check below would also
            // be false — both guards point to the same "not enough readings yet" situation.
            // The first guard handles the timeout path; the second makes the intent explicit
            // for the non-timeout path where ContinueAsNewSuggested unblocked the wait but
            // the interval has not been reached yet.
            if (!conditionMet)
                continue; // Timeout with no readings — loop back and wait again.

            if (_readingsSinceLastAnalysis < input.AnalysisInterval)
                continue;

            // ── Analyze recent readings via LLM ──────────────────────────────
            _readingsSinceLastAnalysis = 0;

            // M3: Analyze only the most recent AnalysisInterval readings, not the full buffer.
            var summary = FormatReadingsForAnalysis(_buffer.TakeLast(input.AnalysisInterval));
            var analysisAgent = GetAgent("AnalysisAgent");

            // Fresh session per cycle: each LLM call is a stateless analysis of the current window.
            // To accumulate cross-cycle conversation history, store the session in a field and reuse it.
            var session = await analysisAgent.CreateSessionAsync().ConfigureAwait(true);  // C1, M4

            var response = await analysisAgent.RunAsync(  // C1
                [new ChatMessage(ChatRole.User, summary)],
                session).ConfigureAwait(true);

            var analysisResult = response.Text ?? string.Empty;
            _recentAnalyses.Enqueue(analysisResult);  // M2

            // Keep only the last 10 analyses in memory.
            while (_recentAnalyses.Count > 10)
                _recentAnalyses.Dequeue();  // M2: O(1) vs RemoveAt(0) O(n)

            Workflow.Logger.LogInformation(
                "Analysis complete ({TotalReadings} total readings): {Result}",
                _totalReadings, analysisResult);

            // ── Check for anomaly and signal alert workflow ───────────────────
            if (analysisResult.Contains("ANOMALY", StringComparison.OrdinalIgnoreCase))
            {
                var alert = new AnomalyAlert(
                    DetectedAt: Workflow.UtcNow,
                    Summary: analysisResult,
                    RecentReadings: _buffer.TakeLast(input.AnalysisInterval).ToList());

                await Workflow.ExecuteActivityAsync(  // C1
                    (AlertActivities a) => a.SignalAlertWorkflowAsync(input.AlertWorkflowId, alert),
                    AlertActivityOptions)  // s6: reuse static instance
                    .ConfigureAwait(true);
            }
        }
    }

    [WorkflowSignal("IngestHealthCheck")]
    public Task IngestHealthCheckAsync(HealthCheckData data)
    {
        _buffer.Enqueue(data);  // M2
        _totalReadings++;
        _readingsSinceLastAnalysis++;

        // Enforce max buffer size — drop oldest readings.
        // C2: Guard against _input being null if a signal arrives before the first workflow task.
        while (_input is not null && _buffer.Count > _input.MaxBufferSize)
            _buffer.Dequeue();  // M2: O(1) vs RemoveAt(0) O(n)

        return Task.CompletedTask;
    }

    [WorkflowSignal("Shutdown")]
    public Task ShutdownAsync()
    {
        _shutdownRequested = true;
        return Task.CompletedTask;
    }

    [WorkflowQuery("GetStatus")]
    public MonitorStatus GetStatus() =>
        new(_buffer.Count, _totalReadings, _recentAnalyses.ToList());

    // s1: Removed duplicate format instructions — agent.Instructions already tells the model
    // how to respond. The user message contains only the data to analyze.
    private static string FormatReadingsForAnalysis(IEnumerable<HealthCheckData> readings)  // M2: IEnumerable<T>
    {
        var lines = readings.Select(r =>
            $"[{r.Timestamp:HH:mm:ss}] CPU={r.CpuPercent:F1}% Mem={r.MemoryPercent:F1}% Temp={r.TemperatureCelsius:F1}°C");

        return $"Analyze these system health readings:\n\n" +
               string.Join("\n", lines);
    }
}
