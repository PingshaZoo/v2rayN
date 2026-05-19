namespace ServiceLib.Handler.CloudflareBestIP;

/// <summary>
/// 探测结果 → ProfileItem 节点导出器
/// Probe result → ProfileItem node exporter
///
/// 根据用户选择的协议模板（VLESS/VMess/Trojan），将优选 IP 组装为可被 v2rayN 识别的节点配置
/// Assembles preferred IPs into v2rayN-compatible node configs based on user-selected protocol template
///
/// 所有协议共用配置 / Common config for all protocols:
///   - address = 优选 IP / preferred IP
///   - port = 443
///   - network = ws (WebSocket)
///   - security = tls
///   - sni = 用户配置的源站域名 / user-configured origin domain
///   - fingerprint = chrome
///   - alpn = h2,http/1.1
/// </summary>
public class CfResultExporter
{
    private readonly CfBestIpItem _config;

    public CfResultExporter(CfBestIpItem config)
    {
        _config = config;
    }

    /// <summary>
    /// 将 TOP N 探测结果批量导出为 ProfileItem 节点
    /// Export TOP N probe results as ProfileItem nodes
    /// </summary>
    public List<ProfileItem> ExportAsProfileItems(List<CfProbeResult> topResults)
    {
        var nodes = new List<ProfileItem>();

        foreach (var result in topResults)
        {
            var node = CreateNodeForProtocol(result);
            if (node != null) nodes.Add(node);
        }

        return nodes;
    }

    /// <summary>
    /// 根据用户选择的协议创建对应类型的节点
    /// Create protocol-specific node based on user selection
    ///
    /// 支持的协议 / Supported protocols:
    ///   - vless: VLESS + WebSocket + TLS（默认/default）
    ///   - vmess: VMess + WebSocket + TLS
    ///   - trojan: Trojan + WebSocket + TLS
    /// </summary>
    /// <param name="result">优选 IP 探测结果 / preferred IP probe result</param>
    private ProfileItem? CreateNodeForProtocol(CfProbeResult result)
    {
        var protocol = _config.SelectedProtocol?.ToLowerInvariant() ?? "vless";
        var sniHost = _config.OriginSniList?.FirstOrDefault() ?? result.Ip;
        var uuid = _config.Uuid.IsNotEmpty() ? _config.Uuid : Utils.GetGuid();
        var wsPath = _config.WsPath.IsNotEmpty() ? _config.WsPath : "/";

        return protocol switch
        {
            "vless" => CreateVlessNode(result, sniHost, uuid, wsPath),
            "vmess" => CreateVmessNode(result, sniHost, uuid, wsPath),
            "trojan" => CreateTrojanNode(result, sniHost, uuid, wsPath),
            _ => CreateVlessNode(result, sniHost, uuid, wsPath),
        };
    }

    /// <summary>
    /// 创建 VLESS + WebSocket + TLS 节点
    /// VLESS 无需 alterId 和 vmessSecurity，需设置 VlessEncryption = "none"
    /// </summary>
    private static ProfileItem CreateVlessNode(CfProbeResult result, string sniHost, string uuid, string wsPath)
    {
        var remarks = $"CF-{result.Ip}";

        var node = new ProfileItem
        {
            IndexId = string.Empty,
            ConfigType = EConfigType.VLESS,
            ConfigVersion = 4,
            Remarks = remarks,
            Address = result.Ip,
            Port = 443,
            Password = uuid,
            Username = uuid,
            Network = nameof(ETransport.ws),
            StreamSecurity = Global.StreamSecurity,
            Sni = sniHost,
            AllowInsecure = "false",
            Fingerprint = "chrome",
            Alpn = "h2,http/1.1",
            IsSub = false,
        };

        // 传输层配置：WebSocket Host + Path
        // Transport layer: WebSocket Host + Path
        var transport = node.GetTransportExtra();
        transport = transport with
        {
            Host = sniHost,
            Path = wsPath,
        };
        node.SetTransportExtra(transport);

        // 协议层配置：VLESS encryption = none
        // Protocol layer: VLESS encryption = none
        var proto = node.GetProtocolExtra();
        proto = proto with
        {
            VlessEncryption = Global.None,
            Flow = string.Empty,
        };
        node.SetProtocolExtra(proto);

        return node;
    }

    /// <summary>
    /// 创建 VMess + WebSocket + TLS 节点
    /// VMess 额外需要 alterId=0 和 vmessSecurity=auto
    /// </summary>
    private static ProfileItem CreateVmessNode(CfProbeResult result, string sniHost, string uuid, string wsPath)
    {
        var remarks = $"CF-{result.Ip}";

        var node = new ProfileItem
        {
            IndexId = string.Empty,
            ConfigType = EConfigType.VMess,
            ConfigVersion = 4,
            Remarks = remarks,
            Address = result.Ip,
            Port = 443,
            Password = uuid,
            Network = nameof(ETransport.ws),
            StreamSecurity = Global.StreamSecurity,
            Sni = sniHost,
            AllowInsecure = "false",
            Fingerprint = "chrome",
            Alpn = "h2,http/1.1",
            IsSub = false,
        };

        var transport = node.GetTransportExtra();
        transport = transport with
        {
            Host = sniHost,
            Path = wsPath,
        };
        node.SetTransportExtra(transport);

        // VMess 特有字段：alterId 和 security
        // VMess-specific fields: alterId and security
        var proto = node.GetProtocolExtra();
        proto = proto with
        {
            AlterId = "0",
            VmessSecurity = Global.DefaultSecurity,
        };
        node.SetProtocolExtra(proto);

        return node;
    }

    /// <summary>
    /// 创建 Trojan + WebSocket + TLS 节点
    /// Trojan 协议无额外字段，仅 password 不同
    /// </summary>
    private static ProfileItem CreateTrojanNode(CfProbeResult result, string sniHost, string uuid, string wsPath)
    {
        var remarks = $"CF-{result.Ip}";

        var node = new ProfileItem
        {
            IndexId = string.Empty,
            ConfigType = EConfigType.Trojan,
            ConfigVersion = 4,
            Remarks = remarks,
            Address = result.Ip,
            Port = 443,
            Password = uuid,
            Network = nameof(ETransport.ws),
            StreamSecurity = Global.StreamSecurity,
            Sni = sniHost,
            AllowInsecure = "false",
            Fingerprint = "chrome",
            Alpn = "h2,http/1.1",
            IsSub = false,
        };

        var transport = node.GetTransportExtra();
        transport = transport with
        {
            Host = sniHost,
            Path = wsPath,
        };
        node.SetTransportExtra(transport);

        // Trojan 无额外协议字段 / Trojan has no extra protocol fields

        return node;
    }
}
