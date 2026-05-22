using System.Net.Sockets;

namespace ServiceLib.Handler.AdaptiveNodeScheduler;

/// <summary>
/// Pre-startup parallel TCP-connect bootstrap prober.
///
/// <h2>P1#5 DNS cache integration</h2>
/// When a <see cref="DnsCacheManager"/> is provided, hostnames are resolved explicitly
/// and cached. Subsequent bootstrap cycles use cached IPs, avoiding DNS-induced timeouts
/// and enabling DNS-vs-node failure attribution (§13.2).
/// </summary>
public sealed class BootstrapProber
{
    private const int TcpTimeoutMs = 2000;
    private const int GlobalTimeoutMs = 3000;

    /// <summary>
    /// Runs parallel TCP-connect probes. Uses DNS cache when manager is provided.
    /// </summary>
    public async Task InitializeAsync(IReadOnlyList<NodeState> nodes,
                                      ScoreCalculator scorer,
                                      DnsCacheManager? dnsCache = null)
    {
        using var cts = new CancellationTokenSource(GlobalTimeoutMs);

        var tasks = nodes
            .Where(n => n.Protocol == ProxyProtocol.Tcp)
            .Select(n => ProbeOneAsync(n, scorer, dnsCache, cts.Token));

        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private static async Task ProbeOneAsync(NodeState node,
                                            ScoreCalculator scorer,
                                            DnsCacheManager? dnsCache,
                                            CancellationToken ct)
    {
        long t0 = Stopwatch.GetTimestamp();
        try
        {
            string connectHost = node.Host;
            if (dnsCache != null && !IPAddress.TryParse(node.Host, out _))
            {
                var cachedIp = await dnsCache.ResolveWithCacheAsync(node).ConfigureAwait(false);
                if (cachedIp != null)
                    connectHost = cachedIp;
            }

            using var tcp = new TcpClient();
            await tcp.ConnectAsync(connectHost, node.Port, ct).ConfigureAwait(false);
            double latencyMs = ElapsedMs(t0);

            // Cache hit: the resolved/cached IP worked
            if (connectHost != node.Host)
                dnsCache?.OnCachedIpConnectionSucceeded(node);

            double score = scorer.Compute(latencyMs, 0.0);
            node.UpdateScore(latencyMs, 0.0, score, 0);
        }
        catch (OperationCanceledException)
        {
            // If we used a cached IP that failed, report the cache miss
            if (dnsCache != null && !IPAddress.TryParse(node.Host, out _))
            {
                dnsCache.OnCachedIpConnectionFailed(node);
            }
            node.UpdateScore(5000, 1.0, 1.0, 0);
        }
        catch
        {
            if (dnsCache != null && !IPAddress.TryParse(node.Host, out _))
            {
                dnsCache.OnCachedIpConnectionFailed(node);
            }
            node.UpdateScore(5000, 1.0, 1.0, 0);
        }
    }

    private static double ElapsedMs(long t0) =>
        (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
}
