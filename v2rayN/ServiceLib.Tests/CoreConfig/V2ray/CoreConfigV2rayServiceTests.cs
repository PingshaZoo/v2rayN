using AwesomeAssertions;
using ServiceLib.Common;
using ServiceLib.Enums;
using ServiceLib.Models;
using ServiceLib.Models.CoreConfigs;
using ServiceLib.Services.CoreConfig;
using Xunit;

namespace ServiceLib.Tests.CoreConfig.V2ray;

public class CoreConfigV2rayServiceTests
{
    [Fact]
    public void GenerateClientConfigContent_ShouldGenerateBasicProxyConfig()
    {
        var config = CoreConfigTestFactory.CreateConfig(ECoreType.Xray);
        CoreConfigTestFactory.BindAppManagerConfig(config);
        var node = CoreConfigTestFactory.CreateVmessNode(ECoreType.Xray);
        var context = CoreConfigTestFactory.CreateContext(config, node, ECoreType.Xray);

        var result = new CoreConfigV2rayService(context).GenerateClientConfigContent();

        result.Success.Should().BeTrue();
        result.Data.Should().NotBeNull();

        var v2rayConfig = JsonUtils.Deserialize<V2rayConfig>(result.Data!.ToString());
        v2rayConfig.Should().NotBeNull();
        v2rayConfig!.outbounds.Should().Contain(o => o.tag == Global.ProxyTag && o.protocol == "vmess");
        v2rayConfig.inbounds.Should().Contain(i => i.protocol == nameof(EInboundProtocol.mixed));
    }

    [Fact]
    public void GenerateClientConfigContent_PolicyGroup_ShouldExpandChildrenAndBuildBalancer()
    {
        var config = CoreConfigTestFactory.CreateConfig(ECoreType.Xray);
        CoreConfigTestFactory.BindAppManagerConfig(config);

        var n1 = CoreConfigTestFactory.CreateSocksNode(ECoreType.Xray, "n1", "node-1");
        var n2 = CoreConfigTestFactory.CreateSocksNode(ECoreType.Xray, "n2", "node-2");
        var group = CoreConfigTestFactory.CreatePolicyGroupNode(ECoreType.Xray, "g1", "group",
            [n1.IndexId, n2.IndexId]);

        var context = CoreConfigTestFactory.CreateContext(config, group, ECoreType.Xray);
        context.AllProxiesMap[n1.IndexId] = n1;
        context.AllProxiesMap[n2.IndexId] = n2;
        context.AllProxiesMap[group.IndexId] = group;

        var result = new CoreConfigV2rayService(context).GenerateClientConfigContent();

        result.Success.Should().BeTrue();
        var cfg = JsonUtils.Deserialize<V2rayConfig>(result.Data!.ToString())!;

        cfg.outbounds.Should().Contain(o => o.tag.StartsWith("proxy-1-", StringComparison.Ordinal));
        cfg.outbounds.Should().Contain(o => o.tag.StartsWith("proxy-2-", StringComparison.Ordinal));
        cfg.routing.balancers.Should().NotBeNull();
        cfg.routing.balancers!.Should().Contain(b => b.tag == Global.ProxyTag + Global.BalancerTagSuffix);
    }

    [Fact]
    public void GenerateClientConfigContent_ProxyChain_ShouldBuildDialerProxyChain()
    {
        var config = CoreConfigTestFactory.CreateConfig(ECoreType.Xray);
        CoreConfigTestFactory.BindAppManagerConfig(config);

        var n1 = CoreConfigTestFactory.CreateSocksNode(ECoreType.Xray, "n1", "node-1");
        var n2 = CoreConfigTestFactory.CreateSocksNode(ECoreType.Xray, "n2", "node-2");
        var chain = CoreConfigTestFactory.CreateProxyChainNode(ECoreType.Xray, "c1", "chain", [n1.IndexId, n2.IndexId]);

        var context = CoreConfigTestFactory.CreateContext(config, chain, ECoreType.Xray);
        context.AllProxiesMap[n1.IndexId] = n1;
        context.AllProxiesMap[n2.IndexId] = n2;
        context.AllProxiesMap[chain.IndexId] = chain;

        var result = new CoreConfigV2rayService(context).GenerateClientConfigContent();

        result.Success.Should().BeTrue();
        var cfg = JsonUtils.Deserialize<V2rayConfig>(result.Data!.ToString())!;

        cfg.outbounds.Should().Contain(o => o.tag.StartsWith("chain-proxy-1-", StringComparison.Ordinal));
        var hasDialerChain = cfg.outbounds.Any(o =>
            o.tag == Global.ProxyTag
            && o.streamSettings is not null
            && o.streamSettings.sockopt is not null
            && (o.streamSettings.sockopt.dialerProxy ?? string.Empty).StartsWith("chain-proxy-1-",
                StringComparison.Ordinal));
        hasDialerChain.Should().BeTrue();
    }

