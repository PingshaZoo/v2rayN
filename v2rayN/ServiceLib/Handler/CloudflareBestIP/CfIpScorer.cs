namespace ServiceLib.Handler.CloudflareBestIP;

/// <summary>
/// IP 评分排序引擎：两轮评分制（延迟评分 + 速度重排名）
/// IP scoring & ranking engine: two-round scoring (latency scoring + speed re-ranking)
///
/// 评分层级（越低越好）/ Score hierarchy (lower is better):
///   1-10      — 速度达标，按下载速度降序排名（最快=1）
///   9999      — 速度不达标但测出了速度（>0 且 <LowestSpeed）
///   10000+    — 速度=0（速度测试完全失败），在此基础上叠加延迟分用于内部排序
///   999999    — TCP/TLS 失败率 > 20%，直接淘汰
///
///   1-10      — speed passed, ranked by download speed desc (fastest=1)
///   9999      — speed below threshold but working (>0 and <LowestSpeed)
///   10000+    — speed=0 (test failed entirely), latency score added for internal ordering
///   999999    — TCP/TLS failure rate > 20%, eliminated
/// </summary>
public class CfIpScorer
{
    private readonly CfBestIpItem _config;

    public CfIpScorer(CfBestIpItem config)
    {
        _config = config;
    }

    /// <summary>
    /// 对探测结果评分排序，返回 TOP N
    /// Score and rank probe results, return TOP N
    /// </summary>
    public List<CfProbeResult> ScoreAndRank(List<CfProbeResult> results)
    {
        // ═════════════════════════════════════════════════════════════════
        // Round 1: 基于延迟和丢包的线性评分
        // Round 1: linear scoring based on latency + packet loss
        // ═════════════════════════════════════════════════════════════════
        foreach (var r in results)
        {
            // 丢包率过高的 IP 直接淘汰（对标 Python 版 20% 阈值）
            // Eliminate IPs with excessive loss (matches Python 20% threshold)
            if (r.TcpLossRate > 0.2 || r.TlsLossRate > 0.2)
            {
                r.Score = 999999;
                continue;
            }

            // 延迟 × 延迟权重 + TLS丢包率 × 100 × 丢包惩罚(秒) × 丢包权重
            // Latency × latency weight + TLS loss rate × 100 × loss penalty (seconds) × loss weight
            r.Score = r.AvgLatencyMs * _config.WeightLatency
                      + r.TlsLossRate * 100 * _config.LossPenaltyMs / 1000 * _config.WeightLoss;
        }

        // ═════════════════════════════════════════════════════════════════
        // Round 2: 速度重排名
        // Round 2: speed re-ranking
        // ═════════════════════════════════════════════════════════════════

        // 速度达标 (>= LowestSpeed) → 按下载速度降序重排，最快 Score=1
        // Speed passed → re-rank by download speed desc, fastest=1
        var withSpeed = results
            .Where(r => r.DownloadSpeedKBs >= _config.LowestSpeed)
            .OrderByDescending(r => r.DownloadSpeedKBs)
            .ToList();

        for (var i = 0; i < withSpeed.Count; i++)
        {
            withSpeed[i].Score = i + 1;
        }

        // 速度不达标但测出了速度 (>0 且 <LowestSpeed) → 9999 垫底
        // Below threshold but working → 9999 bottom tier
        foreach (var r in results.Where(r => r.DownloadSpeedKBs > 0 && r.DownloadSpeedKBs < _config.LowestSpeed))
        {
            r.Score = 9999;
        }

        // 速度=0（测试完全失败）→ 保留延迟分 + 10000 基础分
        //   排在 9999（慢但可用）之后，但相互之间仍按延迟排序
        // Speed=0 (test failed) → keep latency score + 10000 base
        //   ranks below slow-but-working (9999), but sorts by latency among themselves
        foreach (var r in results.Where(r => r.DownloadSpeedKBs == 0 && r.Score < 9999))
        {
            r.Score = r.Score + 10000;
        }

        // 按 Score 升序取 TOP N（Score 越低越好）
        // Take TOP N by Score ascending (lower is better)
        return results.OrderBy(r => r.Score).Take(_config.TopN).ToList();
    }
}
