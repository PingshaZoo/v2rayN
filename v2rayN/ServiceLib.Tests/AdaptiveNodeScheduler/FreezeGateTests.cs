using AwesomeAssertions;
using ServiceLib.Handler.AdaptiveNodeScheduler;
using Xunit;

namespace ServiceLib.Tests.AdaptiveNodeScheduler;

/// <summary>
/// §11.8: Freeze gate tests — verifies that during global freeze,
/// observation (EWMA) continues but state transitions (cooldown, consecutiveFailures)
/// are blocked. This prevents latent cooldown explosion when freeze ends.
/// </summary>
public class FreezeGateTests
{
    private static NodeState CreateNode(string tag = "node-a", double score = 80)
    {
        var node = new NodeState
        {
            Tag = tag,
            Host = "example.com",
            Port = 443,
            Protocol = ProxyProtocol.Tcp,
            ChildIndexId = tag,
        };
        node.UpdateScore(150, 0.05, score, 0);
        return node;
    }

    /// <summary>
    /// Triggers a freeze for a single-node scenario and returns the frozen controller.
    /// </summary>
    private static (FailureCollector collector, GlobalFreezeController freeze, FakeClock clock)
        CreateFrozenCollector(NodeState node)
    {
        var clock = new FakeClock();
        var freeze = new GlobalFreezeController(clock)
        {
            TriggerRatio = 0.60,
            TriggerWindow = TimeSpan.FromSeconds(15),
            FreezeDuration = TimeSpan.FromSeconds(60),
        };
        var scorer = new ScoreCalculator();
        var cooldown = new CooldownFsm();
        var collector = new FailureCollector(scorer, cooldown, null, freeze);

        // Record a failure and evaluate to trigger freeze (1/1 = 100% > 60%)
        freeze.RecordFailure(node.Tag);
        var decision = freeze.Evaluate([node.Tag]);
        decision.Type.Should().Be(FreezeDecisionType.TriggerFreeze);

        return (collector, freeze, clock);
    }

    // ── Freeze gate: EWMA updates but cooldown blocked ──────────

    [Fact]
    public void DuringFreeze_RecordFailure_UpdatesEwmaButNotConsecutiveFailures()
    {
        var node = CreateNode();
        var (collector, freeze, _) = CreateFrozenCollector(node);

        double latencyBefore = node.EwmaLatencyMs;
        int failsBefore = node.ConsecutiveFailures;

        collector.RecordFailure(node, FailureType.Timeout, [node]);

        // EWMA updated — observation continues
        node.EwmaLatencyMs.Should().BeGreaterThan(latencyBefore,
            "EWMA must reflect degraded quality even during freeze");
        // Consecutive failures NOT incremented — state transition blocked
        node.ConsecutiveFailures.Should().Be(failsBefore,
            "consecutiveFailures must not increment during freeze (state transition blocked)");
    }

    [Fact]
    public void DuringFreeze_RecordFailure_DoesNotEnterCooldown()
    {
        var node = CreateNode();
        var (collector, freeze, _) = CreateFrozenCollector(node);

        // Multiple failures during freeze — none should trigger cooldown
        for (int i = 0; i < 5; i++)
        {
            collector.RecordFailure(node, FailureType.Timeout, [node]);
        }

        node.IsInCooldown.Should().BeFalse(
            "cooldown must never be entered during global freeze");
        node.ConsecutiveFailures.Should().Be(0,
            "5 failures during freeze → consecutiveFailures still 0");
    }

    [Fact]
    public void DuringFreeze_EwmaDegradationIsAccurate()
    {
        var node = CreateNode(score: 80);
        var (collector, freeze, _) = CreateFrozenCollector(node);

        double scoreBefore = node.Score;
        double latencyBefore = node.EwmaLatencyMs;

        // Multiple timeout failures during freeze
        for (int i = 0; i < 3; i++)
        {
            collector.RecordFailure(node, FailureType.Timeout, [node]);
        }

        // Score degrades (observation continues)
        node.Score.Should().BeLessThan(scoreBefore,
            "EWMA score should degrade during freeze — probe data is real");
        node.EwmaLatencyMs.Should().BeGreaterThan(latencyBefore,
            "EWMA latency should increase during freeze");
    }

    [Fact]
    public void AfterFreezeEnds_NormalCooldownResumes()
    {
        var node = CreateNode();
        var (collector, freeze, clock) = CreateFrozenCollector(node);

        // Record a failure during freeze — should not enter cooldown
        collector.RecordFailure(node, FailureType.Timeout, [node]);
        node.IsInCooldown.Should().BeFalse();

        // Advance time past freeze duration
        clock.AdvanceTime(TimeSpan.FromSeconds(61));
        freeze.Evaluate([node.Tag]); // transitions to Cooldown state
        clock.AdvanceTime(TimeSpan.FromSeconds(121));
        freeze.Evaluate([node.Tag]); // transitions back to Normal

        freeze.IsFrozen.Should().BeFalse();

        // Now a real failure follows the normal path
        collector.RecordFailure(node, FailureType.Timeout, [node]);
        node.ConsecutiveFailures.Should().Be(1,
            "after freeze, consecutiveFailures should increment normally");
    }

    // ── Freeze gate: DNS failures still no-op during freeze ──────

    [Fact]
    public void DuringFreeze_DnsFailure_StillNoOp()
    {
        var node = CreateNode();
        var (collector, freeze, _) = CreateFrozenCollector(node);

        double scoreBefore = node.Score;
        double latencyBefore = node.EwmaLatencyMs;
        int failsBefore = node.ConsecutiveFailures;

        collector.RecordFailure(node, FailureType.DnsResolutionFailure, [node]);

        // DNS failure is still a no-op even during freeze
        node.Score.Should().BeApproximately(scoreBefore, 0.01);
        node.EwmaLatencyMs.Should().BeApproximately(latencyBefore, 0.01);
        node.ConsecutiveFailures.Should().Be(failsBefore);
    }

