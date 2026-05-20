namespace ServiceLib.Services.CoreConfig;

public partial class CoreConfigV2rayService
{
    /// <summary>
    /// Generates probe SOCKS5 inbounds + adaptive balancer config when adaptive scheduling is active.
    /// Replaces the standard observatory/balancer generation.
    /// </summary>
    private void GenAdaptiveConfig(List<Outbounds4Ray> proxyOutboundList)
    {
        var adaptive = context.AdaptiveConfig;
        if (adaptive == null) return;

        var probePorts = adaptive.ProbePorts;

        // 1. Add probe SOCKS5 inbounds (127.0.0.1 only, one per node)
        foreach (var outbound in proxyOutboundList)
        {
            if (!probePorts.TryGetValue(outbound.tag, out int probePort))
                continue;

            var probeInbound = new Inbounds4Ray
            {
                tag = $"probe-{outbound.tag}",
                listen = Global.Loopback,
                port = probePort,
                protocol = EInboundProtocol.mixed.ToString(),
                settings = new Inboundsettings4Ray
                {
                    udp = false,
                    auth = "noauth"
                },
                sniffing = new Sniffing4Ray { enabled = false }
            };
            _coreConfig.inbounds.Add(probeInbound);

            // 2. Add routing rule: probe inbound → specific outbound
            _coreConfig.routing.rules.Insert(0, new RulesItem4Ray
            {
                type = "field",
                inboundTag = [probeInbound.tag],
                outboundTag = outbound.tag
            });
        }

        // 3. Build weighted balancer selector via tag duplication.
        // xray's random strategy picks uniformly from the selector list.
        // Duplicating a tag N times gives it N× the selection probability.
        // Weights are derived from QoS scores: high-score nodes appear more often.
        // Cooldown nodes are excluded. xray's observatory is disabled —
        // probing is handled by our ProbeService.
        var scores = adaptive.NodeScores;
        var activeTags = adaptive.ActiveTags;
        var weightedSelector = proxyOutboundList
            .Where(o => activeTags.Count == 0 || activeTags.Contains(o.tag))
            .SelectMany(o =>
            {
                int copies = 1;
                if (scores.TryGetValue(o.tag, out double s))
                {
                    if (s >= 70) copies = 3;
                    else if (s >= 40) copies = 2;
                }
                return Enumerable.Repeat(o.tag, copies);
            })
            .ToList();

        if (weightedSelector.Count > 1)
        {
            _coreConfig.observatory = null;

            var balancerTag = $"{Global.ProxyTag}{Global.BalancerTagSuffix}";
            _coreConfig.routing.balancers ??= new();
            _coreConfig.routing.balancers.Add(new BalancersItem4Ray
            {
                selector = weightedSelector,
                strategy = new()
                {
                    type = "random",
                },
                tag = balancerTag,
            });

            var finalRule = _coreConfig.routing.rules
                .FirstOrDefault(r => r.outboundTag == Global.ProxyTag);
            if (finalRule != null)
            {
                finalRule.balancerTag = balancerTag;
                finalRule.outboundTag = null;
            }
        }
        else if (weightedSelector.Distinct().Count() == 1)
        {
            var singleTag = weightedSelector[0];
            var finalRule = _coreConfig.routing.rules
                .FirstOrDefault(r => r.outboundTag == Global.ProxyTag);
            if (finalRule != null)
            {
                finalRule.outboundTag = singleTag;
            }
        }
    }
}