    [Fact]
    public void GenerateClientConfigContent_PolicyGroupWithProxyChain_ShouldBuildCombinedOutbounds()
    {
        var config = CoreConfigTestFactory.CreateConfig(ECoreType.Xray);
        CoreConfigTestFactory.BindAppManagerConfig(config);

        var n1 = CoreConfigTestFactory.CreateSocksNode(ECoreType.Xray, "n1", "node-1");
        var n2 = CoreConfigTestFactory.CreateSocksNode(ECoreType.Xray, "n2", "node-2");
        var n3 = CoreConfigTestFactory.CreateSocksNode(ECoreType.Xray, "n3", "node-3");
        var chain = CoreConfigTestFactory.CreateProxyChainNode(ECoreType.Xray, "c1", "chain", [n1.IndexId, n2.IndexId]);
        var group = CoreConfigTestFactory.CreatePolicyGroupNode(ECoreType.Xray, "g1", "group",
            [chain.IndexId, n3.IndexId]);

        var context = CoreConfigTestFactory.CreateContext(config, group, ECoreType.Xray);
        context.AllProxiesMap[n1.IndexId] = n1;
        context.AllProxiesMap[n2.IndexId] = n2;
        context.AllProxiesMap[n3.IndexId] = n3;
        context.AllProxiesMap[chain.IndexId] = chain;
        context.AllProxiesMap[group.IndexId] = group;

        var result = new CoreConfigV2rayService(context).GenerateClientConfigContent();

        result.Success.Should().BeTrue();
        var cfg = JsonUtils.Deserialize<V2rayConfig>(result.Data!.ToString())!;

        cfg.outbounds.Should().Contain(o => o.tag.StartsWith("proxy-1-", StringComparison.Ordinal));
        cfg.outbounds.Should().Contain(o => o.tag.StartsWith("chain-proxy-1-", StringComparison.Ordinal));
        cfg.outbounds.Should().Contain(o => o.tag.StartsWith("proxy-2-", StringComparison.Ordinal));
        cfg.routing.balancers.Should().NotBeNull();
        cfg.routing.balancers!.Should().Contain(b => b.tag == Global.ProxyTag + Global.BalancerTagSuffix);
    }

    [Fact]
    public void GenerateClientConfigContent_ProxyChainWithPolicyGroup_ShouldBuildClonedChainBranches()
    {
        var config = CoreConfigTestFactory.CreateConfig(ECoreType.Xray);
        CoreConfigTestFactory.BindAppManagerConfig(config);

        var n1 = CoreConfigTestFactory.CreateSocksNode(ECoreType.Xray, "n1", "node-1");
        var n2 = CoreConfigTestFactory.CreateSocksNode(ECoreType.Xray, "n2", "node-2");
        var n3 = CoreConfigTestFactory.CreateSocksNode(ECoreType.Xray, "n3", "node-3");
        var group = CoreConfigTestFactory.CreatePolicyGroupNode(ECoreType.Xray, "g1", "group",
            [n1.IndexId, n2.IndexId]);
        var chain = CoreConfigTestFactory.CreateProxyChainNode(ECoreType.Xray, "c1", "chain",
            [group.IndexId, n3.IndexId]);

        var context = CoreConfigTestFactory.CreateContext(config, chain, ECoreType.Xray);
        context.AllProxiesMap[n1.IndexId] = n1;
        context.AllProxiesMap[n2.IndexId] = n2;
        context.AllProxiesMap[n3.IndexId] = n3;
        context.AllProxiesMap[group.IndexId] = group;
        context.AllProxiesMap[chain.IndexId] = chain;

        var result = new CoreConfigV2rayService(context).GenerateClientConfigContent();

        result.Success.Should().BeTrue();
        var cfg = JsonUtils.Deserialize<V2rayConfig>(result.Data!.ToString())!;

        cfg.outbounds.Should().Contain(o => o.tag.StartsWith("chain-proxy-1-group-1-", StringComparison.Ordinal));
        cfg.outbounds.Should().Contain(o => o.tag.StartsWith("chain-proxy-1-group-2-", StringComparison.Ordinal));

        var proxyCloneCount = cfg.outbounds.Count(o => o.tag.StartsWith("proxy-clone-", StringComparison.Ordinal));
        proxyCloneCount.Should().Be(2);

        var allCloneDialersPointToGroupBranches = cfg.outbounds
            .Where(o => o.tag.StartsWith("proxy-clone-", StringComparison.Ordinal))
            .All(o => (o.streamSettings?.sockopt?.dialerProxy ?? string.Empty).StartsWith("chain-proxy-1-group-",
                StringComparison.Ordinal));
        allCloneDialersPointToGroupBranches.Should().BeTrue();

        cfg.routing.balancers.Should().NotBeNull();
        cfg.routing.balancers!.Should().Contain(b => b.tag == Global.ProxyTag + Global.BalancerTagSuffix);
    }

