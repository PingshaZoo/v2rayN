namespace ServiceLib.Services.Statistics;

public class StatisticsXrayService
{
    private const long linkBase = 1024;
    private ServerSpeedItem _serverSpeedItem = new();
    private readonly Dictionary<string, (long Uplink, long Downlink)> _perTagBaseline = new(StringComparer.Ordinal);
    private readonly Config _config;
    private bool _exitFlag;
    private readonly Func<ServerSpeedItem, Task>? _updateFunc;
    private string Url => $"{Global.HttpProtocol}{Global.Loopback}:{AppManager.Instance.StatePort}/debug/vars";

    public StatisticsXrayService(Config config, Func<ServerSpeedItem, Task> updateFunc)
    {
        _config = config;
        _updateFunc = updateFunc;
        _exitFlag = false;

        _ = Task.Run(Run);
    }

    public void Close()
    {
        _exitFlag = true;
    }

    private async Task Run()
    {
        while (!_exitFlag)
        {
            await Task.Delay(1000);
            try
            {
                if (AppManager.Instance.RunningCoreType != ECoreType.Xray)
                {
                    continue;
                }

                var result = await HttpClientHelper.Instance.TryGetAsync(Url);
                if (result != null)
                {
                    var server = ParseOutput(result) ?? new ServerSpeedItem();
                    await _updateFunc?.Invoke(server);
                }
            }
            catch
            {
                // ignored
            }
        }
    }

    private ServerSpeedItem? ParseOutput(string result)
    {
        try
        {
            var source = JsonUtils.Deserialize<V2rayMetricsVars>(result);
            if (source?.stats?.outbound == null)
            {
                return null;
            }

            ServerSpeedItem server = new();
            var perTagCumulative = new Dictionary<string, (long Uplink, long Downlink)>(StringComparer.Ordinal);
            foreach (var key in source.stats.outbound.Keys.Cast<string>())
            {
                var value = source.stats.outbound[key];
                if (value == null)
                {
                    continue;
                }
                var state = JsonUtils.Deserialize<V2rayMetricsVarsLink>(value.ToString());

                if (key.StartsWith(Global.ProxyTag))
                {
                    server.ProxyUp += state.uplink / linkBase;
                    server.ProxyDown += state.downlink / linkBase;
                    perTagCumulative[key] = (state.uplink / linkBase, state.downlink / linkBase);
                }
                else if (key == Global.DirectTag)
                {
                    server.DirectUp = state.uplink / linkBase;
                    server.DirectDown = state.downlink / linkBase;
                }
            }

            if (server.DirectDown < _serverSpeedItem.DirectDown || server.ProxyDown < _serverSpeedItem.ProxyDown)
            {
                _serverSpeedItem = new();
                _perTagBaseline.Clear();
                return null;
            }

            ServerSpeedItem curItem = new()
            {
                ProxyUp = server.ProxyUp - _serverSpeedItem.ProxyUp,
                ProxyDown = server.ProxyDown - _serverSpeedItem.ProxyDown,
                DirectUp = server.DirectUp - _serverSpeedItem.DirectUp,
                DirectDown = server.DirectDown - _serverSpeedItem.DirectDown,
            };

            // Compute per-tag proxy traffic deltas
            if (perTagCumulative.Count > 0)
            {
                var perTagDeltas = new Dictionary<string, (long Up, long Down)>(StringComparer.Ordinal);
                foreach (var (tag, (uplink, downlink)) in perTagCumulative)
                {
                    if (_perTagBaseline.TryGetValue(tag, out var prev))
                    {
                        var deltaUp = uplink - prev.Uplink;
                        var deltaDown = downlink - prev.Downlink;
                        if (deltaUp > 0 || deltaDown > 0)
                            perTagDeltas[tag] = (deltaUp, deltaDown);
                    }
                    else
                    {
                        // First observation — don't report a spike; baseline it
                    }
                    _perTagBaseline[tag] = (uplink, downlink);
                }
                if (perTagDeltas.Count > 0)
                    curItem.PerTagProxyTraffic = new ConcurrentDictionary<string, NodeTrafficSnapshot>(
                        perTagDeltas.ToDictionary(
                            kv => kv.Key,
                            kv => new NodeTrafficSnapshot(kv.Value.Up, kv.Value.Down, DateTime.UtcNow)));
            }

            _serverSpeedItem = server;
            return curItem;
        }
        catch
        {
            // ignored
        }

        return null;
    }
}
