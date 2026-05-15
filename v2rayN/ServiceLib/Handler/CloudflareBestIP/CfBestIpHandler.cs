namespace ServiceLib.Handler.CloudflareBestIP;

public class CfBestIpHandler
{
    private static readonly Lazy<CfBestIpHandler> _instance = new(() => new());
    public static CfBestIpHandler Instance => _instance.Value;

    public async Task RunAsync(Config config, Action<bool, string> onUpdate)
    {
        try
        {
            var cfConfig = config.CfBestIpItem;
            if (cfConfig == null)
            {
                onUpdate(true, "Cloudflare prefer IP config is null, please configure first.");
                return;
            }

            if (cfConfig.OriginSniList.Count == 0)
            {
                onUpdate(true, "Origin SNI list is required. Please configure in settings.");
                return;
            }
            if (cfConfig.OriginTestPath.IsNullOrEmpty())
            {
                onUpdate(true, "Origin test path is required. Please configure in settings.");
                return;
            }
            if (cfConfig.OriginSpeedTestPath.IsNullOrEmpty())
            {
                onUpdate(true, "Origin speed test path is required. Please configure in settings.");
                return;
            }
            if (cfConfig.DomainsSetUrl.IsNullOrEmpty())
            {
                onUpdate(true, "Domains set URL is required. Please configure in settings.");
                return;
            }

            onUpdate(false, $"{"=".PadRight(55, '=')}");
            onUpdate(false, $"  Cloudflare Best IP Probe");
            onUpdate(false, $"  Protocol: {cfConfig.SelectedProtocol?.ToUpperInvariant() ?? "VLESS"} | Mode: {cfConfig.ProbeMode}");
            onUpdate(false, $"  Repeat: {cfConfig.ProbeRepeat} | Timeout: {cfConfig.Timeout}s | TopN: {cfConfig.TopN}");
            onUpdate(false, $"{"=".PadRight(55, '=')}");

            // Step 1: Fetch data sources
            onUpdate(false, "[1/4] Fetching IP sources...");
            var fetcher = new CfDataFetcher(cfConfig);
            var ips = await fetcher.FetchAllIpsAsync(config, msg => onUpdate(false, $"  {msg}"));
            if (ips.Count == 0)
            {
                onUpdate(true, "No IPs found from data sources. Please check URLs.");
                return;
            }
            onUpdate(false, $"  Total IPs after dedup & CIDR filter: {ips.Count}");

            // Step 2: Batch probe
            onUpdate(false, $"[2/4] Probing {ips.Count} IPs (concurrency: {(Utils.IsWindows() ? 20 : 10)})...");
            var batchProber = new CfIpBatchProber(cfConfig);
            var results = await batchProber.ProbeAllAsync(ips, msg => onUpdate(false, $"  {msg}"));
            if (results.Count == 0)
            {
                onUpdate(true, "No successful probe results.");
                return;
            }
            var avgLatency = results.Average(r => r.AvgLatencyMs);
            var avgSpeed = results.Average(r => r.DownloadSpeedKBs);
            onUpdate(false, $"  Probing done. Success: {results.Count} | Avg latency: {avgLatency:F1}ms | Avg speed: {avgSpeed:F0}KB/s");

            // Step 3: Score and rank
            onUpdate(false, "[3/4] Scoring and ranking...");
            var scorer = new CfIpScorer(cfConfig);
            var topResults = scorer.ScoreAndRank(results);

            onUpdate(false, $"{"=".PadRight(55, '=')}");
            onUpdate(false, $"  TOP {topResults.Count} Results:");
            onUpdate(false, $"  {"IP",-18} {"Colo",-8} {"Region",-16} {"Latency",-10} {"Speed",-10} {"Score",-8}");
            onUpdate(false, $"  {"-" .PadRight(18, '-')} {"-" .PadRight(8, '-')} {"-" .PadRight(16, '-')} {"-" .PadRight(10, '-')} {"-" .PadRight(10, '-')} {"-" .PadRight(8, '-')}");
            foreach (var r in topResults)
            {
                onUpdate(false,
                    $"  {r.Ip,-18} {r.Colo ?? "?",-8} {r.Region ?? "?",-16} {r.AvgLatencyMs,8:F1}ms {r.DownloadSpeedKBs,8:F0}KB/s {r.Score,6:F1}");
            }
            onUpdate(false, $"{"=".PadRight(55, '=')}");

            // Step 4: Export as ProfileItem nodes and add to group
            onUpdate(false, "[4/4] Generating nodes & adding to [CF优选] group...");
            var exporter = new CfResultExporter(cfConfig);
            var nodes = exporter.ExportAsProfileItems(topResults);

            await AddNodesToGroupAsync(nodes, onUpdate);

            onUpdate(false, $"Done! {nodes.Count} nodes added to [CF优选] group.");
        }
        catch (Exception ex)
        {
            Logging.SaveLog("CfBestIpHandler", ex);
            onUpdate(true, $"Error: {ex.Message}");
        }
    }

    private static async Task AddNodesToGroupAsync(List<ProfileItem> nodes, Action<bool, string> onUpdate)
    {
        var config = AppManager.Instance.Config;
        var groupName = "CF优选";

        // Find existing group
        var existingItems = await AppManager.Instance.ProfileItems(string.Empty);
        var groupItem = existingItems?
            .FirstOrDefault(t => t.ConfigType is EConfigType.PolicyGroup && t.Remarks == groupName);

        if (groupItem == null)
        {
            groupItem = new ProfileItem
            {
                IndexId = string.Empty,
                ConfigType = EConfigType.PolicyGroup,
                ConfigVersion = 4,
                Remarks = groupName,
                Address = groupName,
                IsSub = false,
            };
            var proto = groupItem.GetProtocolExtra();
            proto = proto with { GroupType = nameof(EConfigType.PolicyGroup) };
            groupItem.SetProtocolExtra(proto);

            await ConfigHandler.AddServerCommon(config, groupItem);
        }

        // Add nodes using proper API
        var insertedIds = new List<string>();
        foreach (var node in nodes)
        {
            node.IndexId = string.Empty;
            await ConfigHandler.AddServerCommon(config, node);
            insertedIds.Add(node.IndexId);
        }

        // Update group children with inserted node IDs
        var groupProto = groupItem.GetProtocolExtra();
        var childIds = Utils.String2List(groupProto.ChildItems);
        childIds.AddRange(insertedIds);
        groupProto = groupProto with { ChildItems = Utils.List2String(childIds.Distinct().ToList()) };
        groupItem.SetProtocolExtra(groupProto);
        await SQLiteHelper.Instance.UpdateAsync(groupItem);

        onUpdate(false, $"Added {nodes.Count} nodes to group [{groupName}]");
    }
}