    [Fact]
    public void GenerateClientConfigContent_RoutingSplit_DirectAndBlock_ShouldApplyRules()
    {
        var config = CoreConfigTestFactory.CreateConfig(ECoreType.Xray);
        CoreConfigTestFactory.BindAppManagerConfig(config);

        var node = CoreConfigTestFactory.CreateVmessNode(ECoreType.Xray, "n-main", "main");
        var context = CoreConfigTestFactory.CreateContext(config, node, ECoreType.Xray) with
        {
            RoutingItem = new RoutingItem
            {
                Id = "r-split-1",
                Remarks = "split-direct-block",
                RuleSet = JsonUtils.Serialize(new List<RulesItem>
                {
                    new()
                    {
                        Enabled = true,
                        RuleType = ERuleType.Routing,
                        OutboundTag = Global.DirectTag,
                        Domain = ["full:direct.example.com"],
                    },
                    new()
                    {
                        Enabled = true,
                        RuleType = ERuleType.Routing,
                        OutboundTag = Global.BlockTag,
                        Domain = ["full:block.example.com"],
                    }
                }),
                DomainStrategy = Global.AsIs,
                DomainStrategy4Singbox = string.Empty,
            }
        };

        var result = new CoreConfigV2rayService(context).GenerateClientConfigContent();

        result.Success.Should().BeTrue();
        var cfg = JsonUtils.Deserialize<V2rayConfig>(result.Data!.ToString())!;

        var hasDirectRule = cfg.routing.rules.Any(r =>
            r.domain != null
            && r.domain.Contains("full:direct.example.com")
            && r.outboundTag == Global.DirectTag);
        hasDirectRule.Should().BeTrue();

        var hasBlockRule = cfg.routing.rules.Any(r =>
            r.domain != null
            && r.domain.Contains("full:block.example.com")
            && r.outboundTag == Global.BlockTag);
        hasBlockRule.Should().BeTrue();
    }

    [Fact]
    public void GenerateClientConfigContent_RoutingSplit_ByRemark_ShouldGenerateTargetOutbound()
    {
        var config = CoreConfigTestFactory.CreateConfig(ECoreType.Xray);
        CoreConfigTestFactory.BindAppManagerConfig(config);

        var node = CoreConfigTestFactory.CreateVmessNode(ECoreType.Xray, "n-main", "main");
        var routeNode = CoreConfigTestFactory.CreateSocksNode(ECoreType.Xray, "n-route", "route-node");

        var context = CoreConfigTestFactory.CreateContext(config, node, ECoreType.Xray) with
        {
            RoutingItem = new RoutingItem
            {
                Id = "r-split-2",
                Remarks = "split-remark",
                RuleSet = JsonUtils.Serialize(new List<RulesItem>
                {
                    new()
                    {
                        Enabled = true,
                        RuleType = ERuleType.Routing,
                        OutboundTag = routeNode.Remarks,
                        Domain = ["full:route.example.com"],
                    }
                }),
                DomainStrategy = Global.AsIs,
                DomainStrategy4Singbox = string.Empty,
            }
        };
        context.AllProxiesMap[$"remark:{routeNode.Remarks}"] = routeNode;

        var result = new CoreConfigV2rayService(context).GenerateClientConfigContent();

        result.Success.Should().BeTrue();
        var cfg = JsonUtils.Deserialize<V2rayConfig>(result.Data!.ToString())!;
        var expectedPrefix = $"{routeNode.IndexId}-{Global.ProxyTag}-{routeNode.Remarks}";

        cfg.outbounds.Should().Contain(o => o.tag.StartsWith(expectedPrefix, StringComparison.Ordinal));
        var hasRouteRule = cfg.routing.rules.Any(r =>
            r.domain != null
            && r.domain.Contains("full:route.example.com")
            && (r.outboundTag ?? string.Empty).StartsWith(expectedPrefix, StringComparison.Ordinal));
        hasRouteRule.Should().BeTrue();
    }

