using System.Collections;
using ServiceLib.Models.CoreConfigs;

namespace ServiceLib.Handler.AdaptiveNodeScheduler;

/// <summary>
/// Production implementation of IXrayStatsClient that fetches xray /debug/vars.
/// Parses outbound tag → cumulative bytes using the same model types as
/// <see cref="Services.Statistics.StatisticsXrayService"/>.
/// </summary>
public sealed class XrayStatsClient : IXrayStatsClient
{
    private readonly string _url;

    public XrayStatsClient(int statePort)
    {
        _url = $"{Global.HttpProtocol}{Global.Loopback}:{statePort}/debug/vars";
    }

    public async Task<Dictionary<string, long>> GetOutboundStatsAsync()
    {
        try
        {
            var result = await HttpClientHelper.Instance.TryGetAsync(_url);
            if (result == null)
                return new Dictionary<string, long>(StringComparer.Ordinal);

            var source = JsonUtils.Deserialize<V2rayMetricsVars>(result);
            if (source?.stats?.outbound == null)
                return new Dictionary<string, long>(StringComparer.Ordinal);

            var stats = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (var key in source.stats.outbound.Keys.Cast<string>())
            {
                var value = source.stats.outbound[key];
                if (value == null) continue;
                var state = JsonUtils.Deserialize<V2rayMetricsVarsLink>(value.ToString());
                stats[key] = state.uplink + state.downlink;
            }
            return stats;
        }
        catch
        {
            return new Dictionary<string, long>(StringComparer.Ordinal);
        }
    }
}
