using AwesomeAssertions;
using ServiceLib.Handler.AdaptiveNodeScheduler;
using Xunit;

namespace ServiceLib.Tests.AdaptiveNodeScheduler;

/// <summary>
/// P3.2: Verifies SchedulingQualityMetrics computation.
/// Tests entropy, P95 latency, mean, stddev, and edge cases.
/// </summary>
public class SchedulingQualityMetricsTests
{
    private static NodeState CreateNode(string tag, double score, double latencyMs)
    {
        var node = new NodeState
        {
            Tag = tag,
            Host = "127.0.0.1",
            Port = 1080,
            Protocol = ProxyProtocol.Tcp,
            ChildIndexId = tag,
        };
        node.UpdateScore(latencyMs, 0.05, score, 0);
        return node;
    }

    // ── Basic computation ──────────────────────────────────────

    [Fact]
    public void EmptyNodes_ReturnsZeroes()
    {
        var snapshot = SchedulingQualityMetrics.Compute(Array.Empty<NodeState>());

        snapshot.Entropy.Should().Be(0);
        snapshot.P95LatencyMs.Should().Be(0);
        snapshot.MeanScore.Should().Be(0);
        snapshot.ScoreStdDev.Should().Be(0);
        snapshot.ActiveNodeCount.Should().Be(0);
        snapshot.CooldownNodeCount.Should().Be(0);
    }

    [Fact]
    public void SingleNode_P95IsItsLatency()
    {
        var nodes = new[] { CreateNode("A", score: 80, latencyMs: 120) };

        var snapshot = SchedulingQualityMetrics.Compute(nodes);

        snapshot.P95LatencyMs.Should().Be(120, "P95 of single value is that value");
        snapshot.ActiveNodeCount.Should().Be(1);
        snapshot.ScoreStdDev.Should().Be(0, "single node → no deviation");
    }

    [Fact]
    public void MeanScore_AverageOfScores()
    {
        var nodes = new[]
        {
            CreateNode("A", score: 90, latencyMs: 100),
            CreateNode("B", score: 60, latencyMs: 200),
            CreateNode("C", score: 30, latencyMs: 500),
        };

        var snapshot = SchedulingQualityMetrics.Compute(nodes);

        snapshot.MeanScore.Should().BeApproximately(60.0, 0.01);
    }

    [Fact]
    public void StdDev_ReflectsScoreDispersion()
    {
        var tightNodes = new[]
        {
            CreateNode("A", score: 80, latencyMs: 100),
            CreateNode("B", score: 82, latencyMs: 110),
            CreateNode("C", score: 78, latencyMs: 105),
        };

        var wideNodes = new[]
        {
            CreateNode("A", score: 90, latencyMs: 50),
            CreateNode("B", score: 50, latencyMs: 500),
            CreateNode("C", score: 10, latencyMs: 2000),
        };

        var tight = SchedulingQualityMetrics.Compute(tightNodes);
        var wide = SchedulingQualityMetrics.Compute(wideNodes);

        wide.ScoreStdDev.Should().BeGreaterThan(tight.ScoreStdDev,
            "wide score spread → higher standard deviation");
    }

    // ── P95 latency ────────────────────────────────────────────

    [Fact]
    public void P95Latency_10Nodes_ReturnsSecondHighest()
    {
        var nodes = new List<NodeState>();
        for (int i = 1; i <= 10; i++)
            nodes.Add(CreateNode($"N{i}", score: 50, latencyMs: i * 10)); // 10, 20, ..., 100

        var snapshot = SchedulingQualityMetrics.Compute(nodes);

        // Sorted latencies: 10, 20, …, 100 → 10 values
        // P95 index = ceil(10 * 0.95) - 1 = 10 - 1 = 9 → value at index 9 = 100
        snapshot.P95LatencyMs.Should().Be(100);
    }

    [Fact]
    public void P95Latency_20Nodes_CorrectPercentile()
    {
        var nodes = new List<NodeState>();
        for (int i = 1; i <= 20; i++)
            nodes.Add(CreateNode($"N{i}", score: 50, latencyMs: i * 10)); // 10, 20, …, 200

        var snapshot = SchedulingQualityMetrics.Compute(nodes);

        // ceil(20 * 0.95) - 1 = 19 - 1 = 18 → index 18 = value 190
        snapshot.P95LatencyMs.Should().Be(190);
    }

    [Fact]
    public void P95Latency_UnsortedInput_StillCorrect()
    {
        var nodes = new[]
        {
            CreateNode("A", score: 50, latencyMs: 500),
            CreateNode("B", score: 50, latencyMs: 100),
            CreateNode("C", score: 50, latencyMs: 300),
            CreateNode("D", score: 50, latencyMs: 200),
            CreateNode("E", score: 50, latencyMs: 400),
        };

        var snapshot = SchedulingQualityMetrics.Compute(nodes);

        // Sorted: 100, 200, 300, 400, 500 → P95 index = ceil(5*0.95)-1 = 5-1 = 4 → 500
        snapshot.P95LatencyMs.Should().Be(500);
    }

