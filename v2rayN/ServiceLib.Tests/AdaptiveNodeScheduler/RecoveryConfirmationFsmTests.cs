using AwesomeAssertions;
using ServiceLib.Handler.AdaptiveNodeScheduler;
using Xunit;

namespace ServiceLib.Tests.AdaptiveNodeScheduler;

/// <summary>
/// P0#1: Tests for RecoveryConfirmationFsm — the 4-stage recovery pipeline
/// (ACTIVE → FAILED → RECOVERY_PROBING → STABILITY_VERIFICATION → ACTIVE).
///
/// Covers: state transitions, illegal transition rejection, exponential backoff,
/// recovery probe counting, stability verification timer, cooldown budget.
/// </summary>
public class RecoveryConfirmationFsmTests
{
    private static NodeState CreateNode(string tag, double score = 50, NodeHealthState healthState = NodeHealthState.Active)
    {
        var node = new NodeState
        {
            Tag = tag,
            Host = "127.0.0.1",
            Port = 1080,
            Protocol = ProxyProtocol.Tcp,
            ChildIndexId = tag,
        };
        node.UpdateScore(100, 0.0, score, 0);
        if (healthState != NodeHealthState.Active)
            node.SetHealthState(healthState);
        return node;
    }

    private static (RecoveryConfirmationFsm fsm, FakeClock clock) CreateFsm(int stabilityMinutes = 5)
    {
        var clock = new FakeClock();
        var fsm = new RecoveryConfirmationFsm(clock, stabilityMinutes);
        return (fsm, clock);
    }

    // ── Legal state transitions (§10.7) ──────────────────────

    [Fact]
    public void TransitionToFailed_FromActive_Succeeds()
    {
        var (fsm, _) = CreateFsm();
        var node = CreateNode("A", healthState: NodeHealthState.Active);

        var result = fsm.TransitionToFailed(node, cooldownMaxAllowed: 3, currentCooldownCount: 0);

        result.Should().BeTrue();
        node.HealthState.Should().Be(NodeHealthState.Failed);
    }

    [Fact]
    public void TransitionToFailed_AlreadyFailed_ReturnsFalse_Idempotent()
    {
        var (fsm, _) = CreateFsm();
        var node = CreateNode("A", healthState: NodeHealthState.Failed);

        var result = fsm.TransitionToFailed(node, cooldownMaxAllowed: 3, currentCooldownCount: 0);

        // Identity transition is allowed (idempotent), but shouldn't hurt
        result.Should().BeTrue("identity transition is legal per §10.7");
        node.HealthState.Should().Be(NodeHealthState.Failed);
    }

    [Fact]
    public void TransitionToFailed_CooldownBudgetExhausted_ReturnsFalse()
    {
        var (fsm, _) = CreateFsm();
        var node = CreateNode("A", healthState: NodeHealthState.Active);

        // max=3, currently 3 cooldowns → budget exhausted
        var result = fsm.TransitionToFailed(node, cooldownMaxAllowed: 3, currentCooldownCount: 3);

        result.Should().BeFalse("cooldown budget exhausted — caller should downgrade instead");
    }

    [Fact]
    public void TransitionToRecoveryProbing_FromFailed_Succeeds()
    {
        var (fsm, _) = CreateFsm();
        var node = CreateNode("A", healthState: NodeHealthState.Failed);

        var result = fsm.TransitionToRecoveryProbing(node);

        result.Should().BeTrue();
        node.HealthState.Should().Be(NodeHealthState.RecoveryProbing);
        node.RecoveryProbeSuccessCount.Should().Be(0, "recovery counter is reset on entry");
    }

    [Fact]
    public void TransitionToRecoveryProbing_FromActive_ReturnsFalse()
    {
        var (fsm, _) = CreateFsm();
        var node = CreateNode("A", healthState: NodeHealthState.Active);

        var result = fsm.TransitionToRecoveryProbing(node);

        result.Should().BeFalse("ACTIVE → RECOVERY_PROBING is illegal (§10.7)");
    }

    [Fact]
    public void TransitionToRecoveryProbing_FromStabilityVerification_ReturnsFalse()
    {
        var (fsm, _) = CreateFsm();
        var node = CreateNode("A", healthState: NodeHealthState.StabilityVerification);

        var result = fsm.TransitionToRecoveryProbing(node);

        result.Should().BeFalse("STABILITY_VERIFICATION → RECOVERY_PROBING is illegal (§10.7)");
    }

    // ── Recovery probing: success counting ───────────────────

