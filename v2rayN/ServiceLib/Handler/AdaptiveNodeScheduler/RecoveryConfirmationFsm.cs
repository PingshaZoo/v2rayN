namespace ServiceLib.Handler.AdaptiveNodeScheduler;

/// <summary>
/// v7.0 Recovery Confirmation FSM (§10.1, §10.7).
///
/// Replaces the old two-stage "COOLDOWN → ACTIVE" with a four-stage recovery pipeline:
///   ACTIVE → FAILED → RECOVERY_PROBING → STABILITY_VERIFICATION → ACTIVE
///
/// <h2>Why this matters</h2>
/// Without recovery confirmation, a node coming out of cooldown is immediately
/// re-admitted to the production selector. If the node is still degraded, it gets
/// ejected again — creating a cooldown/re-eject oscillation that causes unnecessary
/// reloads and user-visible connection resets.
///
/// The four-stage pipeline ensures a node proves basic reachability (3 consecutive
/// probe successes) AND sustained stability (N minutes of clean probes) before
/// re-entering production traffic.
///
/// <h2>Exponential backoff</h2>
/// Each time a node fails during RECOVERY_PROBING or STABILITY_VERIFICATION, the
/// cooldown duration doubles: 30s * 2^n, capped at 30 minutes. This prevents
/// permanently freezing a node (which would violate "用户极少手动干预").
///
/// <h2>State persistence</h2>
/// HealthState + backoff level + verification start time are persisted to
/// ProfileExItem so the FSM survives process restarts (§10.1.4).
/// </summary>
public sealed class RecoveryConfirmationFsm
{
    private const double CooldownBaseSeconds = 30.0;
    private const double MaxCooldownSeconds = 1800.0; // 30 min cap
    private const int RequiredRecoverySuccesses = 3;
    private readonly int _stabilityVerificationMinutes;
    private readonly IClock _clock;

    public RecoveryConfirmationFsm(IClock clock, int stabilityVerificationMinutes = 5)
    {
        _clock = clock;
        _stabilityVerificationMinutes = stabilityVerificationMinutes;
    }

    /// <summary>
    /// Transitions a node from ACTIVE to FAILED. Called when the node enters cooldown
    /// after 2+ consecutive failures. Resets any in-progress recovery state.
    /// Returns false if the transition is illegal (e.g., already in FAILED).
    /// </summary>
    public bool TransitionToFailed(NodeState node, int cooldownMaxAllowed, int currentCooldownCount)
    {
        if (!IsLegalTransition(node.HealthState, NodeHealthState.Failed))
            return false;

        if (currentCooldownCount >= cooldownMaxAllowed)
            return false; // cooldown budget exhausted — caller should downgrade instead

        node.SetHealthState(NodeHealthState.Failed);
        node.ResetRecoveryProbeSuccess();
        return true;
    }

    /// <summary>
    /// Transitions from FAILED to RECOVERY_PROBING. Called when cooldown expires.
    /// This is the entry point into the recovery pipeline.
    /// Returns false if the node is not in FAILED state.
    /// </summary>
    public bool TransitionToRecoveryProbing(NodeState node)
    {
        if (node.HealthState != NodeHealthState.Failed)
            return false;

        node.SetHealthState(NodeHealthState.RecoveryProbing);
        node.ResetRecoveryProbeSuccess();
        return true;
    }

    /// <summary>
    /// Processes a probe result for a node in RECOVERY_PROBING or STABILITY_VERIFICATION.
    ///
    /// RECOVERY_PROBING + success: increments counter; after 3 consecutive successes →
    ///   transition to STABILITY_VERIFICATION.
    /// RECOVERY_PROBING + failure: returns to FAILED with exponential backoff.
    /// STABILITY_VERIFICATION + success: no state change (timer-based promotion).
    /// STABILITY_VERIFICATION + failure: returns to FAILED, resets verification progress.
    ///
    /// Returns the cooldown duration if the node should re-enter cooldown (failure case),
    /// or null if no cooldown is needed.
    /// </summary>
    public RecoveryProbeOutcome OnProbeResult(NodeState node, bool success,
                                               IReadOnlyList<NodeState> allNodes)
    {
        var state = node.HealthState;

        if (state == NodeHealthState.RecoveryProbing)
        {
            if (success)
            {
                node.IncrementRecoveryProbeSuccess();
                if (node.RecoveryProbeSuccessCount >= RequiredRecoverySuccesses)
                {
                    // Transition to STABILITY_VERIFICATION
                    node.SetHealthState(NodeHealthState.StabilityVerification);
                    node.MarkStabilityVerificationStarted(_clock.UtcNow);
                    node.ResetRecoveryProbeSuccess();
                    return new RecoveryProbeOutcome(RecoveryAction.EnterStabilityVerification, null);
                }
                return new RecoveryProbeOutcome(RecoveryAction.StayInRecoveryProbing, null);
            }
            else
            {
                // Failure: back to FAILED with exponential backoff
                return BackToFailed(node, allNodes);
            }
        }

        if (state == NodeHealthState.StabilityVerification)
        {
            if (success)
            {
                // Check if verification period has elapsed
                if (ShouldPromoteToActive(node))
                {
                    node.ResetHealthFsm();
                    return new RecoveryProbeOutcome(RecoveryAction.PromoteToActive, null);
                }
                return new RecoveryProbeOutcome(RecoveryAction.StayInStabilityVerification, null);
            }
            else
            {
                // Probe failure → back to FAILED, reset verification progress
                node.SetHealthState(NodeHealthState.Failed);
                node.ResetRecoveryProbeSuccess();
                node.IncrementBackoffLevel();
                var cooldownDuration = ComputeCooldownDuration(node.CooldownBackoffLevel);
                return new RecoveryProbeOutcome(RecoveryAction.EnterFailed, cooldownDuration);
            }
        }

        // Not in a recovery state — caller should not have invoked this
        return new RecoveryProbeOutcome(RecoveryAction.NoOp, null);
    }