    [Fact]
    public void GenerateClientConfigContent_DirectExpectedIPs_ShouldApplyExpectedIPsToDirectDnsServer()
    {
        var config = CoreConfigTestFactory.CreateConfigWithDirectExpectedIPs(ECoreType.Xray, "192.168.0.0/16,geoip:cn");
        CoreConfigTestFactory.BindAppManagerConfig(config);

        var node = CoreConfigTestFactory.CreateVmessNode(ECoreType.Xray, "n-main", "main");
        var context = CoreConfigTestFactory.CreateContext(config, node, ECoreType.Xray) with
        {
            RoutingItem = new RoutingItem
            {
                Id = "r-dns-direct-expected",
                Remarks = "dns-direct-expected",
                RuleSet = JsonUtils.Serialize(new List<RulesItem>
                {
                    new()
                    {
                        Enabled = true,
                        RuleType = ERuleType.DNS,
                        OutboundTag = Global.DirectTag,
                        Domain = ["geosite:cn"],
                    }
                }),
                DomainStrategy = Global.AsIs,
                DomainStrategy4Singbox = string.Empty,
            }
        };

        var result = new CoreConfigV2rayService(context).GenerateClientConfigContent();

        result.Success.Should().BeTrue();
        var cfg = JsonUtils.Deserialize<V2rayConfig>(result.Data!.ToString())!;
        var dns = JsonUtils.Deserialize<Dns4Ray>(JsonUtils.Serialize(cfg.dns))!;

        var dnsServers = dns.servers
            .Select(s => JsonUtils.Deserialize<DnsServer4Ray>(JsonUtils.Serialize(s)))
            .Where(s => s is not null)
            .Cast<DnsServer4Ray>()
            .ToList();

        var hasExpectedServer = dnsServers.Any(s =>
            (s.tag ?? string.Empty).StartsWith(Global.DirectDnsTag, StringComparison.Ordinal)
            && s.domains?.Contains("geosite:cn") == true
            && s.expectedIPs?.Contains("192.168.0.0/16") == true
            && s.expectedIPs?.Contains("geoip:cn") == true);
        hasExpectedServer.Should().BeTrue();
    }

    [Fact]
    public void GenerateClientConfigContent_BootstrapDNS_ShouldApplyToDnsServerDomains()
    {
        var bootstrapDns = "8.8.8.8";
        var config = CoreConfigTestFactory.CreateConfigWithBootstrapDNS(ECoreType.Xray, bootstrapDns);
        config.SimpleDNSItem.DirectDNS = "https://dns-direct.example/dns-query";
        config.SimpleDNSItem.RemoteDNS = "https://dns-remote.example/dns-query";
        CoreConfigTestFactory.BindAppManagerConfig(config);

        var node = CoreConfigTestFactory.CreateVmessNode(ECoreType.Xray, "n-main", "main");
        var context = CoreConfigTestFactory.CreateContext(config, node, ECoreType.Xray);

        var result = new CoreConfigV2rayService(context).GenerateClientConfigContent();

        result.Success.Should().BeTrue();
        var cfg = JsonUtils.Deserialize<V2rayConfig>(result.Data!.ToString())!;
        var dns = JsonUtils.Deserialize<Dns4Ray>(JsonUtils.Serialize(cfg.dns))!;

        var dnsServers = dns.servers
            .Select(s => JsonUtils.Deserialize<DnsServer4Ray>(JsonUtils.Serialize(s)))
            .Where(s => s is not null)
            .Cast<DnsServer4Ray>()
            .ToList();

        var hasBootstrapServer = dnsServers.Any(s =>
            s.address == bootstrapDns
            && s.domains?.Contains("full:dns-direct.example") == true
            && s.domains?.Contains("full:dns-remote.example") == true);
        hasBootstrapServer.Should().BeTrue();
    }