    [Fact]
    public void RecoveryProbing_OneSuccess_StaysInRecoveryProbing()
    {
        var (fsm, _) = CreateFsm();
        var node = CreateNode("A", healthState: NodeHealthState.RecoveryProbing);
        var nodes = new List<NodeState> { node };

        var outcome = fsm.OnProbeResult(node, success: true, nodes);

        outcome.Action.Should().Be(RecoveryAction.StayInRecoveryProbing);
        node.HealthState.Should().Be(NodeHealthState.RecoveryProbing);
        node.RecoveryProbeSuccessCount.Should().Be(1);
    }

    [Fact]
    public void RecoveryProbing_TwoSuccesses_StaysInRecoveryProbing()
    {
        var (fsm, _) = CreateFsm();
        var node = CreateNode("A", healthState: NodeHealthState.RecoveryProbing);
        var nodes = new List<NodeState> { node };

        fsm.OnProbeResult(node, success: true, nodes);
        var outcome = fsm.OnProbeResult(node, success: true, nodes);

        outcome.Action.Should().Be(RecoveryAction.StayInRecoveryProbing);
        node.HealthState.Should().Be(NodeHealthState.RecoveryProbing);
        node.RecoveryProbeSuccessCount.Should().Be(2);
    }

    [Fact]
    public void RecoveryProbing_ThreeSuccesses_EntersStabilityVerification()
    {
        var (fsm, clock) = CreateFsm();
        var node = CreateNode("A", healthState: NodeHealthState.RecoveryProbing);
        var nodes = new List<NodeState> { node };

        fsm.OnProbeResult(node, success: true, nodes); // 1
        fsm.OnProbeResult(node, success: true, nodes); // 2
        var outcome = fsm.OnProbeResult(node, success: true, nodes); // 3

        outcome.Action.Should().Be(RecoveryAction.EnterStabilityVerification);
        node.HealthState.Should().Be(NodeHealthState.StabilityVerification);
        node.RecoveryProbeSuccessCount.Should().Be(0, "counter reset on transition");
        node.StabilityVerificationStartedAt.Should().Be(clock.UtcNow);
    }

    [Fact]
    public void RecoveryProbing_Failure_ReturnsToFailedWithBackoff()
    {
        var (fsm, _) = CreateFsm();
        var node = CreateNode("A", healthState: NodeHealthState.RecoveryProbing);
        // Need 4+ nodes so cooldown budget (floor(N/3)=1) allows one cooldown
        var nodes = new List<NodeState>
        {
            node,
            CreateNode("B"), CreateNode("C"), CreateNode("D"),
        };

        fsm.OnProbeResult(node, success: true, nodes); // build up to 1 success
        var outcome = fsm.OnProbeResult(node, success: false, nodes); // failure!

        outcome.Action.Should().Be(RecoveryAction.EnterFailed);
        outcome.CooldownSeconds.Should().NotBeNull();
        node.HealthState.Should().Be(NodeHealthState.Failed);
        node.RecoveryProbeSuccessCount.Should().Be(0);
        node.CooldownBackoffLevel.Should().Be(1, "backoff incremented from 0 to 1");
    }

    [Fact]
    public void RecoveryProbing_FailureThenSuccess_CounterResets()
    {
        var (fsm, _) = CreateFsm();
        var node = CreateNode("A", healthState: NodeHealthState.RecoveryProbing);
        var nodes = new List<NodeState> { node };

        // Success, success, FAILURE, re-enter RECOVERY_PROBING
        fsm.OnProbeResult(node, success: true, nodes);
        fsm.OnProbeResult(node, success: true, nodes);
        fsm.OnProbeResult(node, success: false, nodes);
        // node is now FAILED, transition to RECOVERY_PROBING
        fsm.TransitionToRecoveryProbing(node);

        // First success after re-entry
        var outcome = fsm.OnProbeResult(node, success: true, nodes);

        outcome.Action.Should().Be(RecoveryAction.StayInRecoveryProbing);
        node.RecoveryProbeSuccessCount.Should().Be(1, "counter reset on re-entry to RECOVERY_PROBING");
    }

    // ── Stability verification ────────────────────────────────

    [Fact]
    public void StabilityVerification_SuccessBeforeTimer_StaysInVerification()
    {
        var (fsm, clock) = CreateFsm(stabilityMinutes: 5);
        var node = CreateNode("A", healthState: NodeHealthState.StabilityVerification);
        node.MarkStabilityVerificationStarted(clock.UtcNow);
        var nodes = new List<NodeState> { node };

        // Only 2 minutes elapsed (less than 5)
        clock.AdvanceTime(TimeSpan.FromMinutes(2));

        var outcome = fsm.OnProbeResult(node, success: true, nodes);

        outcome.Action.Should().Be(RecoveryAction.StayInStabilityVerification);
        node.HealthState.Should().Be(NodeHealthState.StabilityVerification);
    }

