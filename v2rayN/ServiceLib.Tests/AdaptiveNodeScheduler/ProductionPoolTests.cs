using AwesomeAssertions;
using ServiceLib.Handler.AdaptiveNodeScheduler;
using Xunit;

namespace ServiceLib.Tests.AdaptiveNodeScheduler;

/// <summary>
/// P2: Verifies Bounded Production Pool + Failure-Driven Promotion.
///
/// Core invariants under test:
/// - TargetProductionSize = clamp(ceil(N × 0.35), 3, 6)
/// - TrafficTier gate: only Production-tier nodes enter the selector
/// - Vacancy-driven promotion: Standby → Production only when pool has vacancy
/// - Score-driven replacement is PROHIBITED
/// - Standby fallback: score ≥ 35 when ≥ 60 candidates insufficient
/// </summary>
public class ProductionPoolTests
{
    private static NodeState CreateNode(string tag, double score, bool inCooldown = false)
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
        if (inCooldown) node.SetCooldown(DateTime.UtcNow.AddMinutes(5));
        return node;
    }

    // ── TargetProductionSize formula (§5.3) ────────────────────

    [Theory]
    [InlineData(1, 3)]   // ceil(1×0.35)=1 → clamp→3
    [InlineData(2, 3)]   // ceil(2×0.35)=1 → clamp→3
    [InlineData(3, 3)]   // ceil(3×0.35)=2 → clamp→3
    [InlineData(5, 3)]   // ceil(5×0.35)=2 → clamp→3
    [InlineData(10, 4)]  // ceil(10×0.35)=4 → clamp→4
    [InlineData(15, 6)]  // ceil(15×0.35)=6 → clamp→6
    [InlineData(20, 6)]  // ceil(20×0.35)=7 → clamp→6
    [InlineData(30, 6)]  // ceil(30×0.35)=11 → clamp→6
    public void ComputeTargetSize_ReturnsExpected(int eligibleCount, int expected)
    {
        var mgr = new ActiveSetManager(new List<NodeState>());
        mgr.ComputeTargetSize(eligibleCount).Should().Be(expected);
    }

    // ── Production Pool sizing ──────────────────────────────────

    [Fact]
    public void GetProductionTags_3Eligible_AllInProduction()
    {
        var nodes = new List<NodeState>
        {
            CreateNode("A", 80),
            CreateNode("B", 70),
            CreateNode("C", 50),
        };

        var mgr = new ActiveSetManager(nodes);
        var production = mgr.GetProductionTags();

        // target=clamp(ceil(3×0.35)=2, 3, 6)=3 → all 3 eligible in production
        production.Should().BeEquivalentTo(new[] { "A", "B", "C" },
            "small pools: all eligible nodes enter production");
    }

    [Fact]
    public void GetProductionTags_5Eligible_BoundedTo3()
    {
        var nodes = new List<NodeState>
        {
            CreateNode("A", 80),
            CreateNode("B", 70),
            CreateNode("C", 65),
            CreateNode("D", 55),
            CreateNode("E", 50),
        };

        var mgr = new ActiveSetManager(nodes);
        var production = mgr.GetProductionTags();

        // target=clamp(ceil(5×0.35)=2, 3, 6)=3
        // A,B,C >= 60 → promoted; D(55),E(50) → Standby (score < 60)
        production.Count.Should().Be(3, "target size = 3 for 5 eligible");
        production.Should().Contain(new[] { "A", "B", "C" },
            "top 3 by score should enter production on first call");
    }

    [Fact]
    public void GetProductionTags_10Eligible_BoundedTo4()
    {
        var nodes = new List<NodeState>();
        for (int i = 0; i < 10; i++)
            nodes.Add(CreateNode($"N{i}", 90 - i * 3)); // 90,87,84,...63

        var mgr = new ActiveSetManager(nodes);
        var production = mgr.GetProductionTags();

        // target=clamp(ceil(10×0.35)=4, 3, 6)=4
        production.Count.Should().Be(4);
    }

    [Fact]
    public void GetProductionTags_20Eligible_CappedAt6()
    {
        var nodes = new List<NodeState>();
        for (int i = 0; i < 20; i++)
            nodes.Add(CreateNode($"N{i}", 90 - i * 2)); // 90,88,...52

        var mgr = new ActiveSetManager(nodes);
        var production = mgr.GetProductionTags();

        // target=clamp(ceil(20×0.35)=7, 3, 6)=6
        production.Count.Should().Be(6, "hard cap at MaxProductionNodes=6");
    }

    // ── TrafficTier gate (§5.4) ─────────────────────────────────

    [Fact]
    public void GetProductionTags_StandbyNode_ExcludedFromProduction()
    {
        // Node B is HealthState=Active but explicitly set to Standby
        var nodes = new List<NodeState>
        {
            CreateNode("A", 80),
            CreateNode("B", 75),
            CreateNode("C", 70),
            CreateNode("D", 65),
            CreateNode("E", 60),
        };
        nodes[1].SetTrafficTier(TrafficTier.Standby); // B=Standby explicitly

        var mgr = new ActiveSetManager(nodes);
        // First call: promotes top nodes to Production
        var production = mgr.GetProductionTags();

        // target=3. B(75) is explicitly Standby → A,C,D promoted (top 3 among non-Standby)
        production.Count.Should().Be(3);
    }

    [Fact]
    public void GetProductionTags_ProductionNode_StaysUntilBelowExit()
    {
        var nodes = new List<NodeState>
        {
            CreateNode("A", 80),
            CreateNode("B", 70),
            CreateNode("C", 65),
            CreateNode("D", 55),
            CreateNode("E", 50),
        };

        var mgr = new ActiveSetManager(nodes);
        var first = mgr.GetProductionTags();
        first.Should().Contain(new[] { "A", "B", "C" });

        // A drops to 40 — still above Exit=35, should stay in Production (sticky)
        nodes[0].UpdateScore(100, 0.0, 40, 0);
        var second = mgr.GetProductionTags();
        second.Should().Contain("A",
            "Production node at score 40 > Exit=35 must stay (sticky protection)");
    }

    [Fact]
    public void GetProductionTags_ProductionNodeBelowExit_DemotedToStandby()
    {
        var nodes = new List<NodeState>
        {
            CreateNode("A", 80),
            CreateNode("B", 70),
            CreateNode("C", 65),
            CreateNode("D", 55),
            CreateNode("E", 50),
        };

        var mgr = new ActiveSetManager(nodes);
        mgr.GetProductionTags(); // promotes A,B,C

        // A drops to 30 — below Exit=35, must be demoted
        nodes[0].UpdateScore(100, 0.0, 30, 0);
        var production = mgr.GetProductionTags();

        production.Should().NotContain("A",
            "Production node at score 30 < Exit=35 must be demoted to Standby");
        nodes[0].TrafficTier.Should().Be(TrafficTier.Standby,
            "demoted node must have TrafficTier=Standby");
    }

    // ── Vacancy-Driven Promotion (§5.7.3) ───────────────────────

    [Fact]
    public void GetProductionTags_Vacancy_PromotesFromStandby()
    {
        var nodes = new List<NodeState>
        {
            CreateNode("A", 80),
            CreateNode("B", 75),
            CreateNode("C", 70), // will be demoted
            CreateNode("D", 65), // standby, score >= 60 → promoted
            CreateNode("E", 50),
        };

        var mgr = new ActiveSetManager(nodes);
        mgr.GetProductionTags(); // promotes A,B,C (target=3)

        // C drops to 30 → demoted → vacancy of 1
        nodes[2].UpdateScore(100, 0.0, 30, 0);
        var production = mgr.GetProductionTags();

        production.Count.Should().Be(3, "vacancy should be filled — target size maintained");
        production.Should().Contain("D",
            "D (score=65 >= Entry=60) should be promoted from Standby to fill vacancy");
        production.Should().NotContain("C",
            "C dropped below Exit → demoted");
    }

    [Fact]
    public void GetProductionTags_MultipleVacancies_PromotesMultiple()
    {
        var nodes = new List<NodeState>
        {
            CreateNode("A", 80),
            CreateNode("B", 75),
            CreateNode("C", 70),
            CreateNode("D", 68), // standby, score >= 60
            CreateNode("E", 65), // standby, score >= 60
            CreateNode("F", 50),
        };

        var mgr = new ActiveSetManager(nodes);
        mgr.GetProductionTags(); // promotes A,B,C (target=3)

        // A and C drop below Exit → 2 vacancies
        nodes[0].UpdateScore(100, 0.0, 30, 0);
        nodes[2].UpdateScore(100, 0.0, 25, 0);
        var production = mgr.GetProductionTags();

        production.Count.Should().Be(3, "both vacancies filled");
        production.Should().Contain(new[] { "D", "E" },
            "D and E should be promoted to fill 2 vacancies");
    }

    // ── Score-Driven Replacement PROHIBITED (§5.7.3) ────────────

    [Fact]
    public void GetProductionTags_HigherScoreStandby_DoesNotReplaceProduction()
    {
        // Pre-seed: A(40), B(75), C(70) as Production, D(65), E(95) as Standby.
        // A at 40 is sticky (>= Exit=35). E at 95 is highest score overall.
        // Pool is full (3=target). E stays Standby — no score-driven replacement.
        var nodes = new List<NodeState>
        {
            CreateNode("A", 40),  // Production, sticky (above Exit=35)
            CreateNode("B", 75),  // Production
            CreateNode("C", 70),  // Production
            CreateNode("D", 65),  // Standby
            CreateNode("E", 95),  // Standby, very high score
        };
        nodes[0].SetTrafficTier(TrafficTier.Production);
        nodes[1].SetTrafficTier(TrafficTier.Production);
        nodes[2].SetTrafficTier(TrafficTier.Production);

        var mgr = new ActiveSetManager(nodes);
        var production = mgr.GetProductionTags();

        // target for 5 eligible = clamp(ceil(5×0.35), 3, 6) = 3
        // A(40), B(75), C(70) already Production, all >= Exit=35 → keep all 3
        // E(95) is Standby, pool full → stays Standby
        production.Should().Contain("A",
            "A at score=40 (above Exit=35) must stay — score-driven replacement is PROHIBITED");
        production.Should().NotContain("E",
            "E at score=95 must remain Standby — no vacancy, no promotion");
        nodes[4].TrafficTier.Should().Be(TrafficTier.Standby,
            "E remains Standby even with highest score");
    }

    [Fact]
    public void GetProductionTags_NoVacancy_NoPromotion()
    {
        // Production pool is full (3 nodes). Even though Standby nodes
        // have higher scores, no promotion occurs without vacancy.
        var nodes = new List<NodeState>
        {
            CreateNode("A", 50),  // Production (sticky, above Exit)
            CreateNode("B", 45),  // Production (sticky, above Exit)
            CreateNode("C", 40),  // Production (sticky, above Exit)
            CreateNode("D", 90),  // Standby, high score
        };

        // Pre-set A,B,C as Production
        nodes[0].SetTrafficTier(TrafficTier.Production);
        nodes[1].SetTrafficTier(TrafficTier.Production);
        nodes[2].SetTrafficTier(TrafficTier.Production);

        var mgr = new ActiveSetManager(nodes);
        var production = mgr.GetProductionTags();

        // target for 4 eligible = clamp(ceil(4*0.35)=2, 3, 6)=3
        // A,B,C already Production, all above Exit=35 → keep all 3
        // D at 90 → no vacancy → stays Standby
        production.Should().Contain(new[] { "A", "B", "C" });
        production.Should().NotContain("D",
            "D must stay Standby — pool full, no score-driven replacement");
        nodes[3].TrafficTier.Should().Be(TrafficTier.Standby);
    }

    // ── Standby Fallback (§5.7.7) ───────────────────────────────

    [Fact]
    public void GetProductionTags_StandbyInsufficient_FallbackToExitThreshold()
    {
        // Only 1 Standby >= 60, but need 3 Production. Fallback: promote >= 35.
        var nodes = new List<NodeState>
        {
            CreateNode("A", 80),  // → Production
            CreateNode("B", 55),  // → Fallback (>= 35, < 60)
            CreateNode("C", 45),  // → Fallback (>= 35, < 60)
            CreateNode("D", 30),  // → excluded (< 35)
        };

        var mgr = new ActiveSetManager(nodes);
        var production = mgr.GetProductionTags();

        // target=clamp(ceil(4*0.35)=2, 3, 6)=3
        // A(80) >= Entry → standard promotion
        // B(55), C(45) >= Exit but < Entry → fallback promotion
        // D(30) < Exit → excluded
        production.Count.Should().Be(3, "fallback allows score >= 35 nodes to fill vacancies");
        production.Should().Contain(new[] { "A", "B", "C" });
        production.Should().NotContain("D");
    }

    [Fact]
    public void GetProductionTags_AllBelowEntry_AllFallback()
    {
        // No nodes >= 60 — all promotions are fallback (>= 35)
        var nodes = new List<NodeState>
        {
            CreateNode("A", 55),
            CreateNode("B", 50),
            CreateNode("C", 48),
            CreateNode("D", 40),
            CreateNode("E", 30),
        };

        var mgr = new ActiveSetManager(nodes);
        var production = mgr.GetProductionTags();

        // target=3. Top 3 by score (>= 35) promoted.
        production.Count.Should().Be(3);
        production.Should().Contain(new[] { "A", "B", "C" });
        production.Should().NotContain("E", "score=30 < Exit=35");
    }

    // ── Cooldown & Recovery (preserved from v7.4) ───────────────

    [Fact]
    public void GetProductionTags_CooldownNode_Excluded()
    {
        var nodes = new List<NodeState>
        {
            CreateNode("A", 80),
            CreateNode("B", 75),
            CreateNode("C", 90, inCooldown: true), // high score but cooldown
            CreateNode("D", 50),
        };

        var mgr = new ActiveSetManager(nodes);
        var production = mgr.GetProductionTags();

        production.Should().NotContain("C",
            "cooldown nodes excluded regardless of score or TrafficTier");
    }

    [Fact]
    public void GetProductionTags_RecoveryProbing_Excluded()
    {
        var nodes = new List<NodeState>
        {
            CreateNode("A", 80),
            CreateNode("B", 75),
            CreateNode("C", 85), // Recovery
            CreateNode("D", 70),
            CreateNode("E", 65),
        };
        nodes[2].SetHealthState(NodeHealthState.RecoveryProbing);

        var mgr = new ActiveSetManager(nodes);
        var production = mgr.GetProductionTags();

        production.Should().NotContain("C",
            "RECOVERY_PROBING node excluded — HealthState != Active");
    }

    [Fact]
    public void GetProductionTags_AllCooldown_FallbackToRecoveryProbing()
    {
        var now = DateTime.UtcNow;
        var nodes = new List<NodeState>
        {
            CreateNode("A", 80, inCooldown: true),
            CreateNode("B", 70, inCooldown: true),
            CreateNode("C", 60), // RecoveryProbing, 2 successes
        };
        nodes[0].SetHealthState(NodeHealthState.Failed);
        nodes[1].SetHealthState(NodeHealthState.Failed);
        nodes[2].SetHealthState(NodeHealthState.RecoveryProbing);
        nodes[2].IncrementRecoveryProbeSuccess();
        nodes[2].IncrementRecoveryProbeSuccess();

        var mgr = new ActiveSetManager(nodes);
        var production = mgr.GetProductionTags();

        production.Should().ContainSingle().Which.Should().Be("C",
            "fallback: RECOVERY_PROBING with successes preferred over cooldown");
    }

    // ── GetStandbyTags ──────────────────────────────────────────

    [Fact]
    public void GetStandbyTags_ReturnsNonProductionEligible()
    {
        var nodes = new List<NodeState>
        {
            CreateNode("A", 80),
            CreateNode("B", 70),
            CreateNode("C", 65),
            CreateNode("D", 55),
            CreateNode("E", 50),
        };

        var mgr = new ActiveSetManager(nodes);
        mgr.GetProductionTags(); // promotes A,B,C

        var standby = mgr.GetStandbyTags();
        standby.Should().BeEquivalentTo(new[] { "D", "E" },
            "D and E are eligible but not in Production pool");
    }

    // ── HasActiveSetChanged with tiering ────────────────────────

    [Fact]
    public void HasActiveSetChanged_PromotionTriggersChange()
    {
        var nodes = new List<NodeState>
        {
            CreateNode("A", 80),
            CreateNode("B", 75),
            CreateNode("C", 70),
            CreateNode("D", 55),
        };

        var mgr = new ActiveSetManager(nodes);
        mgr.Prime(); // establishes baseline

        // D rises above Entry → eligible for promotion
        // But production already at target(4)=clamp(ceil(4*0.35)=2,3,6)=3, and has 3 (A,B,C) → no vacancy
        // Actually wait: target for 4 eligible = clamp(ceil(4*0.35)=2, 3, 6)=3.
        // A,B,C are all >= 60 → all promoted. Production full at 3. D at 65 → no vacancy.
        // So HasActiveSetChanged should be false.
        nodes[3].UpdateScore(100, 0.0, 65, 0);
        var changed1 = mgr.HasActiveSetChanged();
        changed1.Should().BeFalse("no vacancy — D can't enter Production, no change");

        // Now A drops to 30 → demoted → vacancy → D promoted
        nodes[0].UpdateScore(100, 0.0, 30, 0);
        var changed2 = mgr.HasActiveSetChanged();
        changed2.Should().BeTrue("A demoted + D promoted → Production Pool changed");
    }

    // ── IsEligiblePoolEmpty (preserved) ─────────────────────────

    [Fact]
    public void IsEligiblePoolEmpty_AllRecovery_ReturnsTrue()
    {
        var nodes = new List<NodeState>
        {
            CreateNode("A", 80),
            CreateNode("B", 70),
        };
        nodes[0].SetHealthState(NodeHealthState.RecoveryProbing);
        nodes[1].SetHealthState(NodeHealthState.StabilityVerification);

        var mgr = new ActiveSetManager(nodes);
        mgr.GetProductionTags();

        mgr.IsEligiblePoolEmpty.Should().BeTrue("no nodes with HealthState=Active");
    }

    [Fact]
    public void IsEligiblePoolEmpty_SomeActive_ReturnsFalse()
    {
        var nodes = new List<NodeState>
        {
            CreateNode("A", 80),
            CreateNode("B", 70),
        };

        var mgr = new ActiveSetManager(nodes);
        mgr.GetProductionTags();

        mgr.IsEligiblePoolEmpty.Should().BeFalse();
    }
}
