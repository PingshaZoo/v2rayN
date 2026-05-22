namespace ServiceLib.Handler.AdaptiveNodeScheduler;

/// <summary>
/// P2.1: Polls xray /debug/vars every 5s to detect "high score but zero throughput"
/// anomalies. When a node has score > 30 but bytes/sec drops below 1 KB/s,
/// <see cref="ThroughputAnomalyDetected"/> is raised so the caller can trigger
/// a re-probe rather than waiting for the next scheduled probe cycle.
///
/// <h2>CRITICAL: Throughput is NOT a node-quality signal (§6.6, P0#2)</h2>
/// Throughput suffers from fundamental causality inversion:
///   user behavior → throughput (NOT node quality → throughput)
/// Throughput measures "what the user is doing", not "how good the node is".
///
/// <b>Triple prohibition (三重禁止规则):</b>
///   1. PROHIBITED from entering HealthScore
///   2. PROHIBITED from influencing cooldown decisions
///   3. PROHIBITED from influencing active-set membership
///
/// <b>Permitted uses only:</b>
///   - Composite anomaly hint: high score + sustained near-zero throughput
///     + persistent failures ALL THREE simultaneously → trigger re-probe suspicion
///   - Telemetry recording (debugging hint, NOT automated decision input)
///   - Scheduling quality observation (QualityMetricsReporter quality_metrics events)
///
/// <h2>Thread safety</h2>
/// All state mutation happens inside the single-threaded RunAsync loop.
/// The <see cref="ThroughputAnomalyDetected"/> event is raised synchronously
/// from that loop — subscribers should not block.
///
/// <h2>Start/Stop</h2>
/// <see cref="Start"/> begins the background polling loop.
/// <see cref="Stop"/> cancels it synchronously.
/// <see cref="DisposeAsync"/> calls Stop and cleans up.
/// </summary>
public sealed class XrayStatsPoller : IAsyncDisposable
{
    private const int DefaultPollIntervalMs = 5000;
    private const double AnomalyBpsThreshold = 1024.0;
    private const double AnomalyScoreThreshold = 30.0;

    private readonly IXrayStatsClient _client;
    private readonly IReadOnlyList<NodeState> _nodes;
    private readonly Dictionary<string, long> _lastBytes = new(StringComparer.Ordinal);
    private readonly int _pollIntervalMs;
    private CancellationTokenSource? _cts;
    private bool _started;

    public XrayStatsPoller(IXrayStatsClient client, IReadOnlyList<NodeState> nodes)
        : this(client, nodes, DefaultPollIntervalMs)
    {
    }

    /// <summary>
    /// Constructor with configurable poll interval — primarily for unit tests
    /// that need fast polling to avoid 5s delays.
    /// </summary>
    public XrayStatsPoller(IXrayStatsClient client, IReadOnlyList<NodeState> nodes, int pollIntervalMs)
    {
        _client = client;
        _nodes = nodes;
        _pollIntervalMs = pollIntervalMs;
    }

    /// <summary>
    /// Raised when a node has score > 30 but estimated throughput is below 1 KB/s.
    /// Parameters: (tag, estimatedBytesPerSecond).
    /// </summary>
    public event Action<string, double>? ThroughputAnomalyDetected;

    /// <summary>
    /// Executes a single poll cycle synchronously. Exposed for unit tests so they
    /// can control the exact sequence of stats updates without relying on timing.
    /// </summary>
    public async Task TriggerPollAsync()
    {
        await PollOnceAsync().ConfigureAwait(false);
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
            await Task.Delay(_pollIntervalMs, ct).ConfigureAwait(false);
            if (ct.IsCancellationRequested) break;

            try
            {
                await PollOnceAsync().ConfigureAwait(false);
            }
            catch
            {
                // ignore single-poll failures; continue next cycle
            }
        }
    }

    private async Task PollOnceAsync()
    {
        var stats = await _client.GetOutboundStatsAsync().ConfigureAwait(false);

        foreach (var (tag, currentBytes) in stats)
        {
            if (_lastBytes.TryGetValue(tag, out long last))
            {
                long delta = currentBytes - last;
                if (delta < 0)
                {
                    // counter reset (xray restart) — re-baseline
                    _lastBytes[tag] = currentBytes;
                    continue;
                }

                double bps = delta / (_pollIntervalMs / 1000.0);
                UpdateThroughputHint(tag, bps);
            }
            _lastBytes[tag] = currentBytes;
        }
    }

    private void UpdateThroughputHint(string tag, double bps)
    {
        var node = _nodes.FirstOrDefault(n => n.Tag == tag);
        if (node is null) return;

        if (bps < AnomalyBpsThreshold && node.Score > AnomalyScoreThreshold)
            ThroughputAnomalyDetected?.Invoke(tag, bps);
    }

    public async ValueTask DisposeAsync()
    {
        Stop();
        await Task.CompletedTask;
    }
}
