namespace ServiceLib.Handler.AdaptiveNodeScheduler;

public enum ProxyProtocol { Tcp, Udp }

public sealed class NodeState
{
    // ── identity (readonly, never changes after init) ──────────
    public string Tag { get; init; }
    public string Host { get; init; }
    public int Port { get; init; }
    public ProxyProtocol Protocol { get; init; }
    public string ChildIndexId { get; init; }

    // ── scoring state (protected by _lock) ─────────────────────
    private readonly object _lock = new();

    private double _score = 50.0;
    private double _ewmaLatencyMs = 500.0;
    private double _ewmaLossRate = 0.10;
    private DateTime _lastObserved = DateTime.MinValue;
    private int _consecutiveFailures;
    private DateTime _cooldownUntil = DateTime.MinValue;

    // Readonly properties — double reads are atomic on x64, no lock needed
    public double Score => _score;
    public double EwmaLatencyMs => _ewmaLatencyMs;
    public double EwmaLossRate => _ewmaLossRate;
    public DateTime LastObserved => _lastObserved;
    public int ConsecutiveFailures => _consecutiveFailures;
    public bool IsInCooldown => DateTime.UtcNow < _cooldownUntil;
    public DateTime CooldownUntil => _cooldownUntil;

    // Batch update — enter lock once, reduce contention
    public void UpdateScore(double latencyMs, double lossRate,
                            double score, int consecutiveFailures)
    {
        lock (_lock)
        {
            _ewmaLatencyMs = latencyMs;
            _ewmaLossRate = lossRate;
            _score = score;
            _consecutiveFailures = consecutiveFailures;
            _lastObserved = DateTime.UtcNow;
        }
    }

    public void SetCooldown(DateTime until)
    {
        lock (_lock) { _cooldownUntil = until; }
    }

    public void ResetCooldown()
    {
        lock (_lock) { _cooldownUntil = DateTime.MinValue; }
    }

    public NodeSnapshot Snapshot()
    {
        lock (_lock)
        {
            return new NodeSnapshot(Tag, _score, _ewmaLatencyMs,
                                    _ewmaLossRate, IsInCooldown, _cooldownUntil);
        }
    }
}

public record NodeSnapshot(string Tag, double Score, double LatencyMs,
                           double LossRate, bool InCooldown, DateTime CooldownUntil);
