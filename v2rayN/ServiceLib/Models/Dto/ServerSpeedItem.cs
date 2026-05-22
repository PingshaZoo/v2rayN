using System.Collections.Concurrent;

namespace ServiceLib.Models.Dto;

[Serializable]
public class ServerSpeedItem : ServerStatItem
{
    public long ProxyUp { get; set; }

    public long ProxyDown { get; set; }

    public long DirectUp { get; set; }

    public long DirectDown { get; set; }

    /// <summary>
    /// P2.3: Per-outbound-tag proxy traffic deltas (KB).
    /// Thread-safe via ConcurrentDictionary; value is a record for atomic reads.
    /// Populated by StatisticsXrayService when adaptive scheduling is active.
    /// </summary>
    public ConcurrentDictionary<string, NodeTrafficSnapshot>? PerTagProxyTraffic { get; set; }
}

[Serializable]
public class TrafficItem
{
    public ulong Up { get; set; }

    public ulong Down { get; set; }
}