    // ── Without freeze, normal behavior ──────────────────────────

    [Fact]
    public void WithoutFreeze_RecordFailure_IncrementsFailuresAndEntersCooldown()
    {
        var node = CreateNode();
        var clock = new FakeClock();
        var freeze = new GlobalFreezeController(clock)
        {
            TriggerRatio = 0.60,
        };
        var scorer = new ScoreCalculator();
        var cooldown = new CooldownFsm();
        var collector = new FailureCollector(scorer, cooldown, null, freeze);

        // No freeze triggered — controller in Normal state
        freeze.IsFrozen.Should().BeFalse();

        int failsBefore = node.ConsecutiveFailures;
        collector.RecordFailure(node, FailureType.Timeout, [node]);

        node.ConsecutiveFailures.Should().Be(failsBefore + 1,
            "without freeze, consecutiveFailures should increment normally");
    }

    // ── Multiple nodes during freeze ─────────────────────────────

    [Fact]
    public void DuringFreeze_MultipleNodes_AllBlockCooldown()
    {
        var nodeA = CreateNode("a");
        var nodeB = CreateNode("b");
        var nodeC = CreateNode("c");

        var clock = new FakeClock();
        var freeze = new GlobalFreezeController(clock)
        {
            TriggerRatio = 0.60,
        };
        var scorer = new ScoreCalculator();
        var cooldown = new CooldownFsm();
        var collector = new FailureCollector(scorer, cooldown, null, freeze);

        // Trigger freeze with multi-node setup
        freeze.RecordFailure(nodeA.Tag);
        freeze.RecordFailure(nodeB.Tag);
        freeze.RecordFailure(nodeC.Tag);
        freeze.Evaluate([nodeA.Tag, nodeB.Tag, nodeC.Tag]);
        freeze.IsFrozen.Should().BeTrue();

        var allNodes = new[] { nodeA, nodeB, nodeC };

        // Multiple failures on all nodes during freeze
        for (int i = 0; i < 3; i++)
        {
            collector.RecordFailure(nodeA, FailureType.Timeout, allNodes);
            collector.RecordFailure(nodeB, FailureType.Timeout, allNodes);
            collector.RecordFailure(nodeC, FailureType.Timeout, allNodes);
        }

        nodeA.ConsecutiveFailures.Should().Be(0, "freeze blocks failure counting");
        nodeB.ConsecutiveFailures.Should().Be(0);
        nodeC.ConsecutiveFailures.Should().Be(0);
        nodeA.IsInCooldown.Should().BeFalse();
        nodeB.IsInCooldown.Should().BeFalse();
        nodeC.IsInCooldown.Should().BeFalse();
    }

    // ── Bug 2 fix: Active set must still update during freeze ──

    /// <summary>
    /// During freeze, the ActiveSetManager must still exclude nodes with
    /// HealthState != Active from the production pool. The freeze blocks
    /// cooldown entry (preventing mass ejection), but nodes that are already
    /// in a non-Active HealthState must be removable — otherwise the user's
    /// network is stuck on dead nodes for the full 60s freeze duration.
    /// </summary>
    [Fact]
    public void DuringFreeze_ProductionTags_ExcludesNonActiveHealthState()
    {
        var nodes = new List<NodeState>
        {
            CreateNode("A", 90), // Production, Active
            CreateNode("B", 85), // Production, but Failed (already dead before freeze)
            CreateNode("C", 80), // Standby, Active — should be promoted to replace B
            CreateNode("D", 50), // Standby
        };
        nodes[0].SetTrafficTier(TrafficTier.Production);
        nodes[1].SetTrafficTier(TrafficTier.Production);
        nodes[1].SetHealthState(NodeHealthState.Failed); // B is dead
        nodes[1].OverrideProductionPromotedAt(DateTime.UtcNow.AddMinutes(-5)); // MinTenure expired

        var mgr = new ActiveSetManager(nodes);
        var production = mgr.GetProductionTags();

        // B (HealthState=Failed) must be EXCLUDED from production
        production.Should().NotContain("B",
            "dead node (HealthState=Failed) must be excluded even during freeze");
        // C (score=80 >= Entry=60) should be promoted to replace B
        production.Should().Contain("C",
            "healthy Standby node should replace dead Production node even during freeze");
        production.Count.Should().Be(3, "target for 4 eligible = clamp(ceil(4*0.35)=2,3,6)=3");
    }

    [Fact]
    public void DuringFreeze_ProductionTags_ExcludesCooldownNodes()
    {
        // Cooldown is blocked during freeze, but if a node somehow entered
        // cooldown before freeze, it must still be excluded.
        var nodes = new List<NodeState>
        {
            CreateNode("A", 90),
            CreateNode("B", 85),
            CreateNode("C", 80),
            CreateNode("D", 45),
        };
        nodes[0].SetTrafficTier(TrafficTier.Production);
        nodes[0].SetCooldown(DateTime.UtcNow.AddMinutes(5)); // cooldown

        var mgr = new ActiveSetManager(nodes);
        var production = mgr.GetProductionTags();

        production.Should().NotContain("A",
            "cooldown nodes excluded regardless of freeze state");
        // target=3, 3 eligible (B,C,D). B(85),C(80) >= Entry=60 → promoted.
        // D(45) < FallbackPromotionThreshold(48) → excluded.
        production.Count.Should().Be(2,
            "only 2 nodes with score >= 60 for target=3 (D=45 < Fallback=48)");
    }
}
