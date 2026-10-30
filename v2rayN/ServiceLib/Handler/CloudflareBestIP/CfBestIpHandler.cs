namespace ServiceLib.Handler.CloudflareBestIP;

public class CfBestIpHandler
{
    private static readonly Lazy<CfBestIpHandler> Singleton = new(() => new());
    public static CfBestIpHandler Instance => Singleton.Value;
    private const int SpeedPassTarget = 20;

    public async Task<CfProbeRunResult> RunAsync(Config config, IProgress<CfProbeProgress>? progress = null,
        CancellationToken cancellationToken = default, IProgress<string>? logProgress = null)
    {
        var log = new CfProbeLog(message => logProgress?.Report(message));
        log.Write(CfUi.T("开始测速", "Probe started"));
        try
        {
            var result = await RunCoreAsync(config, progress, cancellationToken, log);
            log.Write(CfUi.T($"测速完成：{result.Results.Count} 个有效响应，{result.TopResults.Count} 个入选结果",
                $"Probe complete: {result.Results.Count} valid responses, {result.TopResults.Count} top results"));
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            log.Write(CfUi.T("测速已停止", "Probe stopped"));
            throw;
        }
        catch (Exception ex)
        {
            log.Write(CfUi.T($"测速失败：{ex}", $"Probe failed: {ex}"));
            throw;
        }
    }

