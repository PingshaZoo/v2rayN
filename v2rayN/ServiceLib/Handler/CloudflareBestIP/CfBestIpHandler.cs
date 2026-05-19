namespace ServiceLib.Handler.CloudflareBestIP;

/// <summary>
/// Cloudflare 优选 IP 主流程编排器（单例）
/// Cloudflare Best IP main orchestrator (singleton)
///
/// 两阶段流水线 / Two-phase pipeline:
///   Phase 1: HavePostRes 历史优结果（前 100 个，10 个速度达标即早停）→ 立刻入组
///   Phase 2: IpSetUrls + DomainsSetUrl 常规 IP → 跳过 Phase 1 已探测 → 追加到同一分组
///
/// Phase 1: top 100 historical best IPs (early stop when 10 pass speed test) → add immediately
/// Phase 2: general IPs (skip Phase-1 probed) → append to same group
/// </summary>
public class CfBestIpHandler
{
    private static readonly Lazy<CfBestIpHandler> _instance = new(() => new());
    public static CfBestIpHandler Instance => _instance.Value;

    private const int Phase1MaxIps = 100;
    private const int Phase1SpeedPassTarget = 10;
    private const int Phase1BatchSize = 10;

    public async Task RunAsync(Config config, Action<bool, string> onUpdate)
    {
        try
        {
            var cfConfig = config.CfBestIpItem;

            // ── 前置校验 / Pre-flight validation ──
            if (cfConfig == null)
            {
                onUpdate(true, "Cloudflare prefer IP config is null, please configure first.");
                return;
            }
            if (cfConfig.OriginSniList.Count == 0 || cfConfig.OriginTestPath.IsNullOrEmpty() ||
                cfConfig.OriginSpeedTestPath.IsNullOrEmpty() || cfConfig.DomainsSetUrl.IsNullOrEmpty())
            {
                onUpdate(true, "Required fields missing. Please check: Origin SNI, Test Path, Speed Test Path, Domains URL.");
                return;
            }

            // ── 打印运行参数头 / Print run header ──
            onUpdate(false, $"{"=".PadRight(55, '=')}");
            onUpdate(false, $"  Cloudflare Best IP Probe");
            onUpdate(false, $"  Protocol: {cfConfig.SelectedProtocol?.ToUpperInvariant() ?? "VLESS"} | Mode: {cfConfig.ProbeMode}");
            onUpdate(false, $"  Repeat: {cfConfig.ProbeRepeat} | Timeout: {cfConfig.Timeout}s | TopN: {cfConfig.TopN}");
            onUpdate(false, $"{"=".PadRight(55, '=')}");

            var fetcher = new CfDataFetcher(cfConfig);

            // ═══════════════════════════════════════════════════════════════
            // Phase 1: 历史优选 IP / Historical priority IPs
            // ═══════════════════════════════════════════════════════════════
            onUpdate(false, "── Phase 1: Historical priority IPs ──");
            onUpdate(false, $"[1/5] Fetching historically-good IPs (max {Phase1MaxIps}, early stop at {Phase1SpeedPassTarget} speed-passed)...");

            var historicalIps = await fetcher.FetchHistoricalIpsAsync(msg => onUpdate(false, $"  {msg}"));
            var phase1Ips = historicalIps.Take(Phase1MaxIps).ToList();
            var probedIps = new HashSet<string>(phase1Ips.Select(s => s.Ip));
            var phase1Passed = 0;
            var allProbeResults = new List<CfProbeResult>();

            if (phase1Ips.Count > 0)
            {
                onUpdate(false, $"[2/5] Probing {phase1Ips.Count} historical IPs (concurrency: {(Utils.IsWindows() ? 20 : 10)})...");
                (phase1Passed, var p1Results) = await RunProbeAndExportAsync(cfConfig, phase1Ips, "Phase 1", onUpdate,
                    speedPassThreshold: Phase1SpeedPassTarget, batchSize: Phase1BatchSize);
                allProbeResults.AddRange(p1Results);
            }
            else
            {
                onUpdate(false, "Phase 1: no historical IPs, skipping to Phase 2.");
            }

            // ═══════════════════════════════════════════════════════════════
            // Phase 2: 常规 IP / General IPs
            // ═══════════════════════════════════════════════════════════════
            if (phase1Passed >= cfConfig.TopN)
            {
                onUpdate(false, $"Phase 1 satisfied TOP {cfConfig.TopN} ({phase1Passed} speed-passed), skipping Phase 2.");
            }
            else
            {
                onUpdate(false, "── Phase 2: General IPs (IpSetUrls + DomainsSetUrl) ──");
                onUpdate(false, $"[4/5] Fetching general IPs (skipping {probedIps.Count} already probed)...");

                var generalIpSources = await fetcher.FetchGeneralIpsAsync(probedIps, msg => onUpdate(false, $"  {msg}"));

                if (generalIpSources.Count > 0)
                {
                    var phase2Need = cfConfig.TopN - phase1Passed;
                    onUpdate(false, $"[5/5] Probing {generalIpSources.Count} general IPs (concurrency: {(Utils.IsWindows() ? 20 : 10)}, need {phase2Need} more speed-passed)...");
                    var (_, p2Results) = await RunProbeAndExportAsync(cfConfig, generalIpSources, "Phase 2", onUpdate,
                        speedPassThreshold: phase2Need, batchSize: Phase1BatchSize);
                    allProbeResults.AddRange(p2Results);
                }
                else
                {
                    onUpdate(false, "Phase 2: no new IPs to probe.");
                }
            }

            // ── POST 所有探测结果（对标 Python post_all_results）──
            await PostResultsAsync(allProbeResults, cfConfig.PostUrls, onUpdate);

            onUpdate(false, "Done! All phases complete.");
        }
        catch (Exception ex)
        {
            Logging.SaveLog("CfBestIpHandler", ex);
            onUpdate(true, $"Error: {ex.Message}");
        }
    }

