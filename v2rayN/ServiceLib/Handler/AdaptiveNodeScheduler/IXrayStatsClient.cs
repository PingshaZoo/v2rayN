namespace ServiceLib.Handler.AdaptiveNodeScheduler;

/// <summary>
/// Abstraction over xray /debug/vars stats endpoint.
/// Exists primarily so XrayStatsPoller can be unit-tested with a fake client.
/// </summary>
public interface IXrayStatsClient
{
    /// <summary>
    /// Returns a dictionary mapping outbound tag → cumulative bytes (uplink + downlink).
    /// May return an empty dictionary if the stats endpoint is unavailable.
    /// </summary>
    Task<Dictionary<string, long>> GetOutboundStatsAsync();
}
