using AwesomeAssertions;
using ServiceLib.Handler.AdaptiveNodeScheduler;
using Xunit;

namespace ServiceLib.Tests.AdaptiveNodeScheduler;

/// <summary>
/// P0#1: Tests for GlobalFreezeController — prevents self-oscillation during
/// external shocks by freezing the control plane when >60% of active-set nodes
/// fail within a 15s window.
///
/// Covers: freeze trigger, freeze block, auto-unfreeze, freeze hysteresis,
/// escalation during freeze cooldown, edge cases.
/// </summary>
public class GlobalFreezeControllerTests
{
    private static NodeState CreateNode(string tag, double score = 50)
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
        return node;
    }

    private static (GlobalFreezeController controller, FakeClock clock) CreateController(
        double triggerRatio = 0.60,
        int triggerWindowSec = 15,
        int freezeDurationSec = 60,
        int freezeCooldownSec = 120)
    {
        var clock = new FakeClock();
        var controller = new GlobalFreezeController(clock)
        {
            TriggerRatio = triggerRatio,
            TriggerWindow = TimeSpan.FromSeconds(triggerWindowSec),
            FreezeDuration = TimeSpan.FromSeconds(freezeDurationSec),
            FreezeCooldownDuration = TimeSpan.FromSeconds(freezeCooldownSec),
        };
        return (controller, clock);
    }

    // ── Initial state ─────────────────────────────────────────

    [Fact]
    public void InitialState_IsNormal()
    {
        var (controller, _) = CreateController();

        controller.State.Should().Be(FreezeState.Normal);
        controller.IsFrozen.Should().BeFalse();
    }

    [Fact]
    public void Evaluate_EmptyActiveSet_AllowsOperation()
    {
        var (controller, _) = CreateController();

        var decision = controller.Evaluate(Array.Empty<string>());

        decision.Type.Should().Be(FreezeDecisionType.Allow);
    }

    // ── Freeze trigger ────────────────────────────────────────

    [Fact]
    public void Evaluate_AllActiveNodesFailed_TriggersFreeze()
    {
        var (controller, _) = CreateController();
        var activeTags = new[] { "A", "B", "C" };
        foreach (var tag in activeTags)
            controller.RecordFailure(tag);

        var decision = controller.Evaluate(activeTags);

        decision.Type.Should().Be(FreezeDecisionType.TriggerFreeze);
        decision.Reason.Should().Contain("60%");
        decision.FrozenActiveTags.Should().BeEquivalentTo(activeTags);
        controller.State.Should().Be(FreezeState.FreezeActive);
        controller.IsFrozen.Should().BeTrue();
    }

    [Fact]
    public void Evaluate_TwoOfThreeFailed_TriggersFreeze()
    {
        // 2/3 = 66.7% > 60% trigger
        var (controller, _) = CreateController();
        var activeTags = new[] { "A", "B", "C" };
        controller.RecordFailure("A");
        controller.RecordFailure("B");
        // C has no failures

        var decision = controller.Evaluate(activeTags);

        decision.Type.Should().Be(FreezeDecisionType.TriggerFreeze);
    }

    [Fact]
    public void Evaluate_OneOfThreeFailed_DoesNotTriggerFreeze()
    {
        // 1/3 = 33.3% < 60%
        var (controller, _) = CreateController();
        var activeTags = new[] { "A", "B", "C" };
        controller.RecordFailure("A");

        var decision = controller.Evaluate(activeTags);

        decision.Type.Should().Be(FreezeDecisionType.Allow);
        controller.State.Should().Be(FreezeState.Normal);
    }

    [Fact]
    public void Evaluate_NoFailures_DoesNotTriggerFreeze()
    {
        var (controller, _) = CreateController();
        var activeTags = new[] { "A", "B", "C", "D", "E" };

        var decision = controller.Evaluate(activeTags);

        decision.Type.Should().Be(FreezeDecisionType.Allow);
    }

    [Fact]
    public void Evaluate_FailureWindowExpired_DoesNotCount()
    {
        var (controller, clock) = CreateController(triggerWindowSec: 5);
        var activeTags = new[] { "A", "B", "C" };

        // Record failures for A and B
        controller.RecordFailure("A");
        controller.RecordFailure("B");

        // Advance past the trigger window
        clock.AdvanceTime(TimeSpan.FromSeconds(6));

        // Now A and B's failures are stale, C has none → 0/3 = 0%
        var decision = controller.Evaluate(activeTags);

        decision.Type.Should().Be(FreezeDecisionType.Allow,
            "failures expired from window — no longer counted");
    }

    [Fact]
    public void Evaluate_FailureJustInsideWindow_StillCounts()
    {
        var (controller, clock) = CreateController(triggerWindowSec: 15);
        var activeTags = new[] { "A", "B", "C" };

        controller.RecordFailure("A");
        controller.RecordFailure("B");
        clock.AdvanceTime(TimeSpan.FromSeconds(14)); // still inside 15s window

        var decision = controller.Evaluate(activeTags);

        decision.Type.Should().Be(FreezeDecisionType.TriggerFreeze,
            "14s < 15s window, failures still count");
    }

    [Fact]
    public void Evaluate_FailureAtWindowBoundary_ExpiresWhenStrictlyPast()
    {
        // At exactly 15.0 seconds, the failure age == window → NOT yet expired (strict > comparison)
        // At 15.001 seconds, the failure age > window → expired
        var (controller, clock) = CreateController(triggerWindowSec: 15);
        var activeTags = new[] { "A", "B", "C" };

        controller.RecordFailure("A");
        controller.RecordFailure("B");
        clock.AdvanceTime(TimeSpan.FromSeconds(15) + TimeSpan.FromMilliseconds(1)); // just past boundary

        // Both A and B expired → 0/3 failures
        var decision = controller.Evaluate(activeTags);

        decision.Type.Should().Be(FreezeDecisionType.Allow,
            "failures strictly past the window boundary expire");
    }

    // ── Freeze blocks mutations ────────────────────────────────

    [Fact]
    public void Evaluate_DuringFreeze_BlocksMutations()
    {
        var (controller, _) = CreateController();
        var activeTags = new[] { "A", "B", "C" };
        foreach (var tag in activeTags)
            controller.RecordFailure(tag);

        // Trigger freeze
        controller.Evaluate(activeTags);
        controller.IsFrozen.Should().BeTrue();

        // Subsequent evaluate during freeze → block
        var decision = controller.Evaluate(activeTags);
        decision.Type.Should().Be(FreezeDecisionType.BlockMutation);
    }

    [Fact]
    public void Evaluate_DuringFreeze_BlocksEvenIfNewFailuresArrive()
    {
        var (controller, _) = CreateController();
        var activeTags = new[] { "A", "B", "C" };
        foreach (var tag in activeTags)
            controller.RecordFailure(tag);

        // Trigger freeze
        controller.Evaluate(activeTags);

        // New failure arrives during freeze
        controller.RecordFailure("A");
        var decision = controller.Evaluate(activeTags);

        decision.Type.Should().Be(FreezeDecisionType.BlockMutation,
            "freeze should block regardless of new failures");
    }

    // ── Auto-unfreeze ──────────────────────────────────────────

    [Fact]
    public void Evaluate_AfterFreezeDuration_Unfreezes()
    {
        var (controller, clock) = CreateController(freezeDurationSec: 10);
        var activeTags = new[] { "A", "B", "C" };
        foreach (var tag in activeTags)
            controller.RecordFailure(tag);

        // Trigger freeze
        controller.Evaluate(activeTags);
        controller.IsFrozen.Should().BeTrue();

        // Advance past freeze duration
        clock.AdvanceTime(TimeSpan.FromSeconds(10));

        var decision = controller.Evaluate(activeTags);
        decision.Type.Should().Be(FreezeDecisionType.Unfreeze);
        controller.IsFrozen.Should().BeFalse();
        controller.State.Should().Be(FreezeState.FreezeCooldown,
            "after unfreeze, enters freeze_cooldown");
    }

    [Fact]
    public void Evaluate_JustBeforeFreezeExpiry_StillBlocks()
    {
        var (controller, clock) = CreateController(freezeDurationSec: 60);
        var activeTags = new[] { "A", "B", "C" };
        foreach (var tag in activeTags)
            controller.RecordFailure(tag);

        controller.Evaluate(activeTags);
        clock.AdvanceTime(TimeSpan.FromSeconds(59)); // 1s before expiry

        var decision = controller.Evaluate(activeTags);
        decision.Type.Should().Be(FreezeDecisionType.BlockMutation,
            "1s before expiry — still frozen");
    }

    // ── Freeze cooldown (hysteresis) ───────────────────────────

    [Fact]
    public void Evaluate_DuringFreezeCooldown_AllowsNormalOperation()
    {
        var (controller, clock) = CreateController(freezeDurationSec: 10);
        var activeTags = new[] { "A", "B", "C" };
        foreach (var tag in activeTags)
            controller.RecordFailure(tag);

        // Trigger and expire freeze
        controller.Evaluate(activeTags);
        clock.AdvanceTime(TimeSpan.FromSeconds(10));
        controller.Evaluate(activeTags); // unfreeze
        controller.State.Should().Be(FreezeState.FreezeCooldown);

        // Record only 1 failure (33.3% < 60%) — no re-freeze, no escalation
        controller.RecordFailure("A");
        var decision = controller.Evaluate(activeTags);

        decision.Type.Should().Be(FreezeDecisionType.Allow,
            "single failure below threshold — normal operation during cooldown");
    }

    [Fact]
    public void Evaluate_AfterFreezeCooldownExpires_ReturnsToNormal()
    {
        var (controller, clock) = CreateController(freezeDurationSec: 10, freezeCooldownSec: 30);
        var activeTags = new[] { "A", "B", "C" };
        foreach (var tag in activeTags)
            controller.RecordFailure(tag);

        controller.Evaluate(activeTags); // trigger freeze
        clock.AdvanceTime(TimeSpan.FromSeconds(10));
        controller.Evaluate(activeTags); // unfreeze → cooldown
        clock.AdvanceTime(TimeSpan.FromSeconds(30));
        var decision = controller.Evaluate(activeTags); // cooldown expired → normal

        decision.Type.Should().Be(FreezeDecisionType.Allow);
        controller.State.Should().Be(FreezeState.Normal);
    }

    [Fact]
    public void Evaluate_AfterCooldownExpires_CanReFreeze()
    {
        var (controller, clock) = CreateController(freezeDurationSec: 10, freezeCooldownSec: 30);
        var activeTags = new[] { "A", "B", "C" };

        // First freeze cycle
        foreach (var tag in activeTags) controller.RecordFailure(tag);
        controller.Evaluate(activeTags);
        clock.AdvanceTime(TimeSpan.FromSeconds(10));
        controller.Evaluate(activeTags); // unfreeze
        clock.AdvanceTime(TimeSpan.FromSeconds(30));
        controller.Evaluate(activeTags); // cooldown expired

        controller.State.Should().Be(FreezeState.Normal);

        // New failures after cooldown → should trigger new freeze
        controller.Reset();
        foreach (var tag in activeTags) controller.RecordFailure(tag);
        var decision = controller.Evaluate(activeTags);

        decision.Type.Should().Be(FreezeDecisionType.TriggerFreeze,
            "after cooldown expires, new freeze can be triggered");
    }

    // ── Escalation during freeze cooldown ─────────────────────

    [Fact]
    public void Evaluate_FreezeCooldown_MassAnomaly_EscalatesToEmergencyDisable()
    {
        var (controller, clock) = CreateController(freezeDurationSec: 10);
        var activeTags = new[] { "A", "B", "C" };

        // Trigger and expire freeze
        foreach (var tag in activeTags) controller.RecordFailure(tag);
        controller.Evaluate(activeTags);
        clock.AdvanceTime(TimeSpan.FromSeconds(10));
        controller.Evaluate(activeTags); // unfreeze → cooldown
        controller.State.Should().Be(FreezeState.FreezeCooldown);

        // During cooldown, all active nodes fail again
        string? escalationReason = null;
        controller.EmergencyDisableRequested += reason => escalationReason = reason;
        foreach (var tag in activeTags) controller.RecordFailure(tag);

        var decision = controller.Evaluate(activeTags);

        decision.Type.Should().Be(FreezeDecisionType.EmergencyDisable);
        escalationReason.Should().NotBeNull();
        escalationReason.Should().Contain("escalation");
    }

    // ── Reset ──────────────────────────────────────────────────

    [Fact]
    public void Reset_ReturnsToNormalAndClearsFailures()
    {
        var (controller, _) = CreateController();
        var activeTags = new[] { "A", "B", "C" };

        // Trigger freeze
        foreach (var tag in activeTags) controller.RecordFailure(tag);
        controller.Evaluate(activeTags);
        controller.IsFrozen.Should().BeTrue();

        // Reset
        controller.Reset();

        controller.State.Should().Be(FreezeState.Normal);
        controller.IsFrozen.Should().BeFalse();

        // After reset, same failures should trigger a new freeze
        foreach (var tag in activeTags) controller.RecordFailure(tag);
        var decision = controller.Evaluate(activeTags);

        decision.Type.Should().Be(FreezeDecisionType.TriggerFreeze,
            "reset clears state, new failures trigger new freeze");
    }

    // ── Snapshot ───────────────────────────────────────────────

    [Fact]
    public void GetSnapshot_ReflectsCurrentState()
    {
        var (controller, _) = CreateController();
        var activeTags = new[] { "A", "B", "C" };
        foreach (var tag in activeTags) controller.RecordFailure(tag);
        controller.Evaluate(activeTags);

        var snapshot = controller.GetSnapshot();

        snapshot.State.Should().Be(FreezeState.FreezeActive);
        snapshot.FreezeStartedAt.Should().NotBe(DateTime.MinValue);
    }

    // ── Edge cases ─────────────────────────────────────────────

    [Fact]
    public void Evaluate_SingleActiveNode_Failure_DoesNotTriggerFreeze()
    {
        // 1 node, 60% of 1 = 0.6, 1 failure = 100% > 60%
        // But freezing with 1 node leaves nothing to route through
        // Actually, the spec says >60% triggers freeze regardless of node count
        var (controller, _) = CreateController();
        var activeTags = new[] { "A" };
        controller.RecordFailure("A");

        var decision = controller.Evaluate(activeTags);

        // 1/1 = 100% > 60% → freeze triggers per spec
        decision.Type.Should().Be(FreezeDecisionType.TriggerFreeze,
            "single node with 100% failure rate meets trigger criterion");
    }

    [Fact]
    public void Evaluate_ExactTriggerRatioBoundary_DoesNotTrigger()
    {
        // 3 active, triggerRatio=0.667, 2/3=66.67% is NOT > 66.7%
        var (controller, _) = CreateController(triggerRatio: 0.667);

        var activeTags = new[] { "A", "B", "C" };
        controller.RecordFailure("A");
        controller.RecordFailure("B"); // 2/3 = 66.67%, NOT > 66.7%

        var decision = controller.Evaluate(activeTags);

        decision.Type.Should().Be(FreezeDecisionType.Allow,
            "2/3=66.7% is not > 66.7% trigger ratio (strict greater-than comparison)");
    }

    [Fact]
    public void RecordFailure_MultipleFailuresSameNode_CountsOnce()
    {
        // Multiple failures from same node should only count the node once
        var (controller, _) = CreateController();
        var activeTags = new[] { "A", "B", "C" };

        controller.RecordFailure("A");
        controller.RecordFailure("A"); // duplicate
        controller.RecordFailure("A"); // triplicate

        // Only A failed → 1/3 = 33.3% < 60%
        var decision = controller.Evaluate(activeTags);

        decision.Type.Should().Be(FreezeDecisionType.Allow,
            "multiple failures from same node count as 1 failed node");
    }

    [Fact]
    public void RecordFailure_NonActiveNode_DoesNotAffectFreeze()
    {
        var (controller, _) = CreateController();
        var activeTags = new[] { "A", "B" };

        // Record failure for node C (not in active set)
        controller.RecordFailure("C");

        var decision = controller.Evaluate(activeTags);

        decision.Type.Should().Be(FreezeDecisionType.Allow,
            "only active-set node failures count toward freeze threshold");
    }

    [Fact]
    public void Evaluate_FailureRatioEdgeCase_TwoOfFive()
    {
        // 2/5 = 40% < 60% → no freeze; 3/5 = 60% → NOT > 60%, no freeze; 4/5 = 80% > 60% → freeze
        var (controller, _) = CreateController(triggerRatio: 0.60);
        var activeTags = new[] { "A", "B", "C", "D", "E" };

        controller.RecordFailure("A");
        controller.RecordFailure("B");
        controller.RecordFailure("C"); // 3/5 = 60%, not > 60%

        var decision = controller.Evaluate(activeTags);

        decision.Type.Should().Be(FreezeDecisionType.Allow,
            "3/5=60% exactly at threshold, not above → no freeze");

        controller.RecordFailure("D"); // 4/5 = 80% > 60%
        decision = controller.Evaluate(activeTags);
        decision.Type.Should().Be(FreezeDecisionType.TriggerFreeze);
    }

    // ── Freeze decision carries snapshot data ──────────────────

    [Fact]
    public void TriggerFreeze_DecisionHasFrozenTagsAndTimestamp()
    {
        var (controller, clock) = CreateController();
        var activeTags = new[] { "A", "B", "C" };
        foreach (var tag in activeTags)
            controller.RecordFailure(tag);

        var decision = controller.Evaluate(activeTags);

        decision.Type.Should().Be(FreezeDecisionType.TriggerFreeze);
        decision.FrozenActiveTags.Should().BeEquivalentTo(activeTags);
        decision.FreezeStartedAt.Should().Be(clock.UtcNow);
    }

    [Fact]
    public void Unfreeze_DecisionHasReason()
    {
        var (controller, clock) = CreateController(freezeDurationSec: 5);
        var activeTags = new[] { "A", "B", "C" };
        foreach (var tag in activeTags) controller.RecordFailure(tag);

        controller.Evaluate(activeTags);
        clock.AdvanceTime(TimeSpan.FromSeconds(5));

        var decision = controller.Evaluate(activeTags);

        decision.Type.Should().Be(FreezeDecisionType.Unfreeze);
        decision.Reason.Should().Be("freeze_duration_expired");
    }

    [Fact]
    public void Allow_DecisionReasonIsNull_WhenNormal()
    {
        var (controller, _) = CreateController();
        var activeTags = new[] { "A", "B", "C" };

        var decision = controller.Evaluate(activeTags);

        decision.Type.Should().Be(FreezeDecisionType.Allow);
        decision.Reason.Should().BeNull();
    }
}