    /// <summary>
    /// 探测 → 评分 → 导出 → 入组 子管线
    /// Probe → score → export → add-to-group sub-pipeline
    ///
    /// speedPassThreshold > 0 时按小批次探测并早停：
    ///   每批 batchSize 个 IP，批次内并发延迟 + 串行测速，批次间检查达标数
    ///
    /// When speedPassThreshold > 0, probe in small batches with early stop:
    ///   batchSize IPs per batch, concurrent latency + sequential speed within batch
    /// </summary>
    private static async Task<(int speedPassed, List<CfProbeResult> allResults)> RunProbeAndExportAsync(
        CfBestIpItem cfConfig, List<CfIpSource> ipSources, string phaseLabel, Action<bool, string> onUpdate,
        int speedPassThreshold = 0, int batchSize = 0)
    {
        var allResults = new List<CfProbeResult>();
        var lowestSpeed = cfConfig.LowestSpeed;
        var ips = ipSources.Select(s => s.Ip).ToList();

        if (speedPassThreshold > 0 && batchSize > 0)
        {
            var totalProbed = 0;

            for (var i = 0; i < ips.Count; i += batchSize)
            {
                var remaining = speedPassThreshold - allResults.Count(r => r.DownloadSpeedKBs >= lowestSpeed);
                if (remaining <= 0) break;

                var batch = ips.Skip(i).Take(batchSize).ToList();
                var batchProber = new CfIpBatchProber(cfConfig);
                var batchResults = await batchProber.ProbeAllAsync(batch, msg => onUpdate(false, $"  {msg}"), remaining);
                allResults.AddRange(batchResults);
                totalProbed += batch.Count;

                var batchSpeedPassed = allResults.Count(r => r.DownloadSpeedKBs >= lowestSpeed);
                onUpdate(false, $"  {phaseLabel} progress: {totalProbed}/{ips.Count} probed | {batchSpeedPassed}/{speedPassThreshold} speed-passed");

                if (batchSpeedPassed >= speedPassThreshold)
                {
                    onUpdate(false, $"  {phaseLabel} early stop: {batchSpeedPassed} IPs passed speed test (>= {lowestSpeed}KB/s)");
                    break;
                }
            }
        }
        else
        {
            var batchProber = new CfIpBatchProber(cfConfig);
            allResults = await batchProber.ProbeAllAsync(ips, msg => onUpdate(false, $"  {msg}"),
                speedPassStop: speedPassThreshold);
        }

        // ── 注入 source URL ──
        var sourceMap = ipSources.Where(s => s.SourceUrl != null).ToDictionary(s => s.Ip, s => s.SourceUrl!);
        foreach (var r in allResults)
        {
            if (sourceMap.TryGetValue(r.Ip, out var src))
                r.Source = src;
        }

        if (allResults.Count == 0)
        {
            onUpdate(false, $"{phaseLabel}: no successful probe results.");
            return (0, allResults);
        }

        // ── 评分排序 / Score and rank ──
        var avgLatency = allResults.Average(r => r.AvgLatencyMs);
        var avgSpeed = allResults.Average(r => r.DownloadSpeedKBs);
        onUpdate(false, $"  {phaseLabel} probing done. Success: {allResults.Count} | Avg latency: {avgLatency:F1}ms | Avg speed: {avgSpeed:F0}KB/s");

        onUpdate(false, $"{phaseLabel}: scoring and ranking...");
        var scorer = new CfIpScorer(cfConfig);
        var topResults = scorer.ScoreAndRank(allResults);

        PrintTopResults(topResults, phaseLabel, onUpdate);

        // ── 导出节点 + 添加到分组 / Export nodes + add to group ──
        try
        {
            var exporter = new CfResultExporter(cfConfig);
            var nodes = exporter.ExportAsProfileItems(topResults);
            if (nodes.Count > 0)
            {
                await AddNodesToGroupAsync(nodes, onUpdate);
            }
            else
            {
                onUpdate(false, $"  {phaseLabel}: no nodes generated from top results.");
            }
        }
        catch (Exception ex)
        {
            Logging.SaveLog("CfBestIpHandler.Export", ex);
            onUpdate(false, $"  {phaseLabel}: failed to add nodes — {ex.Message}");
        }

        return (allResults.Count(r => r.DownloadSpeedKBs >= lowestSpeed), allResults);
    }

