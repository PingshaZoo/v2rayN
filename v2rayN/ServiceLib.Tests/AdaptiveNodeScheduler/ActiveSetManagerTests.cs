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
    public void GetActiveTags_AllBelowEntryButAboveExit_FallsBackToTopK()
    {
        // 5 nodes, all at score=55 (below Entry=60, above Exit=35).
        // No nodes qualify for sticky (Entry=60). No explorer in production
        // selector. Safety net activates: fall back to raw top-K by score
        // so the balancer is never empty.
        var nodes = new List<NodeState>
        {
            CreateNode("A", 55),
            CreateNode("B", 55),
            CreateNode("C", 55),
            CreateNode("D", 55),
            CreateNode("E", 55),
        };

        var mgr = new ActiveSetManager(nodes);
        var active = mgr.GetActiveTags();

        // topK = 4, safety net: top 4 by score
        active.Count.Should().Be(4,
            "safety net: when no nodes pass hysteresis, fall back to top-K by raw score");
    }

    [Fact]
    public void GetActiveTags_NodesAboveEntry_ShouldEnterStickySet()
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
        var active = mgr.GetActiveTags();

        // topK = max(2, ceil(5 * 2/3)) = 4
        // Pass Entry=60: A(75), B(70), C(65) → 3 in sticky
        // No explorer in production selector (§3.4 stability fix)
        active.Count.Should().Be(3, "topK(4) = 3 sticky, no explorer in production selector");

        // Sticky set must contain A, B, C
        active.Should().Contain(new[] { "A", "B", "C" },
            "nodes >= 60 must enter the sticky set");

        // D and E must NOT be in production selector (explorer receives probe traffic only)
        active.Should().NotContain(new[] { "D", "E" },
            "nodes below Entry=60 must earn their way in through sustained probe quality");
    }

    [Fact]
    public void GetActiveTags_StickyNodes_ShouldStayUntilBelowExitThreshold()
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
        var firstActive = mgr.GetActiveTags();
        firstActive.Should().Contain("A").And.Contain("B").And.Contain("C");

        // Now scores drop into [35, 60) zone but stay above Exit=35
        nodes.First(n => n.Tag == "A").UpdateScore(100, 0.0, 55, 0);
        nodes.First(n => n.Tag == "B").UpdateScore(100, 0.0, 45, 0);
        nodes.First(n => n.Tag == "C").UpdateScore(100, 0.0, 40, 0);
        // D stays at 50

        var secondActive = mgr.GetActiveTags();

        // A, B, C are sticky (were in active set, score >= Exit=35) → MUST stay
        secondActive.Should().Contain(new[] { "A", "B", "C" },
            "previously active nodes at score 40-55 must stay (sticky, >= Exit=35)");
    }

    [Fact]
    public void GetActiveTags_NodeDropsBelowExit_ShouldBeEjected()
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
        var firstActive = mgr.GetActiveTags();

        // topK = max(2, ceil(4 * 2/3)) = 3
        // Pass Entry=60: A(80), B(70) → 2 sticky
        // C(50) < 60, D(30) < 35 → C is in explorer pool, D is excluded entirely
        firstActive.Should().Contain(new[] { "A", "B" });
        firstActive.Should().NotContain("D", "D at 30 < Exit=35, cannot even be explorer");

        // Now C drops to 30 (below Exit=35). If C was in active (as explorer), it should be ejected.
        // Force C in sticky set first by raising its score above 60.
        nodes.First(n => n.Tag == "C").UpdateScore(100, 0.0, 65, 0);
        mgr.GetActiveTags(); // establishes C in sticky

        // Now drop C to 30
        nodes.First(n => n.Tag == "C").UpdateScore(100, 0.0, 30, 0);
        var secondActive = mgr.GetActiveTags();

        secondActive.Should().NotContain("C",
            "C dropped to 30 < Exit=35, should be ejected from sticky set");
        secondActive.Should().Contain(new[] { "A", "B" },
            "A and B should remain in sticky set");
    }

    [Fact]
    public void GetActiveTags_ScoreOscillatingInHysteresisZone_ShouldNotFlipFlopStickySet()
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

            var active = mgr.GetActiveTags();

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
        mgr.GetActiveTags(); // establish _currentActiveSet = sticky {A,B}
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
    public void HasActiveSetChanged_NodeCrossesEntryThreshold_ShouldTriggerChange()
    {
        var nodes = new List<NodeState>
        {
            CreateNode("A", 80),
            CreateNode("B", 70),
            CreateNode("C", 50), // below entry
            CreateNode("D", 48),
            CreateNode("E", 42),
        };

        var mgr = new ActiveSetManager(nodes);
        mgr.GetActiveTags(); // establish baseline sticky set {A, B}
        mgr.Prime();

        // C rises to 65 — crosses Entry=60 → sticky set changes from {A,B} to {A,B,C}
        nodes.First(n => n.Tag == "C").UpdateScore(100, 0.0, 65, 0);
        bool changed = mgr.HasActiveSetChanged();
        changed.Should().BeTrue("crossing Entry=60 should trigger sticky set change");
    }

    [Fact]
    public void GetActiveTags_CooldownNode_AlwaysExcluded()
    {
        var nodes = new List<NodeState>
        {
            CreateNode("A", 80),
            CreateNode("B", 75),
            CreateNode("C", 90, inCooldown: true), // high score but cooldown
            CreateNode("D", 50),
        };

        var mgr = new ActiveSetManager(nodes);
        var active = mgr.GetActiveTags();

        active.Should().NotContain("C",
            "cooldown nodes must be excluded regardless of score");
        active.Should().Contain(new[] { "A", "B" },
            "A and B pass Entry=60, should be in sticky set");
    }

    [Fact]
    public void GetActiveTags_TwoOrFewerEligible_ReturnsAll()
    {
        var nodes = new List<NodeState>
        {
            CreateNode("A", 80),
            CreateNode("B", 30),
        };

        var mgr = new ActiveSetManager(nodes);
        var active = mgr.GetActiveTags();

        active.Should().Contain(new[] { "A", "B" },
            "with <= 2 eligible, all returned regardless of score");
    }

    [Fact]
    public void GetActiveTags_AllNodesCooldown_ReturnsShortestCooldown()
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
        var active = mgr.GetActiveTags();

        // Returns the node with shortest cooldown (B), not empty
        active.Should().ContainSingle().Which.Should().Be("B",
            "all cooldown → fallback to shortest remaining cooldown");
    }

    [Fact]
    public void GetActiveTags_NodesBelowExitThreshold_ShouldBeExcluded()
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
        var active = mgr.GetActiveTags();

        active.Should().NotContain(new[] { "C", "D" },
            "nodes below Exit=35 must not appear in active set, even as explorer");
        active.Should().Contain(new[] { "A", "B" });
    }
}
