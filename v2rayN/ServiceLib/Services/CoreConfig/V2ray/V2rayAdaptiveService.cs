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

        // 3. Build active-set balancer selector.
        // xray's random balancer picks uniformly from the selector candidates
        // (each unique tag gets one entry). xray deduplicates selector entries
        // internally via prefix-match + dedup, so tag duplication does NOT work
        // for weighting (verified: xray v26.3.27, selector [A,A,A,B] → A=47.6%).
        //
        // The system is an Adaptive Active-Set Scheduler:
        //   - Bad nodes are ejected from the active set (cooldown / low score)
        //   - Nodes within the active set share traffic uniformly
        //   - xray's observatory is disabled — probing is handled by our ProbeService
        var activeTags = adaptive.ActiveTags;
        var activeSelector = proxyOutboundList
            .Where(o => activeTags.Contains(o.tag))
            .Select(o => o.tag)
            .ToList();
        if (activeSelector.Count == 0)
        {
            var fallback = proxyOutboundList
                .FirstOrDefault(o => !adaptive.CooldownTags.Contains(o.tag))
                ?? proxyOutboundList.FirstOrDefault();
            if (fallback != null)
            {
                activeSelector.Add(fallback.tag);
            }
        }

        if (activeSelector.Count > 0)
        {
            _coreConfig.observatory = null;

            var balancerTag = $"{Global.ProxyTag}{Global.BalancerTagSuffix}";
            _coreConfig.routing.balancers ??= new();
            _coreConfig.routing.balancers.Add(new BalancersItem4Ray
            {
                selector = activeSelector,
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
    }
}
