using AwesomeAssertions;
using ServiceLib.Handler.AdaptiveNodeScheduler;
using Xunit;

namespace ServiceLib.Tests.AdaptiveNodeScheduler;

/// <summary>
/// P1#5: Tests for DNS attribution separation (§13).
///
/// Verifies that DnsResolutionFailure and DnsPoisoningSuspected receive
/// zero penalty and do not trigger cooldown — DNS failures ≠ node failures.
/// </summary>
public class DnsAttributionTests
{
    private static NodeState CreateNode(string tag = "test-node", double score = 80)
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

    // ── GetPenalty: zero-penalty for DNS failure types ───────────

    [Fact]
    public void GetPenalty_DnsResolutionFailure_ReturnsZeroPenalty()
    {
        var node = CreateNode();
        var (loss, lat) = FailureCollector.GetPenalty(FailureType.DnsResolutionFailure, node);

        loss.Should().Be(0.0, "DNS resolution failure is not a node quality signal");
        lat.Should().Be(node.EwmaLatencyMs,
            "DNS failure leaves latency unchanged");
    }

    [Fact]
    public void GetPenalty_DnsPoisoningSuspected_ReturnsZeroPenalty()
    {
        var node = CreateNode();
        var (loss, lat) = FailureCollector.GetPenalty(FailureType.DnsPoisoningSuspected, node);

        loss.Should().Be(0.0, "DNS poisoning is not a node quality signal");
        lat.Should().Be(node.EwmaLatencyMs,
            "DNS poisoning leaves latency unchanged");
    }

    // ── RecordFailure: DNS failures are no-ops ────────────────────

    [Fact]
    public void RecordFailure_DnsResolutionFailure_ShouldBeNoOp()
    {
        var scorer = new ScoreCalculator();
        var cooldown = new CooldownFsm();
        var collector = new FailureCollector(scorer, cooldown);

        var node = CreateNode();
        double scoreBefore = node.Score;
        double latencyBefore = node.EwmaLatencyMs;
        double lossBefore = node.EwmaLossRate;
        int failsBefore = node.ConsecutiveFailures;

        collector.RecordFailure(node, FailureType.DnsResolutionFailure, [node]);

        node.Score.Should().BeApproximately(scoreBefore, 0.01,
            "DNS failure must not change score");
        node.EwmaLatencyMs.Should().BeApproximately(latencyBefore, 0.01);
        node.EwmaLossRate.Should().BeApproximately(lossBefore, 0.01);
        node.ConsecutiveFailures.Should().Be(failsBefore,
            "DNS failure must not increment consecutive failures");
        node.IsInCooldown.Should().BeFalse(
            "DNS failure must never trigger cooldown");
    }

    [Fact]
    public void RecordFailure_DnsPoisoningSuspected_ShouldBeNoOp()
    {
        var scorer = new ScoreCalculator();
        var cooldown = new CooldownFsm();
        var collector = new FailureCollector(scorer, cooldown);

        var node = CreateNode();
        double scoreBefore = node.Score;
        double latencyBefore = node.EwmaLatencyMs;
        double lossBefore = node.EwmaLossRate;
        int failsBefore = node.ConsecutiveFailures;

        collector.RecordFailure(node, FailureType.DnsPoisoningSuspected, [node]);

        node.Score.Should().BeApproximately(scoreBefore, 0.01,
            "DNS poisoning must not change score");
        node.EwmaLatencyMs.Should().BeApproximately(latencyBefore, 0.01);
        node.EwmaLossRate.Should().BeApproximately(lossBefore, 0.01);
        node.ConsecutiveFailures.Should().Be(failsBefore,
            "DNS poisoning must not increment consecutive failures");
        node.IsInCooldown.Should().BeFalse(
            "DNS poisoning must never trigger cooldown");
    }

    // ── Contrast: DNS failure vs real failure ─────────────────────

