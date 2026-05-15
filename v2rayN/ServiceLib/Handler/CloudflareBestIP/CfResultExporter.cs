namespace ServiceLib.Handler.CloudflareBestIP;

public class CfResultExporter
{
    private readonly CfBestIpItem _config;

    public CfResultExporter(CfBestIpItem config)
    {
        _config = config;
    }

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

        var transport = node.GetTransportExtra();
        transport = transport with
        {
            Host = sniHost,
            Path = wsPath,
        };
        node.SetTransportExtra(transport);

        var proto = node.GetProtocolExtra();
        proto = proto with
        {
            VlessEncryption = Global.None,
            Flow = string.Empty,
        };
        node.SetProtocolExtra(proto);

        return node;
    }

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

        var proto = node.GetProtocolExtra();
        proto = proto with
        {
            AlterId = "0",
            VmessSecurity = Global.DefaultSecurity,
        };
        node.SetProtocolExtra(proto);

        return node;
    }

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

        return node;
    }
}
