namespace ServiceLib.Models.Dto;

[Serializable]
public class ServerSpeedItem : ServerStatItem
{
    public long ProxyUp { get; set; }

    public long ProxyDown { get; set; }

    public long DirectUp { get; set; }

    public long DirectDown { get; set; }

    /// <summary>
    /// Per-outbound-tag proxy traffic deltas (KB).
    /// Key is the xray outbound tag, value is a tuple of (up, down) in KB per second.
    /// Populated by StatisticsXrayService when adaptive scheduling is active.
    /// </summary>
    public Dictionary<string, (long Up, long Down)>? PerTagProxyTraffic { get; set; }
}

[Serializable]
public class TrafficItem
{
    public ulong Up { get; set; }

    public ulong Down { get; set; }
}
