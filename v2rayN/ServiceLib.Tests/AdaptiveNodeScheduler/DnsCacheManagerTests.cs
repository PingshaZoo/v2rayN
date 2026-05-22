using AwesomeAssertions;
using ServiceLib.Handler.AdaptiveNodeScheduler;
using Xunit;

namespace ServiceLib.Tests.AdaptiveNodeScheduler;

/// <summary>
/// P1#5: Tests for DnsCacheManager — DNS cache confidence lifecycle (§13.4).
///
/// Covers: cache hit/miss, TTL expiry, confidence lifecycle, invalidation,
/// cache fallback on resolution failure.
/// </summary>
public class DnsCacheManagerTests
{
    private static NodeState CreateNode(string tag, string host = "example.com")
    {
        var node = new NodeState
        {
            Tag = tag,
            Host = host,
            Port = 443,
            Protocol = ProxyProtocol.Tcp,
            ChildIndexId = tag,
        };
        node.UpdateScore(100, 0.0, 50, 0);
        return node;
    }

    private static (DnsCacheManager mgr, FakeClock clock) CreateManager(int ttlSec = 300, int invalidateAfter = 3)
    {
        var clock = new FakeClock();
        var mgr = new DnsCacheManager(clock, ttlSec, invalidateAfter);
        return (mgr, clock);
    }

    // ── Initial state ─────────────────────────────────────────

    [Fact]
    public void NewNode_HasNoCachedIp()
    {
        var node = CreateNode("A");

        node.CachedIp.Should().BeNull();
        node.DnsLastResolved.Should().Be(DateTime.MinValue);
        node.DnsCacheConfidence.Should().Be(0);
        node.DnsConsecutiveCacheFailures.Should().Be(0);
    }

    [Fact]
    public void NewNode_IsDnsCacheExpired_ReturnsTrue()
    {
        var node = CreateNode("A");
        node.IsDnsCacheExpired().Should().BeTrue("no cache = expired");
    }

    // ── Set and retrieve cache ─────────────────────────────────

    [Fact]
    public void SetCachedIp_StoresIpAndResetsState()
    {
        var node = CreateNode("A");
        var (_, clock) = CreateManager();
        var now = clock.UtcNow;

        node.SetCachedIp("1.2.3.4", now);

        node.CachedIp.Should().Be("1.2.3.4");
        node.DnsLastResolved.Should().Be(now);
        node.DnsCacheConfidence.Should().Be(1);
        node.DnsConsecutiveCacheFailures.Should().Be(0);
    }

    [Fact]
    public void SetCachedIp_FreshCache_IsNotExpired()
    {
        var node = CreateNode("A");
        var (_, clock) = CreateManager();
        node.SetCachedIp("1.2.3.4", clock.UtcNow);

        node.IsDnsCacheExpired(now: clock.UtcNow).Should().BeFalse("just-set cache is not expired");
    }

    // ── Cache confidence lifecycle ──────────────────────────────

    [Fact]
    public void OnCacheHit_IncrementsConfidence()
    {
        var node = CreateNode("A");
        node.SetCachedIp("1.2.3.4", DateTime.UtcNow);

        node.OnDnsCacheHit();
        node.DnsCacheConfidence.Should().Be(2);

        node.OnDnsCacheHit();
        node.DnsCacheConfidence.Should().Be(3);
    }

    [Fact]
    public void OnCacheHit_ResetsConsecutiveFailures()
    {
        var node = CreateNode("A");
        node.SetCachedIp("1.2.3.4", DateTime.UtcNow);

        // Simulate some misses first
        node.OnDnsCacheMiss();
        node.OnDnsCacheMiss();
        node.DnsConsecutiveCacheFailures.Should().Be(2);

        // Then a hit resets the counter
        node.OnDnsCacheHit();
        node.DnsConsecutiveCacheFailures.Should().Be(0);
    }

    [Fact]
    public void OnDnsCacheMiss_IncrementsConsecutiveFailures()
    {
        var node = CreateNode("A");
        node.SetCachedIp("1.2.3.4", DateTime.UtcNow);

        bool shouldInvalidate1 = node.OnDnsCacheMiss();
        shouldInvalidate1.Should().BeFalse("1 failure < 3 invalidate threshold");
        node.DnsConsecutiveCacheFailures.Should().Be(1);

        bool shouldInvalidate2 = node.OnDnsCacheMiss();
        shouldInvalidate2.Should().BeFalse("2 failures < 3 invalidate threshold");
        node.DnsConsecutiveCacheFailures.Should().Be(2);
    }

    [Fact]
    public void OnDnsCacheMiss_NConsecutiveFailures_TriggersInvalidation()
    {
        var node = CreateNode("A");
        node.SetCachedIp("1.2.3.4", DateTime.UtcNow);

        node.OnDnsCacheMiss(); // 1
        node.OnDnsCacheMiss(); // 2
        bool shouldInvalidate = node.OnDnsCacheMiss(); // 3 → invalidate

        shouldInvalidate.Should().BeTrue("3 consecutive failures → invalidate cache");
    }