    [Fact]
    public void GenerateClientConfigContent_DnsFallback_LastRuleDirect_ShouldUseDirectDnsServers()
    {
        var config = CoreConfigTestFactory.CreateConfig(ECoreType.Xray);
        config.SimpleDNSItem.DirectDNS = "1.1.1.1";
        config.SimpleDNSItem.RemoteDNS = "9.9.9.9";
        CoreConfigTestFactory.BindAppManagerConfig(config);

        var node = CoreConfigTestFactory.CreateVmessNode(ECoreType.Xray, "n-main", "main");
        var context = CoreConfigTestFactory.CreateContext(config, node, ECoreType.Xray) with
        {
            RoutingItem = new RoutingItem
            {
                Id = "r-direct-final",
                Remarks = "direct-final",
                RuleSet = JsonUtils.Serialize(new List<RulesItem>
                {
                    new()
                    {
                        Enabled = true,
                        RuleType = ERuleType.Routing,
                        OutboundTag = Global.DirectTag,
                        Ip = ["0.0.0.0/0"],
                        Port = "0-65535",
                        Network = "tcp,udp",
                    }
                }),
                DomainStrategy = Global.AsIs,
                DomainStrategy4Singbox = string.Empty,
            }
        };

        var result = new CoreConfigV2rayService(context).GenerateClientConfigContent();

        result.Success.Should().BeTrue();
        var cfg = JsonUtils.Deserialize<V2rayConfig>(result.Data!.ToString())!;
        var dns = JsonUtils.Deserialize<Dns4Ray>(JsonUtils.Serialize(cfg.dns))!;
        var dnsServers = dns.servers
            .Select(s => JsonUtils.Deserialize<DnsServer4Ray>(JsonUtils.Serialize(s)))
            .Where(s => s is not null)
            .Cast<DnsServer4Ray>()
            .ToList();

        var hasDirectFallback = dnsServers.Any(s =>
            (s.tag ?? string.Empty).StartsWith(Global.DirectDnsTag, StringComparison.Ordinal)
            && s.address == "1.1.1.1");
        hasDirectFallback.Should().BeTrue();

        var hasRemoteFallback = dnsServers.Any(s => s.address == "9.9.9.9");
        hasRemoteFallback.Should().BeFalse();
    }

    [Fact]
    public void GenerateClientConfigContent_DirectExpectedIPs_NonMatchingRegion_ShouldNotApplyExpectedIPs()
    {
        var config = CoreConfigTestFactory.CreateConfigWithDirectExpectedIPs(ECoreType.Xray, "192.168.0.0/16,geoip:cn");
        CoreConfigTestFactory.BindAppManagerConfig(config);

        var node = CoreConfigTestFactory.CreateVmessNode(ECoreType.Xray, "n-main", "main");
        var context = CoreConfigTestFactory.CreateContext(config, node, ECoreType.Xray) with
        {
            RoutingItem = new RoutingItem
            {
                Id = "r-dns-direct-unmatched",
                Remarks = "dns-direct-unmatched",
                RuleSet = JsonUtils.Serialize(new List<RulesItem>
                {
                    new()
                    {
                        Enabled = true,
                        RuleType = ERuleType.DNS,
                        OutboundTag = Global.DirectTag,
                        Domain = ["geosite:us"],
                    }
                }),
                DomainStrategy = Global.AsIs,
                DomainStrategy4Singbox = string.Empty,
            }
        };

        var result = new CoreConfigV2rayService(context).GenerateClientConfigContent();

        result.Success.Should().BeTrue();
        var cfg = JsonUtils.Deserialize<V2rayConfig>(result.Data!.ToString())!;
        var dns = JsonUtils.Deserialize<Dns4Ray>(JsonUtils.Serialize(cfg.dns))!;
        var dnsServers = dns.servers
            .Select(s => JsonUtils.Deserialize<DnsServer4Ray>(JsonUtils.Serialize(s)))
            .Where(s => s is not null)
            .Cast<DnsServer4Ray>()
            .ToList();

        var hasExpectedIPs = dnsServers.Any(s =>
            s.expectedIPs?.Contains("192.168.0.0/16") == true
            || s.expectedIPs?.Contains("geoip:cn") == true);
        hasExpectedIPs.Should().BeFalse();
    }