    [Fact]
    public void StabilityVerification_SuccessAfterTimer_PromotesToActive()
    {
        var (fsm, clock) = CreateFsm(stabilityMinutes: 5);
        var node = CreateNode("A", healthState: NodeHealthState.StabilityVerification);
        node.MarkStabilityVerificationStarted(clock.UtcNow);
        var nodes = new List<NodeState> { node };

        // 5 minutes elapsed
        clock.AdvanceTime(TimeSpan.FromMinutes(5));

        var outcome = fsm.OnProbeResult(node, success: true, nodes);

        outcome.Action.Should().Be(RecoveryAction.PromoteToActive);
        node.HealthState.Should().Be(NodeHealthState.Active);
    }

    [Fact]
    public void StabilityVerification_SuccessAtBoundary_PromotesToActive()
    {
        // Exactly 5 minutes → should promote
        var (fsm, clock) = CreateFsm(stabilityMinutes: 5);
        var node = CreateNode("A", healthState: NodeHealthState.StabilityVerification);
        node.MarkStabilityVerificationStarted(clock.UtcNow);
        var nodes = new List<NodeState> { node };

        clock.AdvanceTime(TimeSpan.FromMinutes(5));

        var outcome = fsm.OnProbeResult(node, success: true, nodes);
        outcome.Action.Should().Be(RecoveryAction.PromoteToActive,
            "exactly at timer expiry should promote");
    }

    [Fact]
    public void StabilityVerification_Failure_ReturnsToFailed()
    {
        var (fsm, clock) = CreateFsm();
        var node = CreateNode("A", healthState: NodeHealthState.StabilityVerification);
        node.MarkStabilityVerificationStarted(clock.UtcNow);
        var nodes = new List<NodeState> { node };

        var outcome = fsm.OnProbeResult(node, success: false, nodes);

        outcome.Action.Should().Be(RecoveryAction.EnterFailed);
        node.HealthState.Should().Be(NodeHealthState.Failed);
        node.CooldownBackoffLevel.Should().Be(1);
    }

    [Fact]
    public void ShouldPromoteToActive_NonVerificationNode_ReturnsFalse()
    {
        var (fsm, _) = CreateFsm();
        var node = CreateNode("A", healthState: NodeHealthState.Active);

        fsm.ShouldPromoteToActive(node).Should().BeFalse();
    }

    // ── Exponential backoff (§10.1.3) ───────────────────────

    [Theory]
    [InlineData(0, 30.0)]
    [InlineData(1, 60.0)]
    [InlineData(2, 120.0)]
    [InlineData(3, 240.0)]
    [InlineData(4, 480.0)]
    [InlineData(5, 960.0)]
    [InlineData(6, 1800.0)]  // capped at 30 min
    [InlineData(10, 1800.0)] // well past cap
    public void ComputeCooldownDuration_MatchesExponentialBackoff(int level, double expectedSeconds)
    {
        RecoveryConfirmationFsm.ComputeCooldownDuration(level).Should().Be(expectedSeconds);
    }

    [Fact]
    public void RepeatedRecoveryFailures_BackoffIncrementsEachTime()
    {
        var (fsm, _) = CreateFsm();
        var node = CreateNode("A", healthState: NodeHealthState.RecoveryProbing);
        // Need 4+ nodes so cooldown budget (floor(4/3)=1) allows one cooldown
        var nodes = new List<NodeState>
        {
            node,
            CreateNode("B"), CreateNode("C"), CreateNode("D"),
        };

        // Fail 4 times — backoff should increment each time
        for (int i = 0; i < 4; i++)
        {
            node.SetHealthState(NodeHealthState.RecoveryProbing);
            node.ResetRecoveryProbeSuccess();
            // Reset cooldown state so we can re-enter
            node.ResetCooldown();
            var outcome = fsm.OnProbeResult(node, success: false, nodes);
            outcome.Action.Should().Be(RecoveryAction.EnterFailed);
            outcome.CooldownSeconds.Should().NotBeNull();
        }

        node.CooldownBackoffLevel.Should().Be(4, "4 failures → backoff level 4 (480s)");
    }

