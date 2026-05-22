using AwesomeAssertions;
using ServiceLib.Handler.AdaptiveNodeScheduler;
using Xunit;

namespace ServiceLib.Tests.AdaptiveNodeScheduler;

/// <summary>
/// P2.2: Verifies boundary handling for 1, 2, and 3+ node groups.
/// - 1 node: cooldown disabled, active set always returns the node.
/// - 2 nodes: at most 1 cooldown, both eligible nodes remain active.
/// - 3+ nodes: standard cooldown cap at floor(N/3).
/// </summary>
public class BoundaryNodeCountTests
{
    private static NodeState CreateNode(string tag, double score = 50, bool inCooldown = false)
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

    // ── Cooldown cap ──────────────────────────────────────────

    [Theory]
    [InlineData(1, 0)]
    [InlineData(2, 1)]
    [InlineData(3, 1)]
    [InlineData(4, 1)]
    [InlineData(5, 1)]
    [InlineData(6, 2)]
    [InlineData(9, 3)]
    [InlineData(10, 3)]
    [InlineData(12, 4)]
    [InlineData(20, 6)]
    [InlineData(30, 10)]
    public void ComputeMaxCooldown_MatchesBoundaryRules(int nodeCount, int expectedMax)
    {
        CooldownFsm.ComputeMaxCooldown(nodeCount).Should().Be(expectedMax);
    }

    [Fact]
    public void TryEnterCooldown_SingleNode_NeverEntersCooldown()
    {
        var nodes = new List<NodeState> { CreateNode("A") };
        // Set 3 consecutive failures via UpdateScore
        nodes[0].UpdateScore(5000, 1.0, 30.0, 3);

        var fsm = new CooldownFsm();
        fsm.TryEnterCooldown(nodes[0], nodes);

        nodes[0].IsInCooldown.Should().BeFalse(
            "single node should never enter cooldown — nothing to route through otherwise");
    }

    [Fact]
    public void TryEnterCooldown_TwoNodes_MaxOneCooldown()
    {
        var nodes = new List<NodeState>
        {
            CreateNode("A"),
            CreateNode("B"),
        };

        // Node A: 2 consecutive failures → enters cooldown
        nodes[0].UpdateScore(5000, 1.0, 30.0, 2);
        var fsm = new CooldownFsm();
        fsm.TryEnterCooldown(nodes[0], nodes);
        nodes[0].IsInCooldown.Should().BeTrue("A with 2 failures should enter cooldown (max=1)");

        // Node B: also 2 consecutive failures — but max=1 already occupied
        nodes[1].UpdateScore(5000, 1.0, 30.0, 2);
        fsm.TryEnterCooldown(nodes[1], nodes);
        nodes[1].IsInCooldown.Should().BeFalse(
            "B cannot enter cooldown when A is already in (max=1 for 2 nodes)");
    }

    [Fact]
    public void TryEnterCooldown_ThreeNodes_MaxOneCooldown()
    {
        // N=3: maxAllowed = max(1, floor(3/3)) = 1
        var nodes = new List<NodeState>
        {
            CreateNode("A"),
            CreateNode("B"),
            CreateNode("C"),
        };

        var fsm = new CooldownFsm();

        // First node enters
        nodes[0].UpdateScore(5000, 1.0, 30.0, 2);
        fsm.TryEnterCooldown(nodes[0], nodes);
        nodes[0].IsInCooldown.Should().BeTrue("A enters cooldown (max=1)");

        // Second node tries — max=1 already taken
        nodes[1].UpdateScore(5000, 1.0, 30.0, 2);
        fsm.TryEnterCooldown(nodes[1], nodes);
        nodes[1].IsInCooldown.Should().BeFalse("B cannot enter (max=1 already occupied)");
    }

    [Fact]
    public void TryEnterCooldown_SixNodes_MaxTwoCooldowns()
    {
        // N=6: maxAllowed = max(1, floor(6/3)) = 2
        var nodes = new List<NodeState>();
        for (int i = 0; i < 6; i++)
            nodes.Add(CreateNode($"N{i}"));

        var fsm = new CooldownFsm();

        // Enter 2 nodes into cooldown
        for (int i = 0; i < 2; i++)
        {
            nodes[i].UpdateScore(5000, 1.0, 30.0, 2);
            fsm.TryEnterCooldown(nodes[i], nodes);
            nodes[i].IsInCooldown.Should().BeTrue($"N{i} should enter (slot {i + 1}/2)");
        }

        // Third node tries — should fail (max=2)
        nodes[2].UpdateScore(5000, 1.0, 30.0, 2);
        fsm.TryEnterCooldown(nodes[2], nodes);
        nodes[2].IsInCooldown.Should().BeFalse("N2 cannot enter (max=2 already occupied)");
    }

    // ── ActiveSetManager boundary cases ───────────────────────

    [Fact]
    public void GetActiveTags_SingleNode_ReturnsItRegardlessOfScore()
    {
        // 1 node with score=50 (below Entry=60) — should still be returned
        var nodes = new List<NodeState> { CreateNode("A", score: 50) };
        var mgr = new ActiveSetManager(nodes);
        var active = mgr.GetActiveTags();

        active.Should().ContainSingle().Which.Should().Be("A",
            "single node always returned (eligible <= 2 early-return path)");
    }

