namespace ServiceLib.Handler.AdaptiveNodeScheduler;

public enum ProxyProtocol { Tcp, Udp }

/// <summary>
/// v7.5 P2: Traffic exposure tier. Orthogonal to HealthState —
/// a node with HealthState=Active can be Standby if the Production Pool is full.
/// </summary>
public enum TrafficTier { Production, Standby }

/// <summary>
/// v7.0 Recovery Confirmation FSM states (§10.1).
///
/// Health state is distinct from cooldown: cooldown is a time-based ejection
/// mechanism; HealthState is the semantic stage in the node's lifecycle.
/// </summary>
public enum NodeHealthState
{
    /// <summary>Node is healthy and eligible for the production selector.</summary>
    Active = 0,

    /// <summary>Node has been ejected due to consecutive failures. In cooldown.</summary>
    Failed = 1,

    /// <summary>Cooldown expired; probing to confirm basic reachability (needs 3 consecutive successes).</summary>
    RecoveryProbing = 2,

    /// <summary>3 consecutive probe successes achieved; stability gate (N minutes, default 5).
    /// Node only receives probe traffic — NOT in production selector.</summary>
    StabilityVerification = 3,
}

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

    // ── recovery FSM state (§10.1) ─────────────────────────────
    private NodeHealthState _healthState = NodeHealthState.Active;

    // ── traffic tier (P2: orthogonal to HealthState) ───────────
    private TrafficTier _trafficTier = TrafficTier.Standby;
    private int _recoveryProbeSuccessCount;
    private DateTime _stabilityVerificationStartedAt = DateTime.MinValue;
    private int _cooldownBackoffLevel; // n for exponential backoff: 30s * 2^n

    // ── DNS cache state (§13.4) ────────────────────────────────
    private string? _cachedIp;
    private DateTime _dnsLastResolved = DateTime.MinValue;
    private int _dnsCacheConfidence;
    private int _dnsConsecutiveCacheFailures;

    // Readonly properties — double reads are atomic on x64, no lock needed
    public double Score => _score;
    public double EwmaLatencyMs => _ewmaLatencyMs;
    public double EwmaLossRate => _ewmaLossRate;
    public DateTime LastObserved => _lastObserved;
    public int ConsecutiveFailures => _consecutiveFailures;
    public bool IsInCooldown => DateTime.UtcNow < _cooldownUntil;
    public DateTime CooldownUntil => _cooldownUntil;

    public NodeHealthState HealthState => _healthState;
    public int RecoveryProbeSuccessCount => _recoveryProbeSuccessCount;
    public int CooldownBackoffLevel => _cooldownBackoffLevel;
    public DateTime StabilityVerificationStartedAt => _stabilityVerificationStartedAt;
    public TrafficTier TrafficTier => _trafficTier;

    public string? CachedIp => _cachedIp;
    public DateTime DnsLastResolved => _dnsLastResolved;
    public int DnsCacheConfidence => _dnsCacheConfidence;
    public int DnsConsecutiveCacheFailures => _dnsConsecutiveCacheFailures;

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

    // ── recovery FSM state transitions ─────────────────────────

    public void SetHealthState(NodeHealthState newState)
    {
        lock (_lock) { _healthState = newState; }
    }

    public void IncrementRecoveryProbeSuccess()
    {
        lock (_lock) { _recoveryProbeSuccessCount++; }
    }

    public void ResetRecoveryProbeSuccess()
    {
        lock (_lock) { _recoveryProbeSuccessCount = 0; }
    }

    public void MarkStabilityVerificationStarted(DateTime now)
    {
        lock (_lock) { _stabilityVerificationStartedAt = now; }
    }

    public void IncrementBackoffLevel()
    {
        lock (_lock) { _cooldownBackoffLevel++; }
    }

    public void ResetBackoffLevel()
    {
        lock (_lock) { _cooldownBackoffLevel = 0; }
    }

    public void ResetHealthFsm()
    {
        lock (_lock)
        {
            _healthState = NodeHealthState.Active;
            _recoveryProbeSuccessCount = 0;
            _stabilityVerificationStartedAt = DateTime.MinValue;
            _cooldownBackoffLevel = 0;
        }
    }

    // ── traffic tier (P2) ──────────────────────────────────────

    public void SetTrafficTier(TrafficTier tier)
    {
        lock (_lock) { _trafficTier = tier; }
    }

    // ── DNS cache management (§13.4) ───────────────────────────

    public void SetCachedIp(string ip, DateTime resolvedAt)
    {
        lock (_lock)
        {
            _cachedIp = ip;
            _dnsLastResolved = resolvedAt;
            _dnsCacheConfidence = 1;
            _dnsConsecutiveCacheFailures = 0;
        }
    }

    public void OnDnsCacheHit()
    {
        lock (_lock)
        {
            _dnsCacheConfidence++;
            _dnsConsecutiveCacheFailures = 0;
        }
    }

    /// <summary>Returns true if the cache should be invalidated (N consecutive failures).</summary>
    public bool OnDnsCacheMiss(int invalidateAfterConsecutiveFailures = 3)
    {
        lock (_lock)
        {
            _dnsConsecutiveCacheFailures++;
            return _dnsConsecutiveCacheFailures >= invalidateAfterConsecutiveFailures;
        }
    }

    public void InvalidateDnsCache()
    {
        lock (_lock)
        {
            _cachedIp = null;
            _dnsLastResolved = DateTime.MinValue;
            _dnsCacheConfidence = 0;
            _dnsConsecutiveCacheFailures = 0;
        }
    }

    /// <summary>Returns true if the DNS cache TTL (default 300s) has expired.</summary>
    public bool IsDnsCacheExpired(int ttlSeconds = 300, DateTime? now = null)
    {
        if (_cachedIp == null || _dnsLastResolved == DateTime.MinValue)
            return true;
        var effectiveNow = now ?? DateTime.UtcNow;
        return (effectiveNow - _dnsLastResolved).TotalSeconds >= ttlSeconds;
    }

    public NodeSnapshot Snapshot()
    {
        lock (_lock)
        {
            return new NodeSnapshot(Tag, _score, _ewmaLatencyMs,
                                    _ewmaLossRate, IsInCooldown, _cooldownUntil,
                                    _healthState, _trafficTier);
        }
    }
}

public record NodeSnapshot(string Tag, double Score, double LatencyMs,
                           double LossRate, bool InCooldown, DateTime CooldownUntil,
                           NodeHealthState HealthState = NodeHealthState.Active,
                           TrafficTier TrafficTier = TrafficTier.Standby);