    private async Task<CfProbeRunResult> RunCoreAsync(Config config, IProgress<CfProbeProgress>? progress,
        CancellationToken cancellationToken, CfProbeLog log)
    {
        var settings = config.CfBestIpItem ?? throw new ArgumentException(CfUi.T("Cloudflare 设置不存在", "Cloudflare settings are missing"));
        var error = CfProbeValidation.Validate(settings);
        if (error is not null) throw new ArgumentException(error);
        cancellationToken.ThrowIfCancellationRequested();
        using var fetcher = new CfDataFetcher(settings, onLog: log.Write);
        var batch = new CfIpBatchProber(settings, onLog: log.Write);
        var scorer = new CfIpScorer(settings);
        var all = new List<CfProbeResult>();
        var target = Math.Max(SpeedPassTarget, settings.TopN);
        log.Write($"mode={settings.ProbeMode}, SNI={string.Join(", ", settings.OriginSniList)}, repeat={settings.ProbeRepeat}, timeout={settings.Timeout}s, minimum={settings.LowestSpeed} KB/s, target={target}");

        progress?.Report(new("source", 0, 0, 0, CfUi.T("正在获取数据源…", "Fetching sources…")));
        var historicalTask = fetcher.FetchHistoricalIpsAsync(
            message => { log.Write(message); progress?.Report(new("source", 0, 0, 0, message)); }, cancellationToken);
        var generalTask = fetcher.FetchGeneralIpsAsync([],
            message => { log.Write(message); progress?.Report(new("source", 0, 0, 0, message)); }, cancellationToken);
        await Task.WhenAll(historicalTask, generalTask);
        cancellationToken.ThrowIfCancellationRequested();

        var historical = historicalTask.Result;
        var prioritized = historical.Where(s => s.HistoricalSpeedKBs >= settings.LowestSpeed * 0.65)
            .Take(100).ToList();
        var seen = new HashSet<string>(prioritized.Select(s => s.Ip));
        var remaining = historical.Where(s => seen.Add(s.Ip)).ToList();
        var general = remaining.Concat(generalTask.Result.Where(s => !seen.Contains(s.Ip)))
            .DistinctBy(s => s.Ip).ToList();
        log.Write(CfUi.T($"候选 IP：历史优先 {prioritized.Count}，常规 {general.Count}",
            $"Candidates: historical priority {prioritized.Count}, general {general.Count}"));
        if (prioritized.Count + general.Count == 0)
            log.Write(CfUi.T("无候选 IP：请检查数据源请求、解析和 CIDR 过滤日志", "No candidates: check source requests, parsing and CIDR filtering logs"));

        if (prioritized.Count > 0)
        {
            log.Write(CfUi.T("开始历史 IP 延迟探测及串行测速", "Starting historical latency and serial download probes"));
            progress?.Report(new("latency", 0, prioritized.Count, 0, CfUi.T("历史 IP 延迟探测", "Historical IP latency")));
            var results = await batch.ProbeLatencyAsync(prioritized,
                (done, total) => progress?.Report(new("latency", done, total, 0, CfUi.T("历史 IP 延迟探测", "Historical IP latency"))), cancellationToken);
            all.AddRange(results);
            var candidates = results.OrderBy(scorer.LatencyScore).ToList();
            await batch.ProbeSpeedAsync(candidates, target,
                (done, total) => progress?.Report(new("speed", done, total,
                    all.Count(r => CfIpScorer.IsQualified(r, settings)), CfUi.T("历史 IP 串行测速", "Historical IP download speed"))), cancellationToken);
        }

        if (all.Count(r => CfIpScorer.IsQualified(r, settings)) < target)
        {
            for (var start = 0; start < general.Count; start += 100)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var sources = general.Skip(start).Take(100).ToList();
                log.Write(CfUi.T($"开始常规 IP 批次：{start + 1}–{start + sources.Count}/{general.Count}",
                    $"Starting general batch: {start + 1}–{start + sources.Count}/{general.Count}"));
                var results = await batch.ProbeLatencyAsync(sources,
                    (done, total) => progress?.Report(new("latency", start + done, general.Count,
                        all.Count(r => CfIpScorer.IsQualified(r, settings)), CfUi.T("常规 IP 延迟探测", "General IP latency"))), cancellationToken);
                all.AddRange(results);
                var eligible = SelectSpeedCandidates(results, scorer);
                var stillNeeded = target - all.Count(r => CfIpScorer.IsQualified(r, settings));
                await batch.ProbeSpeedAsync(eligible, stillNeeded,
                    (done, total) => progress?.Report(new("speed", done, total,
                        all.Count(r => CfIpScorer.IsQualified(r, settings)), CfUi.T("常规 IP 串行测速", "General IP download speed"))), cancellationToken);
                if (all.Count(r => CfIpScorer.IsQualified(r, settings)) >= target) break;
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        var top = scorer.ScoreAndRank(all);
        progress?.Report(new("done", all.Count, all.Count, top.Count,
            CfUi.T($"完成：{top.Count} 个达标 IP", $"Done: {top.Count} qualified IPs")));
        return new CfProbeRunResult(all, top);
    }

    // Kept for existing callers: running a scan has no side effects on profiles or POST endpoints.
    public async Task RunAsync(Config config, Action<bool, string> onUpdate)
    {
        try
        {
            var report = new Progress<CfProbeProgress>(p => onUpdate(false, p.Message));
            await RunAsync(config, report);
        }
        catch (Exception ex)
        {
            Logging.SaveLog("CfBestIpHandler", ex);
            onUpdate(true, ex.Message);
        }
    }

    public static List<CfProbeResult> SelectSpeedCandidates(IEnumerable<CfProbeResult> results, CfIpScorer scorer)
    {
        var sorted = results.Where(CfIpScorer.HasReliableLatency).OrderBy(scorer.LatencyScore).ToList();
        var selected = new List<CfProbeResult>();
        foreach (var region in new[] { "NorthAmerica", "HONGKONG", "EastAsia" })
            selected.AddRange(sorted.Where(r => r.Region == region).Take(5));
        selected.AddRange(sorted.Where(r => !selected.Contains(r)).Take(Math.Max(0, 15 - selected.Count)));
        return selected.DistinctBy(r => r.Ip).ToList();
    }

    public async Task PostResultsAsync(CfProbeRunResult run, CfBestIpItem settings,
        CancellationToken cancellationToken = default)
    {
        var urls = settings.PostUrls.Where(CfProbeValidation.IsHttpUrl).ToList();
        if (urls.Count == 0) return;
        var rows = run.Results.Where(r => CfIpScorer.HasReliableLatency(r)).Select(r => new
        {
            ip = r.Ip, colo = r.Colo, score = r.Score, lat = r.AvgLatencyMs,
            loss = r.HttpLossRate, source = r.Source ?? "", speed_kb_s = r.DownloadSpeedKBs,
            tcp_ms = r.TcpMs, tls_ms = r.TlsMs, ttfb_ms = r.TtfbMs, total_ms = r.TotalMs
        }).ToList();
        if (rows.Count == 0) return;
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var json = JsonSerializer.Serialize(rows);
        foreach (var url in urls)
        {
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var response = await client.PostAsync(url, content, cancellationToken);
            response.EnsureSuccessStatusCode();
        }
    }
}
