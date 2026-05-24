using AwesomeAssertions;
using ServiceLib.Handler.AdaptiveNodeScheduler;
using Xunit;

namespace ServiceLib.Tests.AdaptiveNodeScheduler;

/// <summary>
/// P0.4: Verifies Active Set Hysteresis (Entry=60, Exit=35).
/// Prevents score oscillation from causing frequent active-set changes and xray reloads.
///
/// Key invariant: the sticky set (top-K without explorer) must remain stable
/// when nodes oscillate in the [35, 60) hysteresis zone. The explorer may vary
/// but does NOT get sticky status — it's a one-round exposure.
/// </summary>
public class ActiveSetManagerTests
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

    /// <summary>
    /// Returns the sticky core (top-K set). Since explorer no longer enters
    /// the production selector, this is equivalent to the full active set
    /// minus any artifacts. Used to verify the deterministic core.
    /// </summary>
    private static HashSet<string> StickyCore(List<string> active, HashSet<string> knownTopK)
    {
        // Remove at most one tag that is NOT in knownTopK (the explorer).
        var result = new HashSet<string>(active, StringComparer.Ordinal);
        foreach (var tag in active)
        {
            if (!knownTopK.Contains(tag))
            {
                result.Remove(tag);
                return result;
            }
        }
        return result;
    }

    [Fact]
    public void GetProductionTags_AllBelowEntryButAboveExit_FallsBackToTargetSize()
    {
        // 5 nodes, all at score=55 (below Entry=60, above Exit=35).
        // No nodes qualify for standard promotion (Entry=60). Fallback to
        // score >= 35 for TargetProductionSize=3.
        var nodes = new List<NodeState>
        {
            CreateNode("A", 55),
            CreateNode("B", 55),
            CreateNode("C", 55),
            CreateNode("D", 55),
            CreateNode("E", 55),
        };

        var mgr = new ActiveSetManager(nodes);
        var active = mgr.GetProductionTags();

        // target = clamp(ceil(5×0.35), 3, 6) = 3
        active.Count.Should().Be(3,
            "target=3: fallback promotes top 3 by score when none >= Entry");
    }

    [Fact]
    public void GetProductionTags_NodesAboveEntry_ShouldEnterProductionSet()
    {
        var nodes = new List<NodeState>
        {
            CreateNode("A", 75),
            CreateNode("B", 70),
            CreateNode("C", 65),
            CreateNode("D", 50),
            CreateNode("E", 45),
        };

        var mgr = new ActiveSetManager(nodes);
        var active = mgr.GetProductionTags();

        // target = clamp(ceil(5×0.35), 3, 6) = 3
        // A(75), B(70), C(65) >= Entry=60 → promoted to Production
        active.Count.Should().Be(3, "target=3, 3 nodes >= 60 promoted");

        // Production set must contain A, B, C
        active.Should().Contain(new[] { "A", "B", "C" },
            "nodes >= 60 must enter the production set");

        // D and E must NOT be in production selector (Standby, probe traffic only)
        active.Should().NotContain(new[] { "D", "E" },
            "nodes below Entry=60 must earn their way in through sustained probe quality");
    }

    [Fact]
    public void GetProductionTags_StickyNodes_ShouldStayUntilBelowExitThreshold()
    {
        // First call: establish sticky set with A, B, C (all >= 60)
        var nodes = new List<NodeState>
        {
            CreateNode("A", 82),
            CreateNode("B", 78),
            CreateNode("C", 62),
            CreateNode("D", 50),
        };

        var mgr = new ActiveSetManager(nodes);
        var firstActive = mgr.GetProductionTags();
        firstActive.Should().Contain("A").And.Contain("B").And.Contain("C");

        // Now scores drop into [35, 60) zone but stay above Exit=35
        nodes.First(n => n.Tag == "A").UpdateScore(100, 0.0, 55, 0);
        nodes.First(n => n.Tag == "B").UpdateScore(100, 0.0, 45, 0);
        nodes.First(n => n.Tag == "C").UpdateScore(100, 0.0, 40, 0);
        // D stays at 50

        var secondActive = mgr.GetProductionTags();

        // A, B, C are sticky (were in active set, score >= Exit=35) → MUST stay
        secondActive.Should().Contain(new[] { "A", "B", "C" },
            "previously active nodes at score 40-55 must stay (sticky, >= Exit=35)");
    }

    [Fact]
    public void GetProductionTags_NodeDropsBelowExit_ShouldBeEjected()
    {
        // 3 nodes → eligible <= 2, all are returned regardless of score
        // Need > 2 eligible to test ejection. Use 4 nodes.
        var nodes = new List<NodeState>
        {
            CreateNode("A", 80),
            CreateNode("B", 70),
            CreateNode("C", 50),
            CreateNode("D", 30),
        };

        var mgr = new ActiveSetManager(nodes);
        var firstActive = mgr.GetProductionTags();

        // target = clamp(ceil(4 * 0.35), 3, 6) = 3
        // First call: A(80),B(70) >= 60 → standard; C(50) >= 35 → fallback promotion
        // D(30) < 35 → excluded entirely
        firstActive.Should().Contain(new[] { "A", "B" });
        firstActive.Should().NotContain("D", "D at 30 < Exit=35, cannot even be explorer");

        // Now C drops to 30 (below Exit=35). If C was in active (as explorer), it should be ejected.
        // Force C in sticky set first by raising its score above 60.
        nodes.First(n => n.Tag == "C").UpdateScore(100, 0.0, 65, 0);
        mgr.GetProductionTags(); // establishes C in sticky

        // Now drop C to 30
        nodes.First(n => n.Tag == "C").UpdateScore(100, 0.0, 30, 0);
        var secondActive = mgr.GetProductionTags();

        secondActive.Should().NotContain("C",
            "C dropped to 30 < Exit=35, should be ejected from sticky set");
        secondActive.Should().Contain(new[] { "A", "B" },
            "A and B should remain in sticky set");
    }

    [Fact]
    public void GetProductionTags_ScoreOscillatingInHysteresisZone_ShouldNotFlipFlopStickySet()
    {
        // Node C oscillates between 45 and 55, never reaching Entry=60.
        // The sticky set (A, B) must remain unchanged across all rounds.
        // The explorer may or may not pick C — that's fine, but the STICKY
        // core must not change.

        var nodes = new List<NodeState>
        {
            CreateNode("A", 70),
            CreateNode("B", 65),
            CreateNode("C", 50),
            CreateNode("D", 48),
            CreateNode("E", 45),
        };

        var mgr = new ActiveSetManager(nodes);

        var stickyAcrossRounds = new List<HashSet<string>>();

        for (int round = 0; round < 6; round++)
        {
            // Oscillate C between 45 and 55
            double cScore = round % 2 == 0 ? 55 : 45;
            nodes.First(n => n.Tag == "C").UpdateScore(100, 0.0, cScore, 0);

            var active = mgr.GetProductionTags();

            // The sticky set must be {A, B} — C is below Entry=60, never enters sticky
            var knownTopK = new HashSet<string>(new[] { "A", "B" }, StringComparer.Ordinal);
            var sticky = StickyCore(active, knownTopK);

            sticky.SetEquals(knownTopK).Should().BeTrue(
                $"sticky set must be {{A,B}} regardless of C oscillation (round {round}, C score={cScore})");

            stickyAcrossRounds.Add(sticky);
        }

        // All sticky sets must be identical
        var first = stickyAcrossRounds[0];
        for (int i = 1; i < stickyAcrossRounds.Count; i++)
        {
            stickyAcrossRounds[i].SetEquals(first).Should().BeTrue(
                $"sticky set must not change across rounds (round {i})");
        }
    }

    [Fact]
    public void HasActiveSetChanged_OscillationInHysteresisZone_ShouldNotTriggerChange()
    {
        var nodes = new List<NodeState>
        {
            CreateNode("A", 80),
            CreateNode("B", 70),
            CreateNode("C", 50),
            CreateNode("D", 48),
            CreateNode("E", 45),
        };

        var mgr = new ActiveSetManager(nodes);
        mgr.GetProductionTags(); // establish _currentActiveSet = sticky {A,B}
        mgr.Prime();

        // Oscillate C between 45 and 55
        for (int i = 0; i < 5; i++)
        {
            double score = i % 2 == 0 ? 55 : 45;
            nodes.First(n => n.Tag == "C").UpdateScore(100, 0.0, score, 0);
            nodes.First(n => n.Tag == "D").UpdateScore(100, 0.0, score - 5, 0);
            nodes.First(n => n.Tag == "E").UpdateScore(100, 0.0, score - 10, 0);

            bool changed = mgr.HasActiveSetChanged();
            changed.Should().BeFalse(
                $"C oscillation to score={score} (all < Entry=60) must not trigger sticky set change (iter {i})");
        }
    }

    [Fact]
    public void HasActiveSetChanged_VacancyThenStandbyCrossesEntry_ShouldTriggerChange()
    {
        // 5 eligible, target=3. A(80),B(70) >= 60 → Production. C(50) >= 35 → fallback.
        // Production = {A,B,C}. Prime establishes baseline.
        // A drops to 30 (< Exit=35) → demoted → vacancy. C was already in production.
        // D rises to 65 (>= Entry) → promoted to fill vacancy → change detected.
        var nodes = new List<NodeState>
        {
            CreateNode("A", 80),
            CreateNode("B", 70),
            CreateNode("C", 50),
            CreateNode("D", 48),
            CreateNode("E", 42),
        };

        var mgr = new ActiveSetManager(nodes);
        mgr.GetProductionTags(); // establishes Production = {A,B,C}
        mgr.Prime();

        // A drops below Exit → demoted to Standby → vacancy of 1
        nodes.First(n => n.Tag == "A").UpdateScore(100, 0.0, 30, 0);
        // D rises to 65 — crosses Entry=60 → fills vacancy
        nodes.First(n => n.Tag == "D").UpdateScore(100, 0.0, 65, 0);
        bool changed = mgr.HasActiveSetChanged();
        changed.Should().BeTrue("A demoted + D promoted → Production Pool changed");
    }

    [Fact]
    public void GetProductionTags_CooldownNode_AlwaysExcluded()
    {
        var nodes = new List<NodeState>
        {
            CreateNode("A", 80),
            CreateNode("B", 75),
            CreateNode("C", 90, inCooldown: true), // high score but cooldown
            CreateNode("D", 50),
        };

        var mgr = new ActiveSetManager(nodes);
        var active = mgr.GetProductionTags();

        active.Should().NotContain("C",
            "cooldown nodes must be excluded regardless of score");
        active.Should().Contain(new[] { "A", "B" },
            "A and B pass Entry=60, should be in sticky set");
    }

    [Fact]
    public void GetProductionTags_TwoEligible_OnlyHealthyEnterProduction()
    {
        // 2 eligible: A(80) passes Entry, B(30) below Exit → excluded.
        // Pool runs below target when insufficient qualified nodes exist.
        var nodes = new List<NodeState>
        {
            CreateNode("A", 80),
            CreateNode("B", 30),
        };

        var mgr = new ActiveSetManager(nodes);
        var active = mgr.GetProductionTags();

        active.Should().Contain("A",
            "A at score=80 >= Entry=60 enters production");
        active.Should().NotContain("B",
            "B at score=30 < Exit=35 excluded — unhealthy nodes don't enter production");
    }

    [Fact]
    public void GetProductionTags_AllNodesCooldown_ReturnsShortestCooldown()
    {
        // When all nodes are in cooldown, the fallback picks the one with
        // shortest remaining cooldown — balancer must never be empty (§8.1 criterion 1).
        var now = DateTime.UtcNow;
        var nodes = new List<NodeState>
        {
            CreateNode("A", 80, inCooldown: true),
            CreateNode("B", 70, inCooldown: true),
        };
        // A's cooldown was set by CreateNode(inCooldown: true) using AddMinutes(5).
        // Give B a shorter cooldown so it's preferred.
        nodes[1].SetCooldown(now.AddMinutes(1));

        var mgr = new ActiveSetManager(nodes);
        var active = mgr.GetProductionTags();

        // Returns the node with shortest cooldown (B), not empty
        active.Should().ContainSingle().Which.Should().Be("B",
            "all cooldown → fallback to shortest remaining cooldown");
    }

    [Fact]
    public void GetProductionTags_NodesBelowExitThreshold_ShouldBeExcluded()
    {
        // Nodes with score < Exit=35 must never enter the production selector
        var nodes = new List<NodeState>
        {
            CreateNode("A", 80),
            CreateNode("B", 70),
            CreateNode("C", 30), // below Exit=35, should never appear
            CreateNode("D", 28), // below Exit=35
        };

        var mgr = new ActiveSetManager(nodes);
        var active = mgr.GetProductionTags();

        active.Should().NotContain(new[] { "C", "D" },
            "nodes below Exit=35 must not appear in active set, even as explorer");
        active.Should().Contain(new[] { "A", "B" });
    }

    [Fact]
    public void GetProductionTags_RecoveryProbingNode_ExcludedFromProductionSelector()
    {
        // §5.4 Invariant I2: RECOVERY_PROBING nodes MUST NOT enter production selector.
        // Node B is not in cooldown but HealthState=RecoveryProbing → must be excluded.
        var nodes = new List<NodeState>
        {
            CreateNode("A", 80),
            CreateNode("B", 75),
            CreateNode("C", 70),
            CreateNode("D", 65),
        };
        nodes[1].SetHealthState(NodeHealthState.RecoveryProbing);

        var mgr = new ActiveSetManager(nodes);
        var active = mgr.GetProductionTags();

        active.Should().NotContain("B",
            "RECOVERY_PROBING node must be excluded even when !IsInCooldown");
        // eligible=3 (A,C,D), target=clamp(ceil(3×0.35),3,6)=3 → all 3 eligible enter
        active.Should().BeEquivalentTo(new[] { "A", "C", "D" },
            "all 3 HealthState=Active nodes enter production (target=3)");
    }

    [Fact]
    public void GetProductionTags_StabilityVerificationNode_ExcludedFromProductionSelector()
    {
        // §5.4 Invariant I2: STABILITY_VERIFICATION nodes MUST NOT enter production selector.
        var nodes = new List<NodeState>
        {
            CreateNode("A", 80),
            CreateNode("B", 75),
            CreateNode("C", 70),
            CreateNode("D", 65),
        };
        nodes[1].SetHealthState(NodeHealthState.StabilityVerification);

        var mgr = new ActiveSetManager(nodes);
        var active = mgr.GetProductionTags();

        active.Should().NotContain("B",
            "STABILITY_VERIFICATION node must be excluded even when !IsInCooldown");
        // eligible=3 (A,C,D), target=3 → all 3 enter
        active.Should().BeEquivalentTo(new[] { "A", "C", "D" });
    }

    [Fact]
    public void HasActiveSetChanged_RecoveryProbingNodeScoreChange_ShouldNotTriggerChange()
    {
        // §5.4: RECOVERY_PROBING nodes are not in eligible pool → their score
        // changes MUST NOT cause HasActiveSetChanged() to return true.
        var nodes = new List<NodeState>
        {
            CreateNode("A", 80),
            CreateNode("B", 70),
            CreateNode("C", 65),
            CreateNode("D", 50),
        };
        nodes[3].SetHealthState(NodeHealthState.RecoveryProbing);

        var mgr = new ActiveSetManager(nodes);
        mgr.GetProductionTags(); // establish baseline
        mgr.Prime();

        // Oscillate D's score (it's in RECOVERY_PROBING, should be invisible)
        for (int i = 0; i < 5; i++)
        {
            double score = i % 2 == 0 ? 90 : 30;
            nodes[3].UpdateScore(100, 0.0, score, 0);
            bool changed = mgr.HasActiveSetChanged();
            changed.Should().BeFalse(
                $"RECOVERY_PROBING node D score change to {score} must not trigger active-set change (iter {i})");
        }
    }

    [Fact]
    public void GetProductionTags_HealthStateFailedButNotCooldown_ExcludedFromProductionSelector()
    {
        // Invariant I2 + III4: When cooldown is cleared (ResetCooldown) but
        // HealthState remains Failed, the node MUST NOT enter production selector.
        // This is the "manual cooldown clear" scenario — the node must go through
        // RECOVERY_PROBING → STABILITY_VERIFICATION → ACTIVE first.
        var nodes = new List<NodeState>
        {
            CreateNode("A", 80),
            CreateNode("B", 70),
            CreateNode("C", 90, inCooldown: true),
        };
        nodes[2].SetHealthState(NodeHealthState.Failed);
        nodes[2].ResetCooldown(); // manual clear: cooldown=gone, but HealthState=Failed

        var mgr = new ActiveSetManager(nodes);
        var active = mgr.GetProductionTags();

        active.Should().NotContain("C",
            "node with HealthState=Failed must not enter production selector even after ResetCooldown");
        active.Should().Contain(new[] { "A", "B" });
    }

    [Fact]
    public void GetProductionTags_AllActiveNodesButSomeInRecovery_OnlyActiveEligible()
    {
        // Eligible pool = HealthState=Active + !IsInCooldown only.
        // 5 nodes total: 2 Active, 2 RecoveryProbing, 1 StabilityVerification.
        // Only the 2 Active nodes should be eligible.
        var nodes = new List<NodeState>
        {
            CreateNode("A", 80), // Active, healthy
            CreateNode("B", 70), // Active, healthy
            CreateNode("C", 90), // → RecoveryProbing
            CreateNode("D", 85), // → StabilityVerification
            CreateNode("E", 60), // → RecoveryProbing
        };
        nodes[2].SetHealthState(NodeHealthState.RecoveryProbing);
        nodes[3].SetHealthState(NodeHealthState.StabilityVerification);
        nodes[4].SetHealthState(NodeHealthState.RecoveryProbing);

        var mgr = new ActiveSetManager(nodes);
        var active = mgr.GetProductionTags();

        // With only 2 eligible (A, B), topK formula says eligible <= 2 → return all
        active.Should().BeEquivalentTo(new[] { "A", "B" },
            "only Active-health nodes A and B should be eligible");
        active.Should().NotContain(new[] { "C", "D", "E" },
            "RecoveryProbing/StabilityVerification nodes must not enter production selector");
    }

    [Fact]
    public void IsEligiblePoolEmpty_AllNodesActive_ReturnsFalse()
    {
        var nodes = new List<NodeState>
        {
            CreateNode("A", 80),
            CreateNode("B", 70),
        };

        var mgr = new ActiveSetManager(nodes);
        mgr.GetProductionTags();

        mgr.IsEligiblePoolEmpty.Should().BeFalse("at least one Active node exists");
    }

    [Fact]
    public void IsEligiblePoolEmpty_AllNodesInRecovery_ReturnsTrue()
    {
        // All nodes HealthState != Active → eligible pool is empty → catastrophic
        var nodes = new List<NodeState>
        {
            CreateNode("A", 80),
            CreateNode("B", 70),
        };
        nodes[0].SetHealthState(NodeHealthState.RecoveryProbing);
        nodes[1].SetHealthState(NodeHealthState.StabilityVerification);

        var mgr = new ActiveSetManager(nodes);
        mgr.GetProductionTags();

        mgr.IsEligiblePoolEmpty.Should().BeTrue("no nodes have HealthState=Active");
    }

    [Fact]
    public void IsEligiblePoolEmpty_AllNodesCooldown_ReturnsTrue()
    {
        var nodes = new List<NodeState>
        {
            CreateNode("A", 80, inCooldown: true),
            CreateNode("B", 70, inCooldown: true),
        };
        nodes[0].SetHealthState(NodeHealthState.Failed);
        nodes[1].SetHealthState(NodeHealthState.Failed);

        var mgr = new ActiveSetManager(nodes);
        mgr.GetProductionTags();

        mgr.IsEligiblePoolEmpty.Should().BeTrue("cooldown nodes are not eligible");
    }

    [Fact]
    public void IsEligiblePoolEmpty_MixedActiveAndRecovery_ReturnsFalse()
    {
        var nodes = new List<NodeState>
        {
            CreateNode("A", 80),  // Active, healthy
            CreateNode("B", 70),  // Active, healthy
            CreateNode("C", 60),  // → RecoveryProbing
        };
        nodes[2].SetHealthState(NodeHealthState.RecoveryProbing);

        var mgr = new ActiveSetManager(nodes);
        mgr.GetProductionTags();

        mgr.IsEligiblePoolEmpty.Should().BeFalse("A and B are HealthState=Active");
    }

    [Fact]
    public void GetProductionTags_EmptyEligible_FallbackPrefersRecoveryProbingOverCooldown()
    {
        // §8.4 fallback priority: RECOVERY_PROBING (highest probe success) >
        // STABILITY_VERIFICATION > cooldown (shortest remaining)
        var now = DateTime.UtcNow;
        var nodes = new List<NodeState>
        {
            CreateNode("A", 80, inCooldown: true),  // cooldown, 5min
            CreateNode("B", 70, inCooldown: true),  // cooldown, 1min (shorter)
            CreateNode("C", 60),                     // RecoveryProbing, 2 successes
        };
        nodes[0].SetHealthState(NodeHealthState.Failed);
        nodes[1].SetHealthState(NodeHealthState.Failed);
        nodes[1].SetCooldown(now.AddMinutes(1));     // B has shorter cooldown
        nodes[2].SetHealthState(NodeHealthState.RecoveryProbing);
        nodes[2].IncrementRecoveryProbeSuccess();
        nodes[2].IncrementRecoveryProbeSuccess();    // C has 2 successful probes

        var mgr = new ActiveSetManager(nodes);
        var active = mgr.GetProductionTags();

        // C should be preferred (RecoveryProbing with 2 successes) over B (short cooldown)
        active.Should().ContainSingle().Which.Should().Be("C",
            "RecoveryProbing with probe successes should be preferred over cooldown nodes");
    }

    [Fact]
    public void GetProductionTags_EmptyEligible_FallbackPrefersStabilityVerificationOverCooldown()
    {
        var now = DateTime.UtcNow;
        var nodes = new List<NodeState>
        {
            CreateNode("A", 80, inCooldown: true),  // cooldown
            CreateNode("B", 70),                     // StabilityVerification (past basic reachability)
        };
        nodes[0].SetHealthState(NodeHealthState.Failed);
        nodes[1].SetHealthState(NodeHealthState.StabilityVerification);

        var mgr = new ActiveSetManager(nodes);
        var active = mgr.GetProductionTags();

        active.Should().ContainSingle().Which.Should().Be("B",
            "StabilityVerification should be preferred over cooldown-only nodes");
    }

    [Fact]
    public void GetProductionTags_EmptyEligible_AllCooldown_ReturnsShortest()
    {
        var now = DateTime.UtcNow;
        var nodes = new List<NodeState>
        {
            CreateNode("A", 80, inCooldown: true),
            CreateNode("B", 70, inCooldown: true),
        };
        nodes[0].SetHealthState(NodeHealthState.Failed);
        nodes[0].SetCooldown(now.AddMinutes(10));
        nodes[1].SetHealthState(NodeHealthState.Failed);
        nodes[1].SetCooldown(now.AddMinutes(3));

        var mgr = new ActiveSetManager(nodes);
        var active = mgr.GetProductionTags();

        active.Should().ContainSingle().Which.Should().Be("B",
            "when all nodes are in cooldown, shortest remaining cooldown should be selected");
    }
}
