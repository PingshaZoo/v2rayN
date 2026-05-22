using System.Net.Sockets;

namespace ServiceLib.Handler.AdaptiveNodeScheduler;

/// <summary>
/// P2.6: Active TTFB probe service with multi-target support.
/// P2.4: Concurrent probing capped at max(3, ceil(N/5)) to bound resource usage.
/// Probes each node through its dedicated xray SOCKS5 inbound port to measure latency
/// without going through the balancer. Supports multiple probe URLs; averages successful
/// TTFBs and only records failure when ALL targets fail.
///
/// <h2>P0#1 Recovery FSM integration</h2>
/// When a <see cref="RecoveryConfirmationFsm"/> is provided, nodes in recovery states
/// (RECOVERY_PROBING, STABILITY_VERIFICATION) are routed through the FSM instead of
/// the normal success/failure path. This enables the 4-stage recovery pipeline.
/// </summary>
public sealed class ProbeService : IDisposable
{
    private readonly string[] _probeUrls;
    private readonly int _probeTimeoutMs;
    private readonly int _intervalMs;
    private readonly SemaphoreSlim _concurrencyGate;
    private readonly int _maxConcurrency;
    private bool _disposed;
    private CancellationTokenSource? _cts;

    private readonly IReadOnlyList<NodeState> _nodes;
    private readonly Func<string, int> _portResolver; // node tag → local probe port
    private readonly FailureCollector _collector;
    private readonly RecoveryConfirmationFsm? _recoveryFsm;
    private readonly double _heavyFraction;
    private readonly Random _rng = new();
    private readonly ConcurrentDictionary<string, (SocketsHttpHandler handler, HttpClient client)> _pool = new();

    private const int MinConcurrency = 3;
    private const int MinTimeoutMs = 500;
    private const int DefaultTimeoutMs = 5000;
    private const int MinIntervalSec = 5;
    private const int DefaultIntervalSec = 30;
    private const string DefaultProbeUrl = "http://cp.cloudflare.com/";

    public ProbeService(IReadOnlyList<NodeState> nodes,
                        Func<string, int> portResolver,
                        FailureCollector collector,
                        AdaptiveSchedulerItem config)
        : this(nodes, portResolver, collector, config, null)
    {
    }

    /// <summary>
    /// P0#1: Constructor with optional RecoveryConfirmationFsm for the 4-stage recovery pipeline.
    /// </summary>
    public ProbeService(IReadOnlyList<NodeState> nodes,
                        Func<string, int> portResolver,
                        FailureCollector collector,
                        AdaptiveSchedulerItem config,
                        RecoveryConfirmationFsm? recoveryFsm)
    {
        _nodes = nodes;
        _portResolver = portResolver;
        _collector = collector;
        _recoveryFsm = recoveryFsm;

        // P2.4: concurrency cap = max(3, ceil(N/5))
        _maxConcurrency = Math.Max(MinConcurrency, (int)Math.Ceiling(nodes.Count / 5.0));
        _concurrencyGate = new SemaphoreSlim(_maxConcurrency, _maxConcurrency);

        var rawUrl = string.IsNullOrWhiteSpace(config.ProbeUrl)
            ? DefaultProbeUrl : config.ProbeUrl;
        // P2.6: split by newlines to support multiple probe targets
        _probeUrls = rawUrl
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(u => u.Trim())
            .Where(u => u.Length > 0)
            .ToArray();
        if (_probeUrls.Length == 0)
            _probeUrls = [DefaultProbeUrl];

        _probeTimeoutMs = config.ProbeTimeoutMs < MinTimeoutMs
            ? DefaultTimeoutMs : config.ProbeTimeoutMs;
        var intervalSec = config.ProbeIntervalSec < MinIntervalSec
            ? DefaultIntervalSec : config.ProbeIntervalSec;
        _intervalMs = intervalSec * 1000;

        // P1#7: Fraction of probes that use GET to defeat small-packet acceleration
        _heavyFraction = Math.Clamp(config.ProbeHeavyFraction, 0.0, 1.0);
    }