    /// <summary>
    /// Checks whether a STABILITY_VERIFICATION node has completed its verification
    /// period and should be promoted to ACTIVE. Called both on probe success and
    /// from the periodic monitor loop (to catch nodes whose verification timer
    /// expires between probe cycles).
    /// </summary>
    public bool ShouldPromoteToActive(NodeState node)
    {
        if (node.HealthState != NodeHealthState.StabilityVerification)
            return false;

        var elapsed = _clock.UtcNow - node.StabilityVerificationStartedAt;
        return elapsed >= TimeSpan.FromMinutes(_stabilityVerificationMinutes);
    }

    /// <summary>
    /// Re-enters FAILED state with exponential backoff. Called when a node in
    /// RECOVERY_PROBING or STABILITY_VERIFICATION fails a probe.
    /// </summary>
    private RecoveryProbeOutcome BackToFailed(NodeState node, IReadOnlyList<NodeState> allNodes)
    {
        node.SetHealthState(NodeHealthState.Failed);
        node.ResetRecoveryProbeSuccess();
        node.IncrementBackoffLevel();

        int cooldownCount = allNodes.Count(n => n.IsInCooldown);
        int maxAllowed = CooldownFsm.ComputeMaxCooldown(allNodes.Count);

        if (cooldownCount >= maxAllowed)
        {
            // cooldown budget exhausted — just downgrade score, don't set cooldown timer
            return new RecoveryProbeOutcome(RecoveryAction.DowngradeOnly, null);
        }

        var cooldownDuration = ComputeCooldownDuration(node.CooldownBackoffLevel);
        return new RecoveryProbeOutcome(RecoveryAction.EnterFailed, cooldownDuration);
    }

    /// <summary>
    /// Exponential backoff: 30s * 2^n, capped at 30 minutes.
    /// Level 0 → 30s, 1 → 60s, 2 → 120s, 3 → 240s, 4 → 480s, 5 → 960s, 6 → 1800s (cap).
    /// </summary>
    public static double ComputeCooldownDuration(int backoffLevel)
    {
        double seconds = CooldownBaseSeconds * Math.Pow(2, backoffLevel);
        return Math.Min(seconds, MaxCooldownSeconds);
    }

    /// <summary>
    /// Validates state transitions per §10.7 transition invariants.
    /// Returns true only for the six legal transitions defined in the design doc.
    /// </summary>
    public static bool IsLegalTransition(NodeHealthState from, NodeHealthState to)
    {
        return (from, to) switch
        {
            // Legal transitions (§10.7)
            (NodeHealthState.Active, NodeHealthState.Failed) => true,
            (NodeHealthState.Failed, NodeHealthState.RecoveryProbing) => true,
            (NodeHealthState.RecoveryProbing, NodeHealthState.StabilityVerification) => true,
            (NodeHealthState.RecoveryProbing, NodeHealthState.Failed) => true,
            (NodeHealthState.StabilityVerification, NodeHealthState.Active) => true,
            (NodeHealthState.StabilityVerification, NodeHealthState.Failed) => true,

            // Identity (no-op, allowed for idempotency)
            (NodeHealthState.Active, NodeHealthState.Active) => true,
            (NodeHealthState.Failed, NodeHealthState.Failed) => true,
            (NodeHealthState.RecoveryProbing, NodeHealthState.RecoveryProbing) => true,
            (NodeHealthState.StabilityVerification, NodeHealthState.StabilityVerification) => true,

            // Everything else is illegal
            _ => false,
        };
    }
}

/// <summary>
/// Result of processing a probe result through the recovery FSM.
/// </summary>
public enum RecoveryAction
{
    NoOp,
    StayInRecoveryProbing,
    EnterStabilityVerification,
    StayInStabilityVerification,
    PromoteToActive,
    EnterFailed,
    DowngradeOnly,
}

public sealed record RecoveryProbeOutcome(RecoveryAction Action, double? CooldownSeconds);
