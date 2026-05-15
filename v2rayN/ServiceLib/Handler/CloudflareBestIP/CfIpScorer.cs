namespace ServiceLib.Handler.CloudflareBestIP;

public class CfIpScorer
{
    private readonly CfBestIpItem _config;

    public CfIpScorer(CfBestIpItem config)
    {
        _config = config;
    }

    public List<CfProbeResult> ScoreAndRank(List<CfProbeResult> results)
    {
        // Phase 1: Latency + loss scoring
        foreach (var r in results)
        {
            if (r.TcpLossRate > 0.2 || r.TlsLossRate > 0.2)
            {
                r.Score = 999999;
                continue;
            }

            r.Score = r.AvgLatencyMs * _config.WeightLatency
                      + r.TlsLossRate * 100 * _config.LossPenaltyMs / 1000 * _config.WeightLoss;
        }

        // Phase 2: Speed-based re-ranking for those with speed data
        var withSpeed = results
            .Where(r => r.DownloadSpeedKBs >= _config.LowestSpeed)
            .OrderByDescending(r => r.DownloadSpeedKBs)
            .ToList();

        // Assign rank scores: fastest = 1, second = 2, etc.
        for (var i = 0; i < withSpeed.Count; i++)
        {
            withSpeed[i].Score = i + 1;
        }

        // Those below speed threshold get 9999
        foreach (var r in results.Where(r => r.DownloadSpeedKBs > 0 && r.DownloadSpeedKBs < _config.LowestSpeed))
        {
            r.Score = 9999;
        }

        // Sort by score ascending (lower is better)
        return results.OrderBy(r => r.Score).Take(_config.TopN).ToList();
    }
}
