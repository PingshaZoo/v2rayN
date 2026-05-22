namespace ServiceLib.Handler.AdaptiveNodeScheduler;

/// <summary>
/// P3.2: Periodic reporter that computes <see cref="SchedulingQualityMetrics"/>
/// every 5 minutes and writes a "quality_metrics" JSONL event to the
/// <see cref="ScoreLogger"/>. Runs as a background task.
///
/// <h2>Start/Stop</h2>
/// <see cref="Start"/> begins the background loop.
/// <see cref="Stop"/> cancels it synchronously.
///
/// <h2>Thread safety</h2>
/// <see cref="SchedulingQualityMetrics.Compute"/> reads NodeState properties
/// (atomic doubles on x64) — safe to call from the background thread.
/// </summary>
public sealed class QualityMetricsReporter
{
    private readonly IReadOnlyList<NodeState> _nodes;
    private readonly ScoreLogger? _logger;
    private readonly TimeSpan _interval;
    private CancellationTokenSource? _cts;
    private bool _started;

    public QualityMetricsReporter(IReadOnlyList<NodeState> nodes,
                                  ScoreLogger? logger,
                                  TimeSpan? interval = null)
    {
        _nodes = nodes;
        _logger = logger;
        _interval = interval ?? TimeSpan.FromMinutes(5);
    }

    public void Start()
    {
        if (_started) return;
        _started = true;
        _cts = new CancellationTokenSource();
        _ = RunAsync(_cts.Token);
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        _started = false;
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(_interval, ct).ConfigureAwait(false);
            if (ct.IsCancellationRequested) break;

            try
            {
                var snapshot = SchedulingQualityMetrics.Compute(_nodes);
                _logger?.LogEvent("quality_metrics", new Dictionary<string, object?>
                {
                    ["entropy"] = snapshot.Entropy.ToString("F4"),
                    ["normalized_entropy"] = snapshot.NormalizedEntropy.ToString("F4"),
                    ["p95_latency_ms"] = snapshot.P95LatencyMs.ToString("F0"),
                    ["mean_score"] = snapshot.MeanScore.ToString("F1"),
                    ["score_stddev"] = snapshot.ScoreStdDev.ToString("F1"),
                    ["active_nodes"] = snapshot.ActiveNodeCount,
                    ["cooldown_nodes"] = snapshot.CooldownNodeCount,
                });
            }
            catch
            {
                // best-effort telemetry — never crash the loop
            }
        }
    }
}