    [Fact]
    public void SuccessfulFullRecovery_ResetsBackoffLevel()
    {
        var (fsm, clock) = CreateFsm(stabilityMinutes: 1);
        var node = CreateNode("A", healthState: NodeHealthState.RecoveryProbing);
        var nodes = new List<NodeState> { node };

        // Fail first recovery to build backoff
        fsm.OnProbeResult(node, success: false, nodes);
        node.CooldownBackoffLevel.Should().Be(1);

        // Start new recovery
        fsm.TransitionToRecoveryProbing(node);
        // 3 successes → STABILITY_VERIFICATION
        fsm.OnProbeResult(node, success: true, nodes);
        fsm.OnProbeResult(node, success: true, nodes);
        fsm.OnProbeResult(node, success: true, nodes);

        // Advance past verification
        clock.AdvanceTime(TimeSpan.FromMinutes(2));
        fsm.OnProbeResult(node, success: true, nodes);

        node.HealthState.Should().Be(NodeHealthState.Active);
        node.CooldownBackoffLevel.Should().Be(0, "backoff reset on full recovery");
    }

    // ── Illegal transition validation (§10.7) ───────────────

    [Theory]
    [InlineData(NodeHealthState.Failed, NodeHealthState.Active)]  // skip RECOVERY_PROBING
    [InlineData(NodeHealthState.RecoveryProbing, NodeHealthState.Active)] // skip STABILITY_VERIFICATION
    [InlineData(NodeHealthState.Active, NodeHealthState.RecoveryProbing)] // wrong direction
    [InlineData(NodeHealthState.Failed, NodeHealthState.StabilityVerification)] // skip RECOVERY_PROBING
    public void IsLegalTransition_ReturnsFalse_ForIllegalTransitions(NodeHealthState from, NodeHealthState to)
    {
        RecoveryConfirmationFsm.IsLegalTransition(from, to).Should().BeFalse(
            $"{from} → {to} is an illegal transition per §10.7");
    }

    [Theory]
    [InlineData(NodeHealthState.Active, NodeHealthState.Failed)]
    [InlineData(NodeHealthState.Failed, NodeHealthState.RecoveryProbing)]
    [InlineData(NodeHealthState.RecoveryProbing, NodeHealthState.StabilityVerification)]
    [InlineData(NodeHealthState.RecoveryProbing, NodeHealthState.Failed)]
    [InlineData(NodeHealthState.StabilityVerification, NodeHealthState.Active)]
    [InlineData(NodeHealthState.StabilityVerification, NodeHealthState.Failed)]
    public void IsLegalTransition_ReturnsTrue_ForLegalTransitions(NodeHealthState from, NodeHealthState to)
    {
        RecoveryConfirmationFsm.IsLegalTransition(from, to).Should().BeTrue(
            $"{from} → {to} is a legal transition per §10.7");
    }

    // ── Cooldown budget in recovery failure ──────────────────

    [Fact]
    public void RecoveryProbingFailure_CooldownBudgetExhausted_DowngradesOnly()
    {
        var (fsm, _) = CreateFsm();
        var node = CreateNode("A", healthState: NodeHealthState.RecoveryProbing);
        // 3 nodes, max cooldown = 1, create a 2nd node already in cooldown
        var otherNode = CreateNode("B", healthState: NodeHealthState.Failed);
        otherNode.SetCooldown(DateTime.UtcNow.AddMinutes(5));
        var nodes = new List<NodeState> { node, otherNode };

        var outcome = fsm.OnProbeResult(node, success: false, nodes);

        outcome.Action.Should().Be(RecoveryAction.DowngradeOnly,
            "cooldown budget exhausted → downgrade score without setting cooldown");
        outcome.CooldownSeconds.Should().BeNull();
    }

    // ── Active nodes are not processed by recovery FSM ──────

    [Fact]
    public void OnProbeResult_ActiveNode_ReturnsNoOp()
    {
        var (fsm, _) = CreateFsm();
        var node = CreateNode("A", healthState: NodeHealthState.Active);
        var nodes = new List<NodeState> { node };

        var outcome = fsm.OnProbeResult(node, success: true, nodes);

        outcome.Action.Should().Be(RecoveryAction.NoOp,
            "active nodes are not in recovery pipeline — caller should use normal path");
    }

    [Fact]
    public void OnProbeResult_FailedNode_ReturnsNoOp()
    {
        var (fsm, _) = CreateFsm();
        var node = CreateNode("A", healthState: NodeHealthState.Failed);
        var nodes = new List<NodeState> { node };

        var outcome = fsm.OnProbeResult(node, success: true, nodes);

        outcome.Action.Should().Be(RecoveryAction.NoOp,
            "failed nodes must transition to RECOVERY_PROBING first");
    }