    [Theory]
    [InlineData("geosite:cn")]
    [InlineData("geosite:geolocation-cn")]
    [InlineData("geosite:tld-cn")]
    public void GenerateClientConfigContent_DirectExpectedIPs_RegionVariant_ShouldApplyExpectedIPs(string domainTag)
    {
        var config = CoreConfigTestFactory.CreateConfigWithDirectExpectedIPs(ECoreType.Xray, "192.168.0.0/16,geoip:cn");
        CoreConfigTestFactory.BindAppManagerConfig(config);

        var node = CoreConfigTestFactory.CreateVmessNode(ECoreType.Xray, "n-main", "main");
        var context = CoreConfigTestFactory.CreateContext(config, node, ECoreType.Xray) with
        {
            RoutingItem = new RoutingItem
            {
                Id = "r-dns-direct-variant",
                Remarks = "dns-direct-variant",
                RuleSet = JsonUtils.Serialize(new List<RulesItem>
                {
                    new()
                    {
                        Enabled = true, RuleType = ERuleType.DNS, OutboundTag = Global.DirectTag, Domain = [domainTag],
                    }
                }),
                DomainStrategy = Global.AsIs,
                DomainStrategy4Singbox = string.Empty,
            }
        };

        var result = new CoreConfigV2rayService(context).GenerateClientConfigContent();

        result.Success.Should().BeTrue();
        var cfg = JsonUtils.Deserialize<V2rayConfig>(result.Data!.ToString())!;
        var dns = JsonUtils.Deserialize<Dns4Ray>(JsonUtils.Serialize(cfg.dns))!;
        var dnsServers = dns.servers
            .Select(s => JsonUtils.Deserialize<DnsServer4Ray>(JsonUtils.Serialize(s)))
            .Where(s => s is not null)
            .Cast<DnsServer4Ray>()
            .ToList();

        var hasExpectedServer = dnsServers.Any(s =>
            (s.tag ?? string.Empty).StartsWith(Global.DirectDnsTag, StringComparison.Ordinal)
            && s.domains?.Contains(domainTag) == true
            && s.expectedIPs?.Contains("192.168.0.0/16") == true
            && s.expectedIPs?.Contains("geoip:cn") == true);
        hasExpectedServer.Should().BeTrue();
    }

    [Fact]
    public void GenerateClientConfigContent_Hosts_ShouldPopulateDnsHosts()
    {
        var config = CoreConfigTestFactory.CreateConfig(ECoreType.Xray);
        config.SimpleDNSItem.Hosts = "resolver.example 1.1.1.1";
        CoreConfigTestFactory.BindAppManagerConfig(config);

        var node = CoreConfigTestFactory.CreateVmessNode(ECoreType.Xray, "n-main", "main");
        var context = CoreConfigTestFactory.CreateContext(config, node, ECoreType.Xray);

        var result = new CoreConfigV2rayService(context).GenerateClientConfigContent();

        result.Success.Should().BeTrue();
        var cfg = JsonUtils.Deserialize<V2rayConfig>(result.Data!.ToString())!;
        var dns = JsonUtils.Deserialize<Dns4Ray>(JsonUtils.Serialize(cfg.dns))!;

        dns.hosts.Should().NotBeNull();
        dns.hosts!.Should().ContainKey("resolver.example");
        JsonUtils.Serialize(dns.hosts!["resolver.example"]).Should().Contain("1.1.1.1");
    }

    [Fact]
    public void GenerateClientConfigContent_RawDnsEnabled_ShouldUseCustomDnsConfig()
    {
        var config = CoreConfigTestFactory.CreateConfig(ECoreType.Xray);
        CoreConfigTestFactory.BindAppManagerConfig(config);

        var node = CoreConfigTestFactory.CreateVmessNode(ECoreType.Xray, "n-main", "main");
        var context = CoreConfigTestFactory.CreateContext(config, node, ECoreType.Xray) with
        {
            RawDnsItem = new DNSItem
            {
                Id = "dns-raw-1",
                Remarks = "raw",
                Enabled = true,
                CoreType = ECoreType.Xray,
                NormalDNS = "{\"servers\":[\"8.8.8.8\"],\"hosts\":{\"raw.example\":\"1.1.1.1\"}}",
                DomainStrategy4Freedom = "UseIPv4",
            }
        };

        var result = new CoreConfigV2rayService(context).GenerateClientConfigContent();

        result.Success.Should().BeTrue();
        var cfg = JsonUtils.Deserialize<V2rayConfig>(result.Data!.ToString())!;
        var dns = JsonUtils.Deserialize<Dns4Ray>(JsonUtils.Serialize(cfg.dns))!;

        JsonUtils.Serialize(dns.servers).Should().Contain("8.8.8.8");
        dns.hosts.Should().NotBeNull();
        dns.hosts!.Should().ContainKey("raw.example");
        JsonUtils.Serialize(dns.hosts!["raw.example"]).Should().Contain("1.1.1.1");

        var directOutbound = cfg.outbounds.FirstOrDefault(o => o.tag == Global.DirectTag && o.protocol == "freedom");
        directOutbound.Should().NotBeNull();
        directOutbound!.settings.domainStrategy.Should().Be("UseIPv4");
    }

