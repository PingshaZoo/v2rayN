using System.Net;
using System.Net.Sockets;

namespace ServiceLib.Handler.AdaptiveNodeScheduler;

/// <summary>
/// P1#5: DNS cache manager implementing the DNS Cache Confidence Lifecycle (§13.4).
///
/// GFW environments make DNS an independent failure domain. DNS failure ≠ node failure.
/// This manager caches resolved IPs per node and provides cache-aware resolution
/// with confidence tracking. When a cached IP works but fresh DNS resolution fails,
/// the failure is attributed to DNS (not the node), preventing false cooldown ejections.
///
/// <h2>Cache Confidence Lifecycle (§13.4)</h2>
/// [DNS_CACHE_VALID] → N consecutive cache misses → [DNS_CACHE_INVALIDATED] → re-resolve
/// TTL expiry (300s) → proactive re-resolution
/// </summary>
public sealed class DnsCacheManager
{
    private readonly int _ttlSeconds;
    private readonly int _invalidateAfterFailures;
    private readonly IClock _clock;

    public DnsCacheManager(IClock clock, int ttlSeconds = 300, int invalidateAfterFailures = 3)
    {
        _clock = clock;
        _ttlSeconds = ttlSeconds;
        _invalidateAfterFailures = invalidateAfterFailures;
    }

    /// <summary>
    /// Resolves a node's hostname to an IP address, caching the result.
    /// If the cache is valid and not expired, returns the cached IP without re-resolution.
    /// Returns null if resolution fails.
    /// </summary>
    public async Task<string?> ResolveWithCacheAsync(NodeState node)
    {
        // Check cache first
        if (!node.IsDnsCacheExpired(_ttlSeconds))
        {
            node.OnDnsCacheHit();
            return node.CachedIp;
        }

        // Cache expired or missing — resolve fresh
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(node.Host).ConfigureAwait(false);
            var ipv4 = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
            if (ipv4 != null)
            {
                var ip = ipv4.ToString();
                if (node.CachedIp == ip)
                {
                    // Same IP — keep confidence, just refresh timestamp
                    node.OnDnsCacheHit();
                }
                else
                {
                    // New IP — set cache, reset confidence
                    node.SetCachedIp(ip, _clock.UtcNow);
                }
                return ip;
            }
        }
        catch
        {
            // Resolution failed — if we have a stale cache, return it as fallback
            if (node.CachedIp != null)
                return node.CachedIp;
        }

        return null;
    }

    /// <summary>
    /// Called when a connection using the cached IP fails.
    /// Returns true if the cache should be invalidated (N consecutive failures reached).
    /// </summary>
    public bool OnCachedIpConnectionFailed(NodeState node)
    {
        return node.OnDnsCacheMiss(_invalidateAfterFailures);
    }

    /// <summary>
    /// Called when a connection using the cached IP succeeds.
    /// Boosts cache confidence.
    /// </summary>
    public void OnCachedIpConnectionSucceeded(NodeState node)
    {
        node.OnDnsCacheHit();
    }

    /// <summary>
    /// Force-invalidates the DNS cache for a node. Used when the cache is
    /// suspected to be poisoned or after profile changes.
    /// </summary>
    public void InvalidateCache(NodeState node)
    {
        node.InvalidateDnsCache();
    }
}
