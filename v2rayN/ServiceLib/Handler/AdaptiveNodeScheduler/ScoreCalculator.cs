namespace ServiceLib.Handler.AdaptiveNodeScheduler;

/// <summary>
/// Computes HealthScore from two signals: EWMA latency and EWMA loss rate.
///
/// <b>CRITICAL (§6.6): Throughput is NOT a valid node-quality signal.</b>
/// Throughput suffers from causality inversion (user behavior → throughput,
/// not node quality → throughput). It is PROHIBITED from entering this formula.
/// See <see cref="XrayStatsPoller"/> for the only permitted throughput usage
/// (composite anomaly detection + telemetry).
/// </summary>
public sealed class ScoreCalculator
{
    private const double LatencyRef = 2000.0;
    private const double LatencyWeight = 0.55;
    private const double LossWeight = 0.45;
    private const double ScoreFloor = 1.0;
    private const double Exponent = 2.0;

    public double Compute(double ewmaLatencyMs, double ewmaLossRate)
    {
        double latNorm = Math.Min(ewmaLatencyMs / LatencyRef, 1.0);
        double lossNorm = Math.Clamp(ewmaLossRate, 0.0, 1.0);

        double raw = 1.0 - (latNorm * LatencyWeight + lossNorm * LossWeight);
        raw = Math.Max(raw, 0.0);

        double score = Math.Pow(raw, Exponent) * 100.0;
        return Math.Max(score, ScoreFloor);
    }
}
