namespace ServiceLib.Models.Entities;

[Serializable]
public class ProfileExItem
{
    [PrimaryKey]
    public string IndexId { get; set; }

    public int Delay { get; set; }
    public decimal Speed { get; set; }
    public int Sort { get; set; }
    public string? Message { get; set; }

    /// <summary>Adaptive QoS score [0,100], round-trip from NodeState.Score.</summary>
    public int AdaptiveScore { get; set; }
    /// <summary>EWMA latency (ms) from adaptive probing.</summary>
    public int AdaptiveLatency { get; set; }
    /// <summary>Non-zero when node is in cooldown (excluded from balancer).</summary>
    public int AdaptiveCooldown { get; set; }
    /// <summary>Non-zero when node is in the active set.</summary>
    public int AdaptiveActive { get; set; }
    /// <summary>UTC timestamp when the adaptive score was last persisted. Used for staleness detection (>4h → reset).</summary>
    public DateTime AdaptiveLastObserved { get; set; }

    // ── P0#1: Recovery Confirmation FSM state persistence (§10.1.4) ──

    /// <summary>NodeHealthState enum value. Persisted so the recovery FSM survives restarts.</summary>
    public int AdaptiveHealthState { get; set; }

    /// <summary>Consecutive successful recovery probes (0-3 for RECOVERY_PROBING stage).</summary>
    public int AdaptiveRecoveryProbeSuccess { get; set; }

    /// <summary>Exponential backoff level for cooldown duration (0-6, capped at 30min).</summary>
    public int AdaptiveBackoffLevel { get; set; }

    /// <summary>UTC timestamp when STABILITY_VERIFICATION started. MinValue = not in verification.</summary>
    public DateTime AdaptiveStabilityVerificationStart { get; set; }
}