    [Fact]
    public void RecordFailure_DnsVsTimeout_OnlyTimeoutPenalizes()
    {
        var scorer = new ScoreCalculator();
        var cooldown = new CooldownFsm();
        var collector = new FailureCollector(scorer, cooldown);

        var nodeDns = CreateNode("dns-node", score: 80);
        var nodeTimeout = CreateNode("timeout-node", score: 80);

        collector.RecordFailure(nodeDns, FailureType.DnsResolutionFailure, [nodeDns, nodeTimeout]);
        collector.RecordFailure(nodeTimeout, FailureType.Timeout, [nodeDns, nodeTimeout]);

        // DNS failure node: unchanged
        nodeDns.Score.Should().BeApproximately(80, 1.0,
            "DNS failure leaves score unchanged");
        nodeDns.IsInCooldown.Should().BeFalse();

        // Timeout node: penalized
        nodeTimeout.Score.Should().BeLessThan(70,
            "Timeout failure reduces score");
        nodeTimeout.ConsecutiveFailures.Should().Be(1);
    }

    [Fact]
    public void RepeatedDnsFailures_NeverEnterCooldown()
    {
        var scorer = new ScoreCalculator();
        var cooldown = new CooldownFsm();
        var collector = new FailureCollector(scorer, cooldown);

        var node = CreateNode();
        var nodes = new[] { node };

        for (int i = 0; i < 10; i++)
        {
            collector.RecordFailure(node, FailureType.DnsResolutionFailure, nodes);
        }

        node.ConsecutiveFailures.Should().Be(0,
            "DNS failures never count as consecutive failures");
        node.IsInCooldown.Should().BeFalse(
            "Even 10 DNS failures must not trigger cooldown");
        node.Score.Should().BeApproximately(80, 0.01,
            "Score unchanged after repeated DNS failures");
    }

    // ── Mixed failure types ───────────────────────────────────────

    [Fact]
    public void DnsFailureThenRealFailure_RealFailureStillPenalized()
    {
        var scorer = new ScoreCalculator();
        var cooldown = new CooldownFsm();
        var collector = new FailureCollector(scorer, cooldown);

        var node = CreateNode(score: 80);
        var nodes = new[] { node };

        // DNS failures first — no penalty
        collector.RecordFailure(node, FailureType.DnsResolutionFailure, nodes);
        collector.RecordFailure(node, FailureType.DnsPoisoningSuspected, nodes);
        node.ConsecutiveFailures.Should().Be(0);

        // Then a real timeout — penalty applies
        collector.RecordFailure(node, FailureType.Timeout, nodes);
        node.ConsecutiveFailures.Should().Be(1,
            "Real failure is still penalized after DNS failures");
    }

    [Fact]
    public void RealFailureThenDnsFailure_ConsecutiveFailuresPreserved()
    {
        var scorer = new ScoreCalculator();
        var cooldown = new CooldownFsm();
        var collector = new FailureCollector(scorer, cooldown);

        var node = CreateNode(score: 80);
        var nodes = new[] { node };

        // First a real timeout
        collector.RecordFailure(node, FailureType.Timeout, nodes);
        int failsAfterTimeout = node.ConsecutiveFailures;

        // Then a DNS failure — existing consecutive failures unchanged
        collector.RecordFailure(node, FailureType.DnsResolutionFailure, nodes);
        node.ConsecutiveFailures.Should().Be(failsAfterTimeout,
            "DNS failure doesn't reset or increment existing failure count");
    }

    // ── DNS failures don't feed into GlobalFreeze ─────────────────

    [Fact]
    public void DnsFailure_DoesNotFeedGlobalFreeze()
    {
        var scorer = new ScoreCalculator();
        var cooldown = new CooldownFsm();
        var clock = new FakeClock();
        var freeze = new GlobalFreezeController(clock);
        var collector = new FailureCollector(scorer, cooldown, null, freeze);

        var node = CreateNode();

        // DNS failures should NOT be recorded in freeze controller
        // (verified indirectly: 10 DNS failures won't trigger freeze)
        for (int i = 0; i < 10; i++)
        {
            collector.RecordFailure(node, FailureType.DnsResolutionFailure, [node]);
        }

        // A subsequent evaluation with the single node should show Normal, not freeze
        var decision = freeze.Evaluate(["some-active-node"]);
        decision.Type.Should().Be(FreezeDecisionType.Allow,
            "DNS failures must not feed into freeze controller");
    }
}
