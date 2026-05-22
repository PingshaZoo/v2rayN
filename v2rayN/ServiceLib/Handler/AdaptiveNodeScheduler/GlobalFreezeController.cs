namespace ServiceLib.Handler.AdaptiveNodeScheduler;

/// <summary>
/// v7.0 Global Instability Freeze controller (§11).
///
/// Prevents self-oscillation during external shocks (GFW波动, ISP抖动, DNS污染,
/// mass timeout events). Without a freeze, a shock triggers this cascade:
///   external shock → many nodes timeout simultaneously → all enter cooldown
///   → active-set shrinks drastically → remaining nodes overloaded
///   → more nodes timeout →恶性循环 → user loses connectivity entirely.
///
/// <h2>Freeze behavior (§11.3)</h2>
/// When freeze is active:
///   1. Freeze active-set mutation — no node ejection, no node addition
///   2. Freeze cooldown ejection — no new cooldowns
///   3. Suspend reload scheduling — all xray config reloads blocked
///   4. Keep last known active-set —维持冻结前的selector
///
/// Probe and telemetry continue during freeze (collect data, preserve scene).
///
/// <h2>Freeze hysteresis (§11.7)</h2>
/// After freeze ends, a 120s freeze_cooldown prevents re-triggering.
/// If massive anomaly recurs during cooldown → escalate to EmergencyDisable
/// (the control plane itself has become an instability source).
///
/// <h2>State machine (§10.7)</h2>
/// NORMAL → FREEZE_ACTIVE: >60% active nodes fail within trigger window
/// FREEZE_ACTIVE → NORMAL: freeze duration (60s) expired
/// NORMAL → FREEZE_COOLDOWN: freeze just ended, 120s cooldown
/// FREEZE_COOLDOWN → NORMAL: 120s expired, full capability restored
/// FREEZE_COOLDOWN → EMERGENCY_DISABLE: massive anomaly during cooldown
/// </summary>
public sealed class GlobalFreezeController
{
    private readonly IClock _clock;

    // ── configurable parameters (§11.6) ────────────────────
    public double TriggerRatio { get; init; } = 0.60;
    public TimeSpan TriggerWindow { get; init; } = TimeSpan.FromSeconds(15);
    public TimeSpan FreezeDuration { get; init; } = TimeSpan.FromSeconds(60);
    public TimeSpan FreezeCooldownDuration { get; init; } = TimeSpan.FromSeconds(120);

    // ── current state ──────────────────────────────────────
    private FreezeState _state = FreezeState.Normal;
    private DateTime _freezeStartedAt = DateTime.MinValue;
    private DateTime _freezeCooldownStartedAt = DateTime.MinValue;

    // ── per-node failure tracking ──────────────────────────
    private readonly Dictionary<string, Queue<DateTime>> _failureWindows = new(StringComparer.Ordinal);

    /// <summary>Raised when cooldown-period anomaly escalates to emergency disable.</summary>
    public event Action<string>? EmergencyDisableRequested;

    public GlobalFreezeController(IClock clock)
    {
        _clock = clock;
    }

    public FreezeState State => _state;
    public bool IsFrozen => _state == FreezeState.FreezeActive;
    public DateTime FreezeStartedAt => _freezeStartedAt;

    /// <summary>
    /// Records a probe failure for a node. Timestamps are used by <see cref="Evaluate"/>
    /// to compute the fraction of active-set nodes that failed within the trigger window.
    /// </summary>
    public void RecordFailure(string tag)
    {
        if (!_failureWindows.TryGetValue(tag, out var queue))
        {
            queue = new Queue<DateTime>();
            _failureWindows[tag] = queue;
        }
        queue.Enqueue(_clock.UtcNow);
    }