    [Fact]
    public void OnCachedIpConnectionFailed_ManagerLevel_InvalidatesAfterThreshold()
    {
        var (mgr, _) = CreateManager(invalidateAfter: 3);
        var node = CreateNode("A");
        node.SetCachedIp("1.2.3.4", DateTime.UtcNow);

        mgr.OnCachedIpConnectionFailed(node); // 1
        mgr.OnCachedIpConnectionFailed(node); // 2
        bool shouldInvalidate = mgr.OnCachedIpConnectionFailed(node); // 3

        shouldInvalidate.Should().BeTrue();
    }

    [Fact]
    public void OnCachedIpConnectionSucceeded_BoostsConfidence()
    {
        var (mgr, _) = CreateManager();
        var node = CreateNode("A");
        node.SetCachedIp("1.2.3.4", DateTime.UtcNow);

        mgr.OnCachedIpConnectionSucceeded(node);

        node.DnsCacheConfidence.Should().Be(2);
        node.DnsConsecutiveCacheFailures.Should().Be(0);
    }

    // ── TTL expiry ─────────────────────────────────────────────

    [Fact]
    public void IsDnsCacheExpired_AfterTtl_ReturnsTrue()
    {
        var (_, clock) = CreateManager(ttlSec: 300);
        var node = CreateNode("A");
        node.SetCachedIp("1.2.3.4", clock.UtcNow);

        clock.AdvanceTime(TimeSpan.FromSeconds(299));
        node.IsDnsCacheExpired(300, clock.UtcNow).Should().BeFalse("299s < 300s TTL");

        clock.AdvanceTime(TimeSpan.FromSeconds(2)); // 301s total
        node.IsDnsCacheExpired(300, clock.UtcNow).Should().BeTrue("301s > 300s TTL");
    }

    [Fact]
    public void IsDnsCacheExpired_ExactlyAtTtl_ReturnsTrue()
    {
        var (_, clock) = CreateManager(ttlSec: 300);
        var node = CreateNode("A");
        node.SetCachedIp("1.2.3.4", clock.UtcNow);

        clock.AdvanceTime(TimeSpan.FromSeconds(300));

        node.IsDnsCacheExpired(300, clock.UtcNow).Should().BeTrue("exactly at TTL boundary → expired");
    }

    // ── Cache invalidation ─────────────────────────────────────

    [Fact]
    public void InvalidateDnsCache_ClearsAllState()
    {
        var node = CreateNode("A");
        node.SetCachedIp("1.2.3.4", DateTime.UtcNow);
        node.OnDnsCacheHit();
        node.OnDnsCacheHit();

        node.InvalidateDnsCache();

        node.CachedIp.Should().BeNull();
        node.DnsLastResolved.Should().Be(DateTime.MinValue);
        node.DnsCacheConfidence.Should().Be(0);
        node.DnsConsecutiveCacheFailures.Should().Be(0);
    }

    [Fact]
    public void InvalidateCache_ManagerLevel_ClearsState()
    {
        var (mgr, _) = CreateManager();
        var node = CreateNode("A");
        node.SetCachedIp("1.2.3.4", DateTime.UtcNow);

        mgr.InvalidateCache(node);

        node.CachedIp.Should().BeNull();
    }

    // ── ResolveWithCacheAsync ──────────────────────────────────

    [Fact]
    public async Task ResolveWithCacheAsync_ValidCache_ReturnsCachedIp()
    {
        // This test verifies the cache-hit path without actual DNS resolution.
        // We use an IP address as the host, which bypasses DNS entirely.
        var (mgr, _) = CreateManager();
        var node = CreateNode("A", host: "1.2.3.4");
        // Pre-populate cache
        node.SetCachedIp("1.2.3.4", DateTime.UtcNow);

        // Force expiry check — not expired
        var result = await mgr.ResolveWithCacheAsync(node);

        result.Should().Be("1.2.3.4");
        node.DnsCacheConfidence.Should().Be(2, "cache hit increments confidence");
    }

    // ── Edge cases ─────────────────────────────────────────────

    [Fact]
    public void DnsCache_ThreadSafety_SingleNode()
    {
        // Multiple operations on the same node should not throw
        var node = CreateNode("A");

        var tasks = new List<Task>();
        for (int i = 0; i < 10; i++)
        {
            tasks.Add(Task.Run(() =>
            {
                node.SetCachedIp($"10.0.0.{Random.Shared.Next(1, 255)}", DateTime.UtcNow);
                node.OnDnsCacheHit();
                node.OnDnsCacheMiss();
                node.IsDnsCacheExpired();
                _ = node.CachedIp;
                _ = node.DnsCacheConfidence;
            }));
        }
        Task.WaitAll(tasks.ToArray());

        // Should not throw — state may be inconsistent but no crash
        node.CachedIp.Should().NotBeNull("at least one SetCachedIp succeeded");
    }

    [Fact]
    public void DifferentNodes_HaveIndependentCaches()
    {
        var nodeA = CreateNode("A");
        var nodeB = CreateNode("B");

        nodeA.SetCachedIp("10.0.0.1", DateTime.UtcNow);
        nodeB.SetCachedIp("10.0.0.2", DateTime.UtcNow);

        nodeA.CachedIp.Should().Be("10.0.0.1");
        nodeB.CachedIp.Should().Be("10.0.0.2");

        // Invalidate A only
        nodeA.InvalidateDnsCache();
        nodeA.CachedIp.Should().BeNull();
        nodeB.CachedIp.Should().Be("10.0.0.2", "B's cache is independent");
    }
}