    #region Adaptive Scheduling — Active-Set Balancer Tests

    /// <summary>
    /// Active-set balancer: each active tag appears exactly once in the selector.
    /// Scores do NOT affect selector duplication — xray deduplicates entries anyway
    /// (verified: xray v26.3.27, selector [A,A,A,B] → A=47.6% ≈ uniform).
    /// The system is an Adaptive Active-Set Scheduler: bad nodes are ejected,
    /// nodes within the active set share traffic uniformly via xray random balancer.
    /// </summary>
    [Fact]
    public void GenerateClientConfigContent_AdaptiveConfig_ShouldUseActiveSetUniformRandom()
    {
        var config = CoreConfigTestFactory.CreateConfig(ECoreType.Xray);
        CoreConfigTestFactory.BindAppManagerConfig(config);

        var n1 = CoreConfigTestFactory.CreateSocksNode(ECoreType.Xray, "n1", "node-fast");
        var n2 = CoreConfigTestFactory.CreateSocksNode(ECoreType.Xray, "n2", "node-slow");
        var group = CoreConfigTestFactory.CreatePolicyGroupNode(ECoreType.Xray, "g1", "group",
            [n1.IndexId, n2.IndexId]);

        var context = CoreConfigTestFactory.CreateContext(config, group, ECoreType.Xray);
        context.AllProxiesMap[n1.IndexId] = n1;
        context.AllProxiesMap[n2.IndexId] = n2;
        context.AllProxiesMap[group.IndexId] = group;

        // First generate without adaptive to get the tag names
        var result = new CoreConfigV2rayService(context).GenerateClientConfigContent();
        var cfg = JsonUtils.Deserialize<V2rayConfig>(result.Data!.ToString())!;
        var proxyOutbounds = cfg.outbounds.Where(o => o.tag.StartsWith(Global.ProxyTag + "-")).ToList();
        proxyOutbounds.Should().HaveCount(2);

        var fastTag = proxyOutbounds[0].tag;
        var slowTag = proxyOutbounds[1].tag;

        // Active-set config: both nodes active, scores are high/low — no duplication
        context = context with
        {
            AdaptiveConfig = new AdaptiveConfig
            {
                ActiveTags = [fastTag, slowTag],
                CooldownTags = [],
                ProbePorts = new Dictionary<string, int>(),
                NodeScores = new Dictionary<string, double>
                {
                    [fastTag] = 90.0,
                    [slowTag] = 30.0,
                },
                TagToIndexId = new Dictionary<string, string>
                {
                    [fastTag] = n1.IndexId,
                    [slowTag] = n2.IndexId,
                },
            }
        };

        result = new CoreConfigV2rayService(context).GenerateClientConfigContent();
        result.Success.Should().BeTrue();
        cfg = JsonUtils.Deserialize<V2rayConfig>(result.Data!.ToString())!;

        cfg.routing.balancers.Should().NotBeNull("adaptive config should produce a balancer");
        var balancer = cfg.routing.balancers!.FirstOrDefault(b => b.tag == Global.ProxyTag + Global.BalancerTagSuffix);
        balancer.Should().NotBeNull("balancer with proxy-round tag should exist");
        balancer!.selector.Should().NotBeNull();
        balancer.selector.Should().HaveCount(2, "each active tag appears exactly once — no duplication");

        balancer.selector.Should().Contain(fastTag);
        balancer.selector.Should().Contain(slowTag);

        balancer.strategy!.type.Should().Be("random");
    }