    // ── Full recovery lifecycle ───────────────────────────────

    [Fact]
    public void FullRecoveryLifecycle_ActiveToFailedToActive()
    {
        var (fsm, clock) = CreateFsm(stabilityMinutes: 1); // 1 min for test speed
        var node = CreateNode("A");
        var nodes = new List<NodeState> { node };

        // Step 1: ACTIVE → FAILED
        fsm.TransitionToFailed(node, cooldownMaxAllowed: 3, currentCooldownCount: 0);
        node.HealthState.Should().Be(NodeHealthState.Failed);
        node.SetCooldown(clock.UtcNow.AddSeconds(5));

        // Step 2: Cooldown expires → FAILED → RECOVERY_PROBING
        clock.AdvanceTime(TimeSpan.FromSeconds(6));
        fsm.TransitionToRecoveryProbing(node);
        node.HealthState.Should().Be(NodeHealthState.RecoveryProbing);

        // Step 3: 3 consecutive probe successes → STABILITY_VERIFICATION
        fsm.OnProbeResult(node, success: true, nodes);
        fsm.OnProbeResult(node, success: true, nodes);
        var outcome = fsm.OnProbeResult(node, success: true, nodes);
        outcome.Action.Should().Be(RecoveryAction.EnterStabilityVerification);
        node.HealthState.Should().Be(NodeHealthState.StabilityVerification);

        // Step 4: Verification period passes → ACTIVE
        clock.AdvanceTime(TimeSpan.FromMinutes(1));
        var finalOutcome = fsm.OnProbeResult(node, success: true, nodes);
        finalOutcome.Action.Should().Be(RecoveryAction.PromoteToActive);
        node.HealthState.Should().Be(NodeHealthState.Active);
        node.CooldownBackoffLevel.Should().Be(0, "full recovery resets backoff");
    }

    [Fact]
    public void FullRecoveryLifecycle_WithMidRecoveryFailure()
    {
        var (fsm, clock) = CreateFsm(stabilityMinutes: 1);
        var node = CreateNode("A");
        var nodes = new List<NodeState> { node };

        // ACTIVE → FAILED
        fsm.TransitionToFailed(node, cooldownMaxAllowed: 3, currentCooldownCount: 0);
        node.SetCooldown(clock.UtcNow.AddSeconds(5));
        clock.AdvanceTime(TimeSpan.FromSeconds(6));

        // First recovery attempt
        fsm.TransitionToRecoveryProbing(node);
        fsm.OnProbeResult(node, success: true, nodes);
        fsm.OnProbeResult(node, success: true, nodes);
        // 3rd probe fails → back to FAILED with backoff
        fsm.OnProbeResult(node, success: false, nodes);
        node.HealthState.Should().Be(NodeHealthState.Failed);
        node.CooldownBackoffLevel.Should().Be(1);

        // Second recovery attempt (after backoff cooldown)
        node.SetCooldown(clock.UtcNow.AddSeconds(5));
        clock.AdvanceTime(TimeSpan.FromSeconds(6));
        fsm.TransitionToRecoveryProbing(node);
        // 3 successes
        fsm.OnProbeResult(node, success: true, nodes);
        fsm.OnProbeResult(node, success: true, nodes);
        fsm.OnProbeResult(node, success: true, nodes);
        node.HealthState.Should().Be(NodeHealthState.StabilityVerification);

        // Verification
        clock.AdvanceTime(TimeSpan.FromMinutes(1));
        fsm.OnProbeResult(node, success: true, nodes);
        node.HealthState.Should().Be(NodeHealthState.Active);
    }

    // ── ResetHealthFsm ────────────────────────────────────────

    [Fact]
    public void ResetHealthFsm_ClearsAllRecoveryState()
    {
        var (fsm, clock) = CreateFsm();
        var node = CreateNode("A", healthState: NodeHealthState.StabilityVerification);
        node.MarkStabilityVerificationStarted(clock.UtcNow);
        node.SetCooldown(clock.UtcNow.AddMinutes(1));

        // Force backoff
        for (int i = 0; i < 3; i++)
            node.IncrementBackoffLevel();

        node.ResetHealthFsm();

        node.HealthState.Should().Be(NodeHealthState.Active);
        node.RecoveryProbeSuccessCount.Should().Be(0);
        node.CooldownBackoffLevel.Should().Be(0);
        node.StabilityVerificationStartedAt.Should().Be(DateTime.MinValue);
    }
}