    /// <summary>
    /// P2.4: Returns the max concurrent probes allowed by this instance.
    /// For unit test verification of the concurrency formula.
    /// </summary>
    public int MaxConcurrency => _maxConcurrency;

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _ = RunAsync(_cts.Token);
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }

    private async Task RunAsync(CancellationToken ct)
    {
        await Task.Delay(2000, ct).ConfigureAwait(false);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await ProbeCooldownRecoveryAsync(ct).ConfigureAwait(false);

                // P2.4: concurrent probing capped at _maxConcurrency
                var tasks = _nodes.Select(node => ProbeWithGateAsync(node, ct));
                await Task.WhenAll(tasks).ConfigureAwait(false);
            }
            catch { /* ignore loop errors */ }

            await Task.Delay(_intervalMs, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// P2.4: Acquires the concurrency semaphore before probing, releases after.
    /// This caps concurrent HTTP HEAD requests across all nodes.
    /// </summary>
    private async Task ProbeWithGateAsync(NodeState node, CancellationToken ct)
    {
        await _concurrencyGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await ProbeOneAsync(node, ct).ConfigureAwait(false);
        }
        finally
        {
            _concurrencyGate.Release();
        }
    }

    /// <summary>
    /// P2.6: Probes all configured URLs for a single node.
    /// Averages the TTFB of all successful probes. Only records failure
    /// when ALL probe targets fail.
    ///
    /// P0#1: Nodes in RECOVERY_PROBING or STABILITY_VERIFICATION are routed through
    /// the RecoveryConfirmationFsm instead of normal success/failure recording.
    /// </summary>
    private async Task ProbeOneAsync(NodeState node, CancellationToken ct)
    {
        int port = _portResolver(node.Tag);
        var (_, client) = _pool.GetOrAdd(node.Tag, _ => CreateEntry(port));

        using var linkCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linkCts.CancelAfter(_probeTimeoutMs);

        // P1#7: Decide whether this probe is "heavy" (GET with body) to defeat
        // airport small-packet acceleration (§6.4). Heavy probes download the response
        // body, producing variable-size traffic that can't be easily prioritized.
        bool isHeavy = _heavyFraction > 0 && _rng.NextDouble() < _heavyFraction;

        var successfulTtfbs = new List<double>();
        FailureType lastFailure = FailureType.Timeout;

        foreach (var probeUrl in _probeUrls)
        {
            if (linkCts.IsCancellationRequested) break;

            long t0 = Stopwatch.GetTimestamp();
            try
            {
                var method = isHeavy ? HttpMethod.Get : HttpMethod.Head;
                var req = new HttpRequestMessage(method, probeUrl);
                using var resp = await client
                    .SendAsync(req, HttpCompletionOption.ResponseHeadersRead, linkCts.Token)
                    .ConfigureAwait(false);

                // TTFB measured at first response header (same for HEAD and GET)
                successfulTtfbs.Add(ElapsedMs(t0));

                // P1#7: For heavy probes, drain the response body to simulate real traffic.
                // We limit the drain to 64KB to bound overhead — the goal is defeating
                // small-packet classification, not measuring throughput.
                if (isHeavy && resp.Content.Headers.ContentLength is not (null or 0))
                {
                    try
                    {
                        using var drainCts = new CancellationTokenSource(2000); // max 2s for body
                        var buffer = new byte[8192];
                        int totalDrained = 0;
                        var stream = await resp.Content.ReadAsStreamAsync(drainCts.Token).ConfigureAwait(false);
                        while (totalDrained < 65536)
                        {
                            int read = await stream.ReadAsync(buffer, 0, buffer.Length, drainCts.Token).ConfigureAwait(false);
                            if (read == 0) break;
                            totalDrained += read;
                        }
                    }
                    catch
                    {
                        // Body drain failure is not a probe failure — TTFB was already captured
                    }
                }
            }
            catch (OperationCanceledException)
            {
                lastFailure = FailureType.Timeout;
            }
            catch (HttpRequestException ex)
            {
                lastFailure = ex.InnerException is SocketException { SocketErrorCode: SocketError.ConnectionRefused }
                    ? FailureType.Refused : FailureType.NetworkError;
            }
            catch
            {
                lastFailure = FailureType.NetworkError;
            }
        }

        bool probeSuccess = successfulTtfbs.Count > 0;

        // P0#1: Route recovery-state nodes through the RecoveryConfirmationFsm
        if (_recoveryFsm != null &&
            node.HealthState is NodeHealthState.RecoveryProbing or NodeHealthState.StabilityVerification)
        {
            var outcome = _recoveryFsm.OnProbeResult(node, probeSuccess, _nodes);

            if (probeSuccess && successfulTtfbs.Count > 0)
            {
                // Record the EWMA update even for recovery nodes
                double avgTtfbMs = successfulTtfbs.Average();
                _collector.RecordSuccess(node, avgTtfbMs);
            }
            else if (!probeSuccess && outcome.CooldownSeconds.HasValue)
            {
                // Failure during recovery → re-enter cooldown with backoff
                node.SetCooldown(DateTime.UtcNow.AddSeconds(outcome.CooldownSeconds.Value));
            }
            // Note: failure during recovery that doesn't set cooldown (DowngradeOnly)
            // still records as failure for EWMA purposes
            if (!probeSuccess && !outcome.CooldownSeconds.HasValue)
            {
                _collector.RecordFailure(node, lastFailure, _nodes);
            }
            return;
        }

        // Normal (non-recovery) path
        if (probeSuccess)
        {
            double avgTtfbMs = successfulTtfbs.Average();
            _collector.RecordSuccess(node, avgTtfbMs);
        }
        else
        {
            _collector.RecordFailure(node, lastFailure, _nodes);
        }
    }

    /// <summary>
    /// P0#1: Recovery state management.
    ///
    /// When RecoveryConfirmationFsm is available:
    ///   - FAILED nodes whose cooldown has expired → transition to RECOVERY_PROBING
    ///   - The regular ProbeOneAsync cycle handles RECOVERY_PROBING/STABILITY_VERIFICATION nodes
    ///
    /// When RecoveryConfirmationFsm is NOT available (legacy path):
    ///   - Probes nodes near cooldown expiry; on success → clear cooldown; on failure → extend
    /// </summary>
    private async Task ProbeCooldownRecoveryAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;

        foreach (var node in _nodes)
        {
            if (ct.IsCancellationRequested) break;

            // P0#1: Transition FAILED nodes whose cooldown expired → RECOVERY_PROBING
            if (_recoveryFsm != null && node.HealthState == NodeHealthState.Failed && !node.IsInCooldown)
            {
                _recoveryFsm.TransitionToRecoveryProbing(node);
                continue;
            }

            if (!node.IsInCooldown) continue;

            var remainingMs = (node.CooldownUntil - now).TotalMilliseconds;
            // Legacy path: skip nodes whose cooldown is far from expiring
            if (_recoveryFsm == null)
            {
                double windowMs = _intervalMs * 1.2;
                if (remainingMs > windowMs || remainingMs < 0) continue;
            }
            else
            {
                // P0#1: Skip nodes whose cooldown hasn't expired (they'll be handled later)
                if (remainingMs > 0) continue;
                // Cooldown expired and node is still FAILED → transition to RECOVERY_PROBING
                _recoveryFsm.TransitionToRecoveryProbing(node);
                continue;
            }

            // Legacy path: probe near-expiry cooldown nodes
            int port = _portResolver(node.Tag);
            var (_, client) = _pool.GetOrAdd(node.Tag, _ => CreateEntry(port));

            using var linkCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linkCts.CancelAfter(Math.Min(_probeTimeoutMs, 3000));

            bool anySuccess = false;
            foreach (var probeUrl in _probeUrls)
            {
                if (linkCts.IsCancellationRequested) break;

                long t0 = Stopwatch.GetTimestamp();
                try
                {
                    var req = new HttpRequestMessage(HttpMethod.Head, probeUrl);
                    using var resp = await client
                        .SendAsync(req, HttpCompletionOption.ResponseHeadersRead, linkCts.Token)
                        .ConfigureAwait(false);

                    double ttfbMs = ElapsedMs(t0);
                    _collector.RecordSuccess(node, ttfbMs);
                    node.ResetCooldown();
                    anySuccess = true;
                    break;
                }
                catch
                {
                    // continue to next probe URL
                }
            }

            if (!anySuccess)
            {
                _collector.RecordFailure(node, FailureType.Timeout, _nodes);
            }
        }
    }

    private (SocketsHttpHandler, HttpClient) CreateEntry(int port)
    {
        var handler = new SocketsHttpHandler
        {
            UseProxy = true,
            Proxy = new WebProxy($"socks5://127.0.0.1:{port}"),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
        };
        var client = new HttpClient(handler, disposeHandler: false)
        {
            Timeout = TimeSpan.FromMilliseconds(_probeTimeoutMs)
        };
        return (handler, client);
    }

    private static double ElapsedMs(long t0) =>
        (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        _concurrencyGate.Dispose();
        foreach (var (h, c) in _pool.Values) { c.Dispose(); h.Dispose(); }
        _pool.Clear();
    }
}
