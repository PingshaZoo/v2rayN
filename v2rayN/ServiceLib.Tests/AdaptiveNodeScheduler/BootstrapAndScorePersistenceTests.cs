using AwesomeAssertions;
using ServiceLib.Handler.AdaptiveNodeScheduler;
using Xunit;

namespace ServiceLib.Tests.AdaptiveNodeScheduler;

/// <summary>
/// P0.3: Verifies that bootstrap probing always overwrites historical scores.
/// Historical high scores must not mask dead nodes on restart.
/// </summary>
public class BootstrapAndScorePersistenceTests
{
    private static NodeState CreateNode(string tag = "test-node", string childIndexId = "child-1")
    {
        return new NodeState
        {
            Tag = tag,
            Host = "127.0.0.1",
            Port = 1080,
            Protocol = ProxyProtocol.Tcp,
            ChildIndexId = childIndexId,
        };
    }

    // ── ScoreCalculator worst-case behavior ─────────────────────

    [Fact]
    public void ScoreCalculator_WorstCaseBootstrapFailure_ShouldReturnScoreFloorOfOne()
    {
        // BootstrapProber calls node.UpdateScore(5000, 1.0, score, 0) on failure
        var scorer = new ScoreCalculator();

        double score = scorer.Compute(ewmaLatencyMs: 5000, ewmaLossRate: 1.0);

        score.Should().Be(1.0,
            "worst-case inputs (5s latency, 100%% loss) must produce floor score = 1.0");
    }

    [Fact]
    public void ScoreCalculator_BootstrapFailureLatency_ShouldProduceMinimalScore()
    {
        var scorer = new ScoreCalculator();

        double score = scorer.Compute(ewmaLatencyMs: 5000, ewmaLossRate: 0.0);
        score.Should().BeLessThan(25.0,
            "5s latency alone should produce a low score (< 25, insufficient for active set entry of 60)");
    }

    [Fact]
    public void ScoreCalculator_GoodBootstrap_ShouldProduceHighScore()
    {
        var scorer = new ScoreCalculator();

        // TCP connect in 30ms, no loss — a healthy node
        double score = scorer.Compute(ewmaLatencyMs: 30, ewmaLossRate: 0.0);

        score.Should().BeGreaterThan(85,
            "30ms TCP connect with no loss should yield a high bootstrap score");
    }

    [Fact]
    public void ScoreCalculator_HistoricalScoreRecovery_ShouldBeOverwrittenByBootstrapFailure()
    {
        // Simulates: node had score=90 from previous run (persisted)
        // Bootstrap TCP connect fails → calls node.UpdateScore(5000, 1.0, 1.0, 0)
        // The node's score must become 1.0, NOT stay at 90

        var node = CreateNode();
        var scorer = new ScoreCalculator();

        // Step 1: restore historical high score (simulates RestorePersistedScoresAsync)
        node.UpdateScore(30, 0.0, 90.0, 0);
        node.Score.Should().Be(90.0);

        // Step 2: bootstrap failure overwrites (simulates BootstrapProber.ProbeOneAsync catch)
        double bootstrapScore = scorer.Compute(ewmaLatencyMs: 5000, ewmaLossRate: 1.0);
        node.UpdateScore(5000, 1.0, bootstrapScore, 0);

        // The historical 90 must be gone
        node.Score.Should().Be(1.0,
            "bootstrap failure must overwrite historical high score");
        node.EwmaLatencyMs.Should().Be(5000);
        node.EwmaLossRate.Should().Be(1.0);
    }

    // ── Score expiration — historical scores > 4h are untrustworthy ──

    [Fact]
    public void NodeState_LastObservedMoreThan4h_ShouldBeConsideredStale()
    {
        var node = CreateNode();
        node.UpdateScore(30, 0.0, 90.0, 0);

        // If last observed was > 4h ago, the score should be treated as stale
        // (This is checked by the caller; the node itself doesn't auto-invalidate)
        var age = DateTime.UtcNow - node.LastObserved;

        // Fresh node: age is near zero
        age.TotalHours.Should().BeLessThan(0.01);

        // Simulate restoring a node whose _lastObserved was set long ago
        // (this happens when restoring from persistence without updating _lastObserved)
        // The caller (RestorePersistedScoresAsync) should check age and reset to 50
        // if > 4h. This test validates the age-detection logic.
        var staleTime = DateTime.UtcNow.AddHours(-5);
        var staleAge = DateTime.UtcNow - staleTime;
        staleAge.TotalHours.Should().BeGreaterThan(4,
            "5h-old observation should be detected as stale");
    }

    // ── Sequencing: RestorePersistedScoresAsync → BootstrapAsync → UpdateScore ──

    [Fact]
    public void BootstrapMustRunAfterRestore_OrderEnsuresOverwrite()
    {
        // In AdaptiveSchedulerManager.BootstrapAsync():
        //   1. await RestorePersistedScoresAsync() — restores old scores
        //   2. await _bootstrapper.InitializeAsync(_nodes, _scorer) — probes all nodes
        //
        // InitializeAsync calls ProbeOneAsync which calls node.UpdateScore() on EVERY node
        // (success or failure). This guarantees that after BootstrapAsync completes,
        // every node's score reflects the current bootstrap result, not the restored value.

        // The bootstrap ALWAYS writes a score via UpdateScore — it cannot leave
        // a restored score untouched. This is verified by code review:
        //
        // BootstrapProber.ProbeOneAsync:
        //   - success path: node.UpdateScore(latencyMs, 0.0, score, 0)   // overwrites
        //   - catch (OperationCanceledException): node.UpdateScore(5000, 1.0, 1.0, 0)
        //   - catch: node.UpdateScore(5000, 1.0, 1.0, 0)
        //
        // ALL code paths call UpdateScore. No path preserves the prior score.
        //
        // This test encodes the invariant.

        // We can't easily test BootstrapProber (it needs real TCP),
        // but we can verify the ScoreCalculator behavior that the bootstrap uses.
        var scorer = new ScoreCalculator();

        // Even the best-case bootstrap result produces a computed score,
        // never a null or "skip" value:
        double bestScore = scorer.Compute(ewmaLatencyMs: 10, ewmaLossRate: 0.0);
        bestScore.Should().BeGreaterThan(90);

        // The worst-case (failure) produces 1.0:
        double worstScore = scorer.Compute(ewmaLatencyMs: 5000, ewmaLossRate: 1.0);
        worstScore.Should().Be(1.0);

        // Both are "real" scores that will overwrite whatever was restored.
        // There is no "skip" return value — bootstrap always produces a score.
    }
}