    /// <summary>
    /// Evaluates whether a freeze should trigger, and advances time-based state
    /// transitions (FREEZE_ACTIVE → NORMAL, FREEZE_COOLDOWN → NORMAL).
    ///
    /// Must be called BEFORE any active-set mutation or reload decision.
    /// Returns the decision the caller must follow.
    /// </summary>
    public FreezeDecision Evaluate(IReadOnlyList<string> activeSetTags)
    {
        var now = _clock.UtcNow;

        // ── advance time-based transitions ─────────────────
        switch (_state)
        {
            case FreezeState.FreezeActive:
                if (now - _freezeStartedAt >= FreezeDuration)
                {
                    // Freeze expired → enter cooldown, clear old failure windows
                    // so they don't trigger a false escalation during cooldown.
                    _failureWindows.Clear();
                    _state = FreezeState.FreezeCooldown;
                    _freezeCooldownStartedAt = now;
                    return new FreezeDecision(FreezeDecisionType.Unfreeze,
                        "freeze_duration_expired", null, null);
                }
                // Still frozen — block all mutations
                return new FreezeDecision(FreezeDecisionType.BlockMutation,
                    "global_freeze_active", null, null);

            case FreezeState.FreezeCooldown:
                if (now - _freezeCooldownStartedAt >= FreezeCooldownDuration)
                {
                    // Cooldown expired → back to normal
                    _state = FreezeState.Normal;
                    return new FreezeDecision(FreezeDecisionType.Allow,
                        "freeze_cooldown_expired", null, null);
                }
                // In cooldown — check for escalation
                if (ShouldTriggerFreeze(activeSetTags))
                {
                    var reason = "escalation: massive anomaly during freeze_cooldown";
                    EmergencyDisableRequested?.Invoke(reason);
                    return new FreezeDecision(FreezeDecisionType.EmergencyDisable,
                        reason, null, null);
                }
                // Cooldown active, no re-trigger — allow normal operation
                return new FreezeDecision(FreezeDecisionType.Allow,
                    "in_freeze_cooldown", null, null);

            case FreezeState.Normal:
                if (ShouldTriggerFreeze(activeSetTags))
                {
                    _state = FreezeState.FreezeActive;
                    _freezeStartedAt = now;
                    var frozenTags = new List<string>(activeSetTags);
                    return new FreezeDecision(FreezeDecisionType.TriggerFreeze,
                        ">60% active nodes failed within trigger window",
                        frozenTags, now);
                }
                return new FreezeDecision(FreezeDecisionType.Allow, null, null, null);
        }

        return new FreezeDecision(FreezeDecisionType.Allow, null, null, null);
    }

    /// <summary>
    /// Checks whether freeze trigger condition is met: > TriggerRatio active-set
    /// nodes have failed within TriggerWindow.
    /// </summary>
    private bool ShouldTriggerFreeze(IReadOnlyList<string> activeSetTags)
    {
        if (activeSetTags.Count == 0) return false;

        var now = _clock.UtcNow;
        int failedCount = 0;

        foreach (var tag in activeSetTags)
        {
            if (_failureWindows.TryGetValue(tag, out var queue))
            {
                // Purge expired entries
                while (queue.Count > 0 && (now - queue.Peek()) > TriggerWindow)
                    queue.Dequeue();

                if (queue.Count > 0)
                    failedCount++;
            }
        }

        double ratio = (double)failedCount / activeSetTags.Count;
        return ratio > TriggerRatio;
    }

    /// <summary>
    /// Clears all failure tracking state. Called on profile switch or system reset.
    /// </summary>
    public void Reset()
    {
        _state = FreezeState.Normal;
        _freezeStartedAt = DateTime.MinValue;
        _freezeCooldownStartedAt = DateTime.MinValue;
        _failureWindows.Clear();
    }

    /// <summary>
    /// Returns a snapshot of the freeze state for telemetry.
    /// </summary>
    public FreezeSnapshot GetSnapshot()
    {
        return new FreezeSnapshot(_state, _freezeStartedAt, _freezeCooldownStartedAt);
    }
}

public enum FreezeState
{
    Normal = 0,
    FreezeActive = 1,
    FreezeCooldown = 2,
}

public enum FreezeDecisionType
{
    Allow,
    TriggerFreeze,
    BlockMutation,
    Unfreeze,
    EmergencyDisable,
}

/// <summary>
/// Result of <see cref="GlobalFreezeController.Evaluate"/>.
/// Callers MUST obey the decision type before performing any active-set mutation
/// or scheduling a reload.
/// </summary>
public sealed record FreezeDecision(
    FreezeDecisionType Type,
    string? Reason,
    IReadOnlyList<string>? FrozenActiveTags,
    DateTime? FreezeStartedAt);

public sealed record FreezeSnapshot(FreezeState State, DateTime FreezeStartedAt, DateTime FreezeCooldownStartedAt);