    [Fact]
    public void GetActiveTags_SingleNodeInCooldown_ReturnsItAsLastResort()
    {
        // All nodes in cooldown → falls back to shortest remaining cooldown.
        // With only 1 node, that node is the fallback — the balancer must not
        // be empty, or xray has nothing to route through.
        var nodes = new List<NodeState> { CreateNode("A", score: 50, inCooldown: true) };
        var mgr = new ActiveSetManager(nodes);
        var active = mgr.GetActiveTags();

        active.Should().ContainSingle().Which.Should().Be("A",
            "single cooldown node → only option, must be returned as fallback");
    }

    [Fact]
    public void GetActiveTags_TwoNodes_BothReturned()
    {
        // 2 eligible nodes → both returned regardless of score (eligible <= 2 path)
        var nodes = new List<NodeState> { CreateNode("A", score: 80), CreateNode("B", score: 30) };
        var mgr = new ActiveSetManager(nodes);
        var active = mgr.GetActiveTags();

        active.Count.Should().Be(2);
        active.Should().Contain(new[] { "A", "B" });
    }

    [Fact]
    public void GetActiveTags_TwoNodes_OneInCooldown_OtherStillReturned()
    {
        var nodes = new List<NodeState>
        {
            CreateNode("A", score: 80),
            CreateNode("B", score: 30, inCooldown: true),
        };
        var mgr = new ActiveSetManager(nodes);

        // B in cooldown → only A is eligible → eligible.Count=1 → early return
        var active = mgr.GetActiveTags();
        active.Should().ContainSingle().Which.Should().Be("A");
    }

    [Fact]
    public void GetActiveTags_ThreeNodes_NormalHysteresisApplies()
    {
        // N=3: topK = max(2, ceil(3*2/3)) = 2
        var nodes = new List<NodeState>
        {
            CreateNode("A", score: 80),
            CreateNode("B", score: 70),
            CreateNode("C", score: 30),
        };
        var mgr = new ActiveSetManager(nodes);
        var active = mgr.GetActiveTags();

        // A and B >= 60 enter sticky. C at 30 < 35 can't even be explorer.
        active.Should().Contain("A");
        active.Should().Contain("B");
        active.Should().NotContain("C", "C at 30 < Exit=35, not eligible for explorer");
    }

    // ── All-nodes-cooldown fallback (§8.1 criterion 1) ─────────

    [Fact]
    public void AllNodesInCooldown_ReturnsShortestRemainingCooldown()
    {
        var now = DateTime.UtcNow;
        var nodes = new List<NodeState>
        {
            CreateNode("A", score: 30), // cooldown ends in 5 min
            CreateNode("B", score: 25), // cooldown ends in 2 min ← shortest
            CreateNode("C", score: 20), // cooldown ends in 10 min
        };
        nodes[0].SetCooldown(now.AddMinutes(5));
        nodes[1].SetCooldown(now.AddMinutes(2));
        nodes[2].SetCooldown(now.AddMinutes(10));

        var mgr = new ActiveSetManager(nodes);
        var active = mgr.GetActiveTags();

        active.Should().ContainSingle().Which.Should().Be("B",
            "B has the shortest remaining cooldown (2 min) — best recovery candidate");
    }

    [Fact]
    public void AllNodesInCooldown_DoesNotCrash()
    {
        var nodes = new List<NodeState>();
        for (int i = 0; i < 10; i++)
        {
            var n = CreateNode($"N{i}", score: 20);
            n.SetCooldown(DateTime.UtcNow.AddMinutes(i + 1));
            nodes.Add(n);
        }

        var mgr = new ActiveSetManager(nodes);
        var active = mgr.GetActiveTags();

        // Must return at least one node — N0 has shortest cooldown (1 min)
        active.Should().NotBeEmpty("balancer must never have an empty selector");
        active.Should().Contain("N0", "N0 has shortest cooldown");
    }

    [Fact]
    public void AllNodesInCooldown_TwoNodes_SameCooldown_ReturnsEither()
    {
        var now = DateTime.UtcNow;
        var nodes = new List<NodeState>
        {
            CreateNode("A", score: 30),
            CreateNode("B", score: 25),
        };
        nodes[0].SetCooldown(now.AddMinutes(3));
        nodes[1].SetCooldown(now.AddMinutes(3));

        var mgr = new ActiveSetManager(nodes);
        var active = mgr.GetActiveTags();

        active.Should().ContainSingle();
        active[0].Should().BeOneOf(["A", "B"],
            "equal cooldown → either is fine, determinism not required");
    }

    [Fact]
    public void AllNodesInCooldown_ChangeDetection_FirstChangeDetected_SecondStable()
    {
        var now = DateTime.UtcNow;
        var nodes = new List<NodeState>
        {
            CreateNode("A", score: 80),
            CreateNode("B", score: 30),
        };

        // Start with both nodes NOT in cooldown — normal baseline
        var mgr = new ActiveSetManager(nodes);
        mgr.Prime(); // baseline: {A, B}

        // Now put both in cooldown — should detect the transition
        nodes[0].SetCooldown(now.AddMinutes(5));
        nodes[1].SetCooldown(now.AddMinutes(2));

        var changed1 = mgr.HasActiveSetChanged();
        changed1.Should().BeTrue("transition from normal ({A,B}) to all-cooldown ({B}) is a change");

        // Second call with same state → stable
        var changed2 = mgr.HasActiveSetChanged();
        changed2.Should().BeFalse("same all-cooldown state → no spurious reload");

        // Third call → still stable
        var changed3 = mgr.HasActiveSetChanged();
        changed3.Should().BeFalse("continued stable state");
    }
}