    /// <summary>
    /// 打印 TOP N 结果表格 / Print TOP N results table
    /// </summary>
    private static void PrintTopResults(List<CfProbeResult> topResults, string phaseLabel, Action<bool, string> onUpdate)
    {
        onUpdate(false, $"{"=".PadRight(55, '=')}");
        onUpdate(false, $"  {phaseLabel} TOP {topResults.Count} Results:");
        onUpdate(false, $"  {"IP",-18} {"Colo",-8} {"Region",-16} {"Latency",-10} {"Speed",-10} {"Score",-8}");
        onUpdate(false, $"  {"-" .PadRight(18, '-')} {"-" .PadRight(8, '-')} {"-" .PadRight(16, '-')} {"-" .PadRight(10, '-')} {"-" .PadRight(10, '-')} {"-" .PadRight(8, '-')}");
        foreach (var r in topResults)
        {
            onUpdate(false,
                $"  {r.Ip,-18} {r.Colo ?? "?",-8} {r.Region ?? "?",-16} {r.AvgLatencyMs,8:F1}ms {r.DownloadSpeedKBs,8:F0}KB/s {r.Score,6:F1}");
        }
        onUpdate(false, $"{"=".PadRight(55, '=')}");
    }

    /// <summary>
    /// POST 所有探测结果到配置的上报地址（对标 Python post_all_results）
    /// 过滤无效节点 → 构建 JSON → 并发 POST 到所有 PostUrls（最多重试 3 次）
    /// </summary>
    private static async Task PostResultsAsync(List<CfProbeResult> allResults, List<string> postUrls, Action<bool, string> onUpdate)
    {
        if (postUrls == null || postUrls.Count == 0) return;
        if (allResults.Count == 0) return;

        // 过滤无效节点（对标 Python: 无 IP/无 colo/score > 9999 跳过）
        var nodes = new List<JsonObject>();
        var skipped = 0;
        foreach (var r in allResults.OrderBy(r => r.Score))
        {
            if (r.Ip.IsNullOrEmpty()) { skipped++; continue; }
            if (r.Colo.IsNullOrEmpty() || r.Colo is "UNKNOWN" or "NONE" or "NULL") { skipped++; continue; }
            if (r.Score > 9999) { skipped++; continue; }

            nodes.Add(new JsonObject
            {
                ["ip"] = r.Ip,
                ["colo"] = r.Colo.ToUpperInvariant(),
                ["score"] = r.Score,
                ["lat"] = r.AvgLatencyMs,
                ["loss"] = Math.Round(r.TlsLossRate, 2),
                ["source"] = r.Source ?? "",
                ["speed_kb_s"] = Math.Round(r.DownloadSpeedKBs, 1),
                ["tcp_ms"] = Math.Round(r.TcpMs, 1),
                ["tls_ms"] = Math.Round(r.TlsMs, 1),
                ["ttfb_ms"] = Math.Round(r.TtfbMs, 1),
                ["total_ms"] = Math.Round(r.TotalMs, 1),
            });
        }

        if (skipped > 0)
            onUpdate(false, $"POST: skipped {skipped} invalid nodes");

        if (nodes.Count == 0) return;

        var json = JsonSerializer.Serialize(nodes, new JsonSerializerOptions { WriteIndented = true });
        onUpdate(false, $"POST: uploading {nodes.Count} nodes to {postUrls.Count} URL(s)...");

        using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        var tasks = postUrls.Where(u => u.IsNotEmpty()).Select(async url =>
        {
            for (var attempt = 1; attempt <= 3; attempt++)
            {
                try
                {
                    var response = await httpClient.PostAsync(url, content);
                    if (response.IsSuccessStatusCode)
                    {
                        onUpdate(false, $"POST OK: {url}");
                        return;
                    }
                }
                catch { }
                if (attempt < 3)
                    await Task.Delay(1000);
            }
            onUpdate(false, $"POST FAIL: {url} (after 3 retries)");
        });

        await Task.WhenAll(tasks);
    }

