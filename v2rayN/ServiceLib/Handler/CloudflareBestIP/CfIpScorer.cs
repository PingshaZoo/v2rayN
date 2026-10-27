namespace ServiceLib.Handler.CloudflareBestIP;

public class CfIpScorer(CfBestIpItem config)
{
    public static bool HasReliableLatency(CfProbeResult r) => IPAddress.TryParse(r.Ip, out _)
        && !string.IsNullOrWhiteSpace(r.Sni) && !string.IsNullOrWhiteSpace(r.Colo)
        && r.Colo is not ("UNKNOWN" or "NONE" or "NULL")
        && double.IsFinite(r.AvgLatencyMs) && r.AvgLatencyMs > 0
        && IsLoss(r.TcpLossRate) && IsLoss(r.TlsLossRate)
        && double.IsFinite(r.HttpLossRate) && r.HttpLossRate is >= 0 and <= 1;

    private static bool IsLoss(double loss) => double.IsFinite(loss) && loss is >= 0 and <= 0.2;

    public static bool IsQualified(CfProbeResult r, CfBestIpItem config) => HasReliableLatency(r)
        && r.SpeedTested && double.IsFinite(r.DownloadSpeedKBs)
        && r.DownloadSpeedKBs > 0 && r.DownloadSpeedKBs >= config.LowestSpeed;

    public double LatencyScore(CfProbeResult r) => HasReliableLatency(r)
        ? r.AvgLatencyMs * config.WeightLatency + r.HttpLossRate * config.LossPenaltyMs * config.WeightLoss
        : 999999;

    public List<CfProbeResult> ScoreAndRank(List<CfProbeResult> results)
    {
        foreach (var r in results)
        {
            r.Qualified = IsQualified(r, config);
            r.Score = r.Qualified ? 0 : 9999;
        }
        var qualified = results.Where(r => r.Qualified)
            .OrderByDescending(r => r.DownloadSpeedKBs).ThenBy(r => r.AvgLatencyMs).ThenBy(r => r.Ip, StringComparer.Ordinal)
            .DistinctBy(r => r.Ip).ToList();
        for (var i = 0; i < qualified.Count; i++) qualified[i].Score = i + 1;
        return qualified.Take(config.TopN).ToList();
    }
}
