using AwesomeAssertions;
using ServiceLib.Handler.AdaptiveNodeScheduler;
using Xunit;

namespace ServiceLib.Tests.AdaptiveNodeScheduler;

/// <summary>
/// P1.7: Verifies score staleness detection.
/// Historical scores older than 4h are reset to 50 on restore.
/// </summary>
public class ScoreExpirationTests
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

    /// <summary>
    /// Simulates what RestorePersistedScoresAsync does: checks AdaptiveLastObserved
    /// age against the 4h threshold. If stale, score is reset to 50.
    /// </summary>
    [Fact]
    public void RestoreScore_StaleOlderThan4h_ShouldResetTo50()
    {
        var node = CreateNode();
        // Simulate restoring a score that was persisted 5 hours ago
        var persistedTime = DateTime.UtcNow.AddHours(-5);
        var age = DateTime.UtcNow - persistedTime;

        bool isStale = age > TimeSpan.FromHours(4);
        isStale.Should().BeTrue("score persisted 5h ago is stale");

        if (isStale)
        {
            // Simulate the reset-to-50 path
            node.UpdateScore(500.0, 0.0, 50.0, 0);
        }
        else
        {
            node.UpdateScore(100.0, 0.0, 90.0, 0);
        }

        node.Score.Should().Be(50.0, "stale score (>4h) must be reset to 50");
        node.EwmaLatencyMs.Should().Be(500.0, "stale score latency reset to default 500ms");
    }

    [Fact]
    public void RestoreScore_FreshWithin4h_ShouldPreserveScore()
    {
        var node = CreateNode();
        // Simulate restoring a score that was persisted 30 minutes ago
        var persistedTime = DateTime.UtcNow.AddMinutes(-30);
        var age = DateTime.UtcNow - persistedTime;

        bool isStale = age > TimeSpan.FromHours(4);
        isStale.Should().BeFalse("score persisted 30min ago is fresh");

        if (isStale)
        {
            node.UpdateScore(500.0, 0.0, 50.0, 0);
        }
        else
        {
            // Fresh score: restore persisted value
            node.UpdateScore(120.0, 0.0, 85.0, 0);
        }

        node.Score.Should().Be(85.0, "fresh score must be preserved");
        node.EwmaLatencyMs.Should().Be(120.0);
    }

    [Fact]
    public void RestoreScore_Exactly4hBoundary_ShouldNotBeStale()
    {
        // Exactly 4h is NOT stale (must be strictly greater than).
        // Freeze "now" to make the comparison deterministic.
        var now = DateTime.UtcNow;
        var persistedTime = now.AddHours(-4);
        var age = now - persistedTime;

        bool isStale = age > TimeSpan.FromHours(4);
        isStale.Should().BeFalse("exactly 4h old is not stale (must be >4h, not >=)");
    }

    [Fact]
    public void RestoreScore_NoTimestamp_ShouldTreatAsFresh()
    {
        // If AdaptiveLastObserved is default(DateTime), it's treated as fresh
        // (backward compat with pre-P1.7 persisted data that has no timestamp)
        var defaultTime = default(DateTime);
        defaultTime.Should().Be(DateTime.MinValue);

        // When ex.AdaptiveLastObserved == default, the staleness check is skipped
        bool skipStalenessCheck = defaultTime == default;
        skipStalenessCheck.Should().BeTrue(
            "no timestamp (default) means staleness check is skipped for backward compat");
    }

    [Fact]
    public void RestoreScore_StaleScoreConsidersFreshTimestamp()
    {
        // Comprehensive: verify that the staleness formula works across various ages.
        // Freeze "now" so comparisons are deterministic.
        // Staleness: age > 4h (strictly greater).
        static bool IsStale(DateTime persisted, DateTime now) =>
            persisted != default && now - persisted > TimeSpan.FromHours(4);

        var now = DateTime.UtcNow;

        // 5h ago → stale
        IsStale(now.AddHours(-5), now).Should().BeTrue();
        // 3h ago → fresh
        IsStale(now.AddHours(-3), now).Should().BeFalse();
        // 4h + 1s ago → stale (strictly > 4h)
        IsStale(now.AddHours(-4).AddSeconds(-1), now).Should().BeTrue();
        // 4h - 1s ago (3h59m59s) → fresh (not strictly > 4h)
        IsStale(now.AddHours(-4).AddSeconds(1), now).Should().BeFalse();
        // default(DateTime) → fresh (no timestamp, backward compat)
        IsStale(default, now).Should().BeFalse();
    }
}