    /// <summary>
    /// Multiple active nodes produce a selector with one entry each (uniform random).
    /// Scores are irrelevant to selector construction.
    /// </summary>
    [Fact]
    public void GenerateClientConfigContent_AdaptiveConfig_MultipleActiveNodes_ShouldEachAppearOnce()
    {
        var config = CoreConfigTestFactory.CreateConfig(ECoreType.Xray);
        CoreConfigTestFactory.BindAppManagerConfig(config);

        var n1 = CoreConfigTestFactory.CreateSocksNode(ECoreType.Xray, "n1", "node-a");
        var n2 = CoreConfigTestFactory.CreateSocksNode(ECoreType.Xray, "n2", "node-b");
        var group = CoreConfigTestFactory.CreatePolicyGroupNode(ECoreType.Xray, "g1", "group",
            [n1.IndexId, n2.IndexId]);

        var context = CoreConfigTestFactory.CreateContext(config, group, ECoreType.Xray);
        context.AllProxiesMap[n1.IndexId] = n1;
        context.AllProxiesMap[n2.IndexId] = n2;
        context.AllProxiesMap[group.IndexId] = group;

        // First pass to get tag names
        var result = new CoreConfigV2rayService(context).GenerateClientConfigContent();
        var cfg = JsonUtils.Deserialize<V2rayConfig>(result.Data!.ToString())!;
        var proxyOutbounds = cfg.outbounds.Where(o => o.tag.StartsWith(Global.ProxyTag + "-")).ToList();
        var tagA = proxyOutbounds[0].tag;
        var tagB = proxyOutbounds[1].tag;

        context = context with
        {
            AdaptiveConfig = new AdaptiveConfig
            {
                ActiveTags = [tagA, tagB],
                CooldownTags = [],
                ProbePorts = new Dictionary<string, int>(),
                NodeScores = new Dictionary<string, double> { [tagA] = 80.0, [tagB] = 80.0 },
                TagToIndexId = new Dictionary<string, string> { [tagA] = n1.IndexId, [tagB] = n2.IndexId },
            }
        };

        result = new CoreConfigV2rayService(context).GenerateClientConfigContent();
        cfg = JsonUtils.Deserialize<V2rayConfig>(result.Data!.ToString())!;

        var balancer = cfg.routing.balancers!.First(b => b.tag == Global.ProxyTag + Global.BalancerTagSuffix);
        balancer.selector.Should().HaveCount(2, "each active node appears exactly once");
        balancer.selector.Should().Contain(tagA);
        balancer.selector.Should().Contain(tagB);
        balancer.strategy!.type.Should().Be("random");
    }

    /// <summary>
    /// Nodes in cooldown should be excluded from the balancer selector entirely.
    /// </summary>
    [Fact]
    public void GenerateClientConfigContent_AdaptiveConfig_CooldownNode_ShouldBeExcluded()
    {
        var config = CoreConfigTestFactory.CreateConfig(ECoreType.Xray);
        CoreConfigTestFactory.BindAppManagerConfig(config);

        var n1 = CoreConfigTestFactory.CreateSocksNode(ECoreType.Xray, "n1", "node-active");
        var n2 = CoreConfigTestFactory.CreateSocksNode(ECoreType.Xray, "n2", "node-cooldown");
        var n3 = CoreConfigTestFactory.CreateSocksNode(ECoreType.Xray, "n3", "node-active2");
        var group = CoreConfigTestFactory.CreatePolicyGroupNode(ECoreType.Xray, "g1", "group",
            [n1.IndexId, n2.IndexId, n3.IndexId]);

        var context = CoreConfigTestFactory.CreateContext(config, group, ECoreType.Xray);
        context.AllProxiesMap[n1.IndexId] = n1;
        context.AllProxiesMap[n2.IndexId] = n2;
        context.AllProxiesMap[n3.IndexId] = n3;
        context.AllProxiesMap[group.IndexId] = group;

        var result = new CoreConfigV2rayService(context).GenerateClientConfigContent();
        var cfg = JsonUtils.Deserialize<V2rayConfig>(result.Data!.ToString())!;
        var tags = cfg.outbounds.Where(o => o.tag.StartsWith(Global.ProxyTag + "-")).Select(o => o.tag).ToList();
        tags.Should().HaveCount(3);
        var active1 = tags[0];
        var cooldown = tags[1];
        var active2 = tags[2];

        context = context with
        {
            AdaptiveConfig = new AdaptiveConfig
            {
                ActiveTags = [active1, active2],
                CooldownTags = [cooldown],
                ProbePorts = new Dictionary<string, int>(),
                NodeScores = new Dictionary<string, double>
                {
                    [active1] = 80.0,
                    [active2] = 80.0,
                    [cooldown] = 5.0,
                },
                TagToIndexId = new Dictionary<string, string>
                {
                    [active1] = n1.IndexId,
                    [cooldown] = n2.IndexId,
                    [active2] = n3.IndexId,
                },
            }
        };

        result = new CoreConfigV2rayService(context).GenerateClientConfigContent();
        cfg = JsonUtils.Deserialize<V2rayConfig>(result.Data!.ToString())!;

        var balancer = cfg.routing.balancers!.First(b => b.tag == Global.ProxyTag + Global.BalancerTagSuffix);
        balancer.selector.Should().NotContain(cooldown, "cooldown node should be excluded from selector");
        balancer.selector.Should().Contain(active1);
        balancer.selector.Should().Contain(active2);
    }

    #endregion
}