    /// <summary>
    /// 将节点插入数据库并添加到 [CF优选] 分组 / Insert nodes into DB and add to [CF优选] group
    /// </summary>
    private static async Task AddNodesToGroupAsync(List<ProfileItem> nodes, Action<bool, string> onUpdate)
    {
        var groupName = "CF优选";

        var appInstance = AppManager.Instance;
        if (appInstance == null)
        {
            onUpdate(false, "  WARNING: AppManager not initialized, cannot add nodes.");
            return;
        }

        var config = appInstance.Config;
        if (config == null)
        {
            onUpdate(false, "  WARNING: Config not loaded, cannot add nodes.");
            return;
        }

        // 查找或创建分组 / Find or create group
        var existingItems = await appInstance.ProfileItems(string.Empty);
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

        // 逐条插入节点 / Insert nodes one by one
        var insertedIds = new List<string>();
        foreach (var node in nodes)
        {
            node.IndexId = string.Empty;
            await ConfigHandler.AddServerCommon(config, node);
            insertedIds.Add(node.IndexId);
        }

        // 重新加载 groupItem 获取最新 ChildItems / Reload to get latest ChildItems
        existingItems = await appInstance.ProfileItems(string.Empty);
        groupItem = existingItems?
            .FirstOrDefault(t => t.ConfigType is EConfigType.PolicyGroup && t.Remarks == groupName);

        if (groupItem != null)
        {
            var groupProto = groupItem.GetProtocolExtra();
            var childIds = Utils.String2List(groupProto.ChildItems) ?? [];
            childIds.AddRange(insertedIds);
            groupProto = groupProto with { ChildItems = Utils.List2String(childIds.Distinct().ToList()) };
            groupItem.SetProtocolExtra(groupProto);
            await SQLiteHelper.Instance.UpdateAsync(groupItem);
        }

        onUpdate(false, $"  Added {nodes.Count} nodes to [{groupName}] group.");
    }
}
