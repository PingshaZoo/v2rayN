namespace ServiceLib.Handler.CloudflareBestIP;

public class CfResultExporter(CfBestIpItem settings)
{
    public const string GroupId = "cf-best-ip-generated";

    public List<ProfileItem> ExportAsProfileItems(IEnumerable<CfProbeResult> topResults, ProfileItem? template = null)
    {
        if (template is not null) ValidateTemplate(template);
        else ValidateManualCredentials();
        return topResults.Where(r => CfIpScorer.IsQualified(r, settings))
            .Select(r => CreateNode(r, template)).ToList();
    }

    private void ValidateManualCredentials()
    {
        if (settings.SelectedProtocol is not ("vless" or "vmess" or "trojan"))
            throw new ArgumentException(CfUi.T("请选择 VLESS、VMess 或 Trojan 协议。", "Choose VLESS, VMess or Trojan."));
        if (string.IsNullOrWhiteSpace(settings.Uuid))
            throw new ArgumentException(CfUi.T("请填写连接凭据，或选择已有 TLS 节点作为模板。", "Enter credentials or select an existing TLS profile."));
        if (settings.SelectedProtocol is "vless" or "vmess" && !Guid.TryParse(settings.Uuid, out _))
            throw new ArgumentException(CfUi.T("VLESS / VMess 凭据需要有效 UUID。", "VLESS / VMess require a valid UUID."));
        if (string.IsNullOrWhiteSpace(settings.WsPath) || !settings.WsPath.StartsWith('/'))
            throw new ArgumentException(CfUi.T("WebSocket 路径必须以 / 开头。", "The WebSocket path must start with /."));
    }

    public static void ValidateTemplate(ProfileItem template)
    {
        if (template.ConfigType is not (EConfigType.VLESS or EConfigType.VMess or EConfigType.Trojan)
            || template.StreamSecurity != Global.StreamSecurity || !template.IsValid())
            throw new ArgumentException(CfUi.T("模板必须是有效的 VLESS、VMess 或 Trojan TLS 节点。", "Select a valid VLESS, VMess or Trojan TLS profile."));
    }

    private ProfileItem CreateNode(CfProbeResult result, ProfileItem? template)
    {
        if (template is not null && !string.IsNullOrWhiteSpace(template.Sni)
            && !string.Equals(template.Sni, result.Sni, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException(CfUi.T($"模板 SNI {template.Sni} 与已测速的 {result.Sni} 不一致。",
                $"Template SNI {template.Sni} does not match tested SNI {result.Sni}."));
        var node = template is null ? CreateManualNode(result) : JsonUtils.DeepCopy(template)
            ?? throw new InvalidOperationException(CfUi.T("复制模板失败", "Could not copy the template"));
        node.IndexId = string.Empty;
        node.Subid = GroupId;
        node.Address = result.Ip;
        node.Port = result.Port;
        node.Sni = result.Sni;
        node.Remarks = $"CF {result.Colo} {result.Ip}";
        node.IsSub = true;
        return node;
    }

    private ProfileItem CreateManualNode(CfProbeResult result)
    {
        var node = new ProfileItem
        {
            ConfigType = settings.SelectedProtocol switch
            {
                "vless" => EConfigType.VLESS,
                "vmess" => EConfigType.VMess,
                _ => EConfigType.Trojan,
            },
            Address = result.Ip, Port = result.Port,
            Password = settings.Uuid, Username = settings.Uuid,
            Network = nameof(ETransport.ws), StreamSecurity = Global.StreamSecurity,
            Sni = result.Sni, AllowInsecure = "false", Fingerprint = "chrome",
            Alpn = "h2,http/1.1", IsSub = true, Subid = GroupId
        };
        var transport = node.GetTransportExtra();
        node.SetTransportExtra(transport with { Host = result.Sni, Path = settings.WsPath });
        var protocol = node.GetProtocolExtra();
        if (node.ConfigType == EConfigType.VLESS)
            node.SetProtocolExtra(protocol with { VlessEncryption = Global.None, Flow = string.Empty });
        else if (node.ConfigType == EConfigType.VMess)
            node.SetProtocolExtra(protocol with { AlterId = "0", VmessSecurity = Global.DefaultSecurity });
        return node;
    }

    public async Task<int> ImportAsync(Config config, CfProbeRunResult run, ProfileItem? template = null,
        CancellationToken cancellationToken = default)
    {
        var nodes = ExportAsProfileItems(run.TopResults, template);
        if (nodes.Count == 0) return 0;
        var app = AppManager.Instance;
        if (await app.GetSubItem(GroupId) is null)
        {
            var maxSort = (await app.SubItems())?.Select(item => item.Sort).DefaultIfEmpty(0).Max() ?? 0;
            var group = new SubItem { Id = GroupId, Remarks = CfUi.T("Cloudflare 优选", "Cloudflare Best IP"), Url = string.Empty,
                Enabled = false, Sort = maxSort + 1,
                Memo = CfUi.T("手动测速导入；不会自动更新", "Imported manually; never auto-updated") };
            await ConfigHandler.AddSubItem(config, group);
        }
        var existing = (await app.ProfileItems(GroupId) ?? [])
            .ToDictionary(n => n.IndexId, StringComparer.Ordinal);
        var count = 0;
        for (var i = 0; i < nodes.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var node = nodes[i];
            var result = run.TopResults[i];
            node.IndexId = StableNodeId(node, template?.IndexId ?? "manual");
            if (!existing.ContainsKey(node.IndexId))
            {
                await ConfigHandler.AddServerCommon(config, node);
                count++;
            }
            ProfileExManager.Instance.SetTestDelay(node.IndexId, (int)Math.Round(result.AvgLatencyMs));
            ProfileExManager.Instance.SetTestSpeed(node.IndexId, (decimal)(result.DownloadSpeedKBs * 1024 / 1_000_000));
        }
        return count;
    }

    public static string StableNodeId(ProfileItem node, string templateId)
    {
        var identity = $"{templateId}|{node.ConfigType}|{node.Address}|{node.Port}|{node.Sni}|{node.Network}|{node.StreamSecurity}|{node.Password}|{JsonSerializer.Serialize(node.GetTransportExtra())}";
        return "cf-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..24];
    }
}
