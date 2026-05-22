namespace ServiceLib.Models.Dto;

/// <summary>
/// P2.3: Thread-safe per-node traffic snapshot.
/// Replaces the <c>(long Up, long Down)</c> value tuple previously used in
/// <see cref="ServerSpeedItem.PerTagProxyTraffic"/> to avoid data races
/// when multiple threads (Stats poller, ProbeService, UI) access tag-level traffic.
/// </summary>
[Serializable]
public record NodeTrafficSnapshot(long UpKbps, long DownKbps, DateTime UpdatedAt);
