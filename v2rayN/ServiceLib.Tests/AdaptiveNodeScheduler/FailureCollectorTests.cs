using AwesomeAssertions;
using ServiceLib.Handler.AdaptiveNodeScheduler;
using Xunit;

namespace ServiceLib.Tests.AdaptiveNodeScheduler;

/// <summary>
/// P0.2: Verifies differentiated penalty per FailureType.
/// TlsError must NOT penalize EWMA — it's a config error, not a network quality signal.
/// </summary>
public class FailureCollectorTests
{
    private static NodeState CreateNode(string tag = "test-node", double score = 80)
    {
        var node = new NodeState
        {
            Tag = tag,
            Host = "127.0.0.1",
            Port = 1080,
            Protocol = ProxyProtocol.Tcp,
            ChildIndexId = tag,
        };
        // seed with known score
        node.UpdateScore(100, 0.0, score, 0);
        return node;
    }

    [Fact]
    public void GetPenalty_Refused_ShouldReturnMaxPenalty()
    {
        var node = CreateNode();
        var (loss, lat) = FailureCollector.GetPenalty(FailureType.Refused, node);
        loss.Should().Be(1.0, "Refused means port is down — strongest signal");
        lat.Should().Be(10_000, "penalty latency should be 10s for hard failures");
    }

    [Fact]
    public void GetPenalty_Timeout_ShouldReturnHighPenalty()
    {
        var node = CreateNode();
        var (loss, lat) = FailureCollector.GetPenalty(FailureType.Timeout, node);
        loss.Should().Be(0.8, "Timeout may be GFW or slow node — medium-strong");
        lat.Should().Be(10_000);
    }

    [Fact]
    public void GetPenalty_NetworkError_ShouldReturnMediumPenalty()
    {
        var node = CreateNode();
        var (loss, lat) = FailureCollector.GetPenalty(FailureType.NetworkError, node);
        loss.Should().Be(0.7);
        lat.Should().Be(10_000);
    }

    [Fact]
    public void GetPenalty_UnexpectedEof_ShouldReturnWeakPenalty()
    {
        var node = CreateNode();
        node.UpdateScore(200, 0.0, 80, 0); // set known latency
        var (loss, lat) = FailureCollector.GetPenalty(FailureType.UnexpectedEof, node);
        loss.Should().Be(0.4, "mid-connection disconnect is a weak signal");
        lat.Should().BeGreaterThan(0).And.BeLessThan(10_000,
            "UnexpectedEof latency penalty should use inflated node latency, not 10s");
    }

    [Fact]
    public void GetPenalty_TlsError_ShouldReturnZeroPenalty()
    {
        var node = CreateNode();
        node.UpdateScore(150, 0.0, 75, 0);
        var (loss, lat) = FailureCollector.GetPenalty(FailureType.TlsError, node);
        loss.Should().Be(0.0, "TlsError is config error — must not penalize EWMA");
        lat.Should().Be(node.EwmaLatencyMs,
            "TlsError should leave latency unchanged");
    }

    [Fact]
    public void GetPenalty_Unknown_ShouldReturnDefaultPenalty()
    {
        var node = CreateNode();
        var (loss, lat) = FailureCollector.GetPenalty((FailureType)999, node);
        loss.Should().Be(0.5);
        lat.Should().Be(10_000);
    }

    [Fact]
    public void RecordFailure_TlsError_ShouldBeNoOp()
    {
        var scorer = new ScoreCalculator();
        var cooldown = new CooldownFsm();
        var collector = new FailureCollector(scorer, cooldown);

        var node = CreateNode();
        double scoreBefore = node.Score;
        double latencyBefore = node.EwmaLatencyMs;
        double lossBefore = node.EwmaLossRate;
        int failsBefore = node.ConsecutiveFailures;

        // TlsError is a config error — must NOT change score, EWMA, or consecutive failures
        collector.RecordFailure(node, FailureType.TlsError, [node]);

        node.Score.Should().BeApproximately(scoreBefore, 0.01,
            "TlsError should not change score");
        node.EwmaLatencyMs.Should().BeApproximately(latencyBefore, 0.01);
        node.EwmaLossRate.Should().BeApproximately(lossBefore, 0.01);
        node.ConsecutiveFailures.Should().Be(failsBefore,
            "TlsError should not increment consecutive failures");
        node.IsInCooldown.Should().BeFalse(
            "TlsError should never trigger cooldown");
    }

    [Fact]
    public void RecordFailure_Timeout_ShouldDecreaseScoreSubstantially()
    {
        var scorer = new ScoreCalculator();
        var cooldown = new CooldownFsm();
        var collector = new FailureCollector(scorer, cooldown);

        var node = CreateNode(score: 80);
        double scoreBefore = node.Score;

        collector.RecordFailure(node, FailureType.Timeout, [node]);

        node.Score.Should().BeLessThan(scoreBefore,
            "Timeout should decrease score");
        node.ConsecutiveFailures.Should().Be(1);
    }

    [Fact]
    public void RecordFailure_Refused_ShouldDecreaseScoreMostAggressively()
    {
        var scorer = new ScoreCalculator();
        var cooldown = new CooldownFsm();
        var collector = new FailureCollector(scorer, cooldown);

        var node = CreateNode(score: 80);
        collector.RecordFailure(node, FailureType.Refused, [node]);
        double scoreAfterRefused = node.Score;

        var node2 = CreateNode(score: 80);
        collector.RecordFailure(node2, FailureType.UnexpectedEof, [node2]);
        double scoreAfterEof = node2.Score;

        scoreAfterRefused.Should().BeLessThan(scoreAfterEof,
            "Refused should be penalized more aggressively than UnexpectedEof");
    }
}