    // ── Entropy ────────────────────────────────────────────────

    [Fact]
    public void UniformScores_HigherEntropyThanDominant()
    {
        var uniform = new[]
        {
            CreateNode("A", score: 50, latencyMs: 100),
            CreateNode("B", score: 50, latencyMs: 100),
            CreateNode("C", score: 50, latencyMs: 100),
            CreateNode("D", score: 50, latencyMs: 100),
        };

        var dominant = new[]
        {
            CreateNode("A", score: 97, latencyMs: 50),
            CreateNode("B", score: 1, latencyMs: 2000),
            CreateNode("C", score: 1, latencyMs: 2000),
            CreateNode("D", score: 1, latencyMs: 2000),
        };

        var uniformSnap = SchedulingQualityMetrics.Compute(uniform);
        var dominantSnap = SchedulingQualityMetrics.Compute(dominant);

        uniformSnap.Entropy.Should().BeGreaterThan(dominantSnap.Entropy,
            "uniform scores → maximum entropy; dominant node → low entropy");
    }

    [Fact]
    public void NormalizedEntropy_UniformScores_CloseToOne()
    {
        var nodes = new[]
        {
            CreateNode("A", score: 50, latencyMs: 100),
            CreateNode("B", score: 50, latencyMs: 100),
            CreateNode("C", score: 50, latencyMs: 100),
            CreateNode("D", score: 50, latencyMs: 100),
        };

        var snapshot = SchedulingQualityMetrics.Compute(nodes);

        // Perfectly uniform → normalized entropy ≈ 1.0
        snapshot.NormalizedEntropy.Should().BeApproximately(1.0, 0.001);
    }

    [Fact]
    public void NormalizedEntropy_SingleNode_IsZero()
    {
        var nodes = new[] { CreateNode("A", score: 80, latencyMs: 100) };

        var snapshot = SchedulingQualityMetrics.Compute(nodes);

        snapshot.NormalizedEntropy.Should().Be(0, "single node → max possible entropy = 0");
    }

    [Fact]
    public void TwoNodes_SameScore_PerfectEntropy()
    {
        var nodes = new[]
        {
            CreateNode("A", score: 75, latencyMs: 100),
            CreateNode("B", score: 75, latencyMs: 100),
        };

        var snapshot = SchedulingQualityMetrics.Compute(nodes);

        snapshot.NormalizedEntropy.Should().BeApproximately(1.0, 0.001,
            "N=2, equal scores → entropy = log2(2) = 1.0 → normalized = 1.0");
    }

    // ── Cooldown counts ────────────────────────────────────────

    [Fact]
    public void CooldownNodes_CountedCorrectly()
    {
        var a = CreateNode("A", score: 80, latencyMs: 100);
        var b = CreateNode("B", score: 30, latencyMs: 2000);
        var c = CreateNode("C", score: 60, latencyMs: 150);

        b.SetCooldown(DateTime.UtcNow.AddMinutes(5)); // B → cooldown

        var snapshot = SchedulingQualityMetrics.Compute(new[] { a, b, c });

        snapshot.ActiveNodeCount.Should().Be(2);
        snapshot.CooldownNodeCount.Should().Be(1);
    }

    [Fact]
    public void AllNodesInCooldown_CountsAllAsCooldown()
    {
        var a = CreateNode("A", score: 40, latencyMs: 1000);
        var b = CreateNode("B", score: 30, latencyMs: 2000);

        a.SetCooldown(DateTime.UtcNow.AddMinutes(5));
        b.SetCooldown(DateTime.UtcNow.AddMinutes(5));

        var snapshot = SchedulingQualityMetrics.Compute(new[] { a, b });

        snapshot.ActiveNodeCount.Should().Be(0);
        snapshot.CooldownNodeCount.Should().Be(2);
    }

    // ── Struct equality ────────────────────────────────────────

    [Fact]
    public void QualitySnapshot_SameValues_AreEqual()
    {
        var s1 = new SchedulingQualityMetrics.QualitySnapshot(1.5, 200, 60, 15, 5, 1, DateTime.MinValue);
        var s2 = new SchedulingQualityMetrics.QualitySnapshot(1.5, 200, 60, 15, 5, 1, DateTime.MinValue);

        s1.Should().Be(s2);
    }

    [Fact]
    public void QualitySnapshot_DifferentValues_AreNotEqual()
    {
        var s1 = new SchedulingQualityMetrics.QualitySnapshot(1.5, 200, 60, 15, 5, 1, DateTime.MinValue);
        var s2 = new SchedulingQualityMetrics.QualitySnapshot(1.2, 180, 55, 12, 4, 1, DateTime.MinValue);

        s1.Should().NotBe(s2);
    }
}
