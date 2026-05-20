using System.Net.Sockets;

namespace ServiceLib.Handler.AdaptiveNodeScheduler;

/// <summary>
/// Active TTFB probe service. Probes each node through its dedicated xray SOCKS5 inbound port
/// to measure latency without going through the balancer. Runs on a configurable interval.
/// </summary>
public sealed class ProbeService : IDisposable
{
    private readonly string _probeUrl;
    private readonly int _probeTimeoutMs;
    private readonly int _intervalMs;
    private bool _disposed;
    private CancellationTokenSource? _cts;

    private readonly IReadOnlyList<NodeState> _nodes;
    private readonly Func<string, int> _portResolver; // node tag → local probe port
    private readonly FailureCollector _collector;
    private readonly ConcurrentDictionary<string, (SocketsHttpHandler handler, HttpClient client)> _pool = new();

    private const int MinTimeoutMs = 500;
    private const int DefaultTimeoutMs = 5000;
    private const int MinIntervalSec = 5;
    private const int DefaultIntervalSec = 30;
    private const string DefaultProbeUrl = "http://cp.cloudflare.com/";

    public ProbeService(IReadOnlyList<NodeState> nodes,
                        Func<string, int> portResolver,
                        FailureCollector collector,
                        AdaptiveSchedulerItem config)
    {
        _nodes = nodes;
        _portResolver = portResolver;
        _collector = collector;
        _probeUrl = string.IsNullOrWhiteSpace(config.ProbeUrl)
            ? DefaultProbeUrl : config.ProbeUrl;
        _probeTimeoutMs = config.ProbeTimeoutMs < MinTimeoutMs
            ? DefaultTimeoutMs : config.ProbeTimeoutMs;
        var intervalSec = config.ProbeIntervalSec < MinIntervalSec
            ? DefaultIntervalSec : config.ProbeIntervalSec;
        _intervalMs = intervalSec * 1000;
    }

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
        // Initial delay to let xray-core start
        await Task.Delay(2000, ct).ConfigureAwait(false);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                foreach (var node in _nodes)
                {
                    if (ct.IsCancellationRequested) break;
                    await ProbeOneAsync(node, ct).ConfigureAwait(false);
                }
            }
            catch { /* ignore loop errors */ }

            await Task.Delay(_intervalMs, ct).ConfigureAwait(false);
        }
    }

    private async Task ProbeOneAsync(NodeState node, CancellationToken ct)
    {
        int port = _portResolver(node.Tag);
        var (_, client) = _pool.GetOrAdd(node.Tag, _ => CreateEntry(port));

        using var linkCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linkCts.CancelAfter(_probeTimeoutMs);

        long t0 = Stopwatch.GetTimestamp();
        try
        {
            var req = new HttpRequestMessage(HttpMethod.Head, _probeUrl);
            using var resp = await client
                .SendAsync(req, HttpCompletionOption.ResponseHeadersRead, linkCts.Token)
                .ConfigureAwait(false);

            double ttfbMs = ElapsedMs(t0);
            _collector.RecordSuccess(node, ttfbMs);
        }
        catch (OperationCanceledException)
        {
            _collector.RecordFailure(node, FailureType.Timeout, _nodes);
        }
        catch (HttpRequestException ex)
        {
            var ft = ex.InnerException is SocketException { SocketErrorCode: SocketError.ConnectionRefused }
                ? FailureType.Refused : FailureType.NetworkError;
            _collector.RecordFailure(node, ft, _nodes);
        }
        catch
        {
            _collector.RecordFailure(node, FailureType.NetworkError, _nodes);
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
        foreach (var (h, c) in _pool.Values) { c.Dispose(); h.Dispose(); }
        _pool.Clear();
    }
}
