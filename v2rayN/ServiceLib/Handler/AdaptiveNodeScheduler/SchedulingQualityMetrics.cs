namespace ServiceLib.Handler.AdaptiveNodeScheduler;

/// <summary>
/// P3.2: Observability-only scheduling quality metrics. Computed every 5 minutes
/// by <see cref="QualityMetricsReporter"/> and written to adaptive.log.
///
/// These are NOT acceptance criteria — they quantify "is adaptive helping?"
/// for post-deployment analysis. No scheduling decision depends on them.
/// </summary>
public static class SchedulingQualityMetrics
{
    /// <summary>
    /// A single sample of quality metrics at a point in time.
    /// </summary>
    public readonly record struct QualitySnapshot(
        double Entropy,
        double P95LatencyMs,
        double MeanScore,
        double ScoreStdDev,
        int ActiveNodeCount,
        int CooldownNodeCount,
        DateTime Timestamp)
    {
        /// <summary>
        /// Shannon entropy normalized to [0, 1]. 1 = all nodes have equal score
        /// (perfectly uniform active set). 0 = one node dominates (others near 0).
        /// Low entropy may indicate the adaptive scheduler is correctly converging
        /// on a few good nodes — or that only one node is healthy.
        /// </summary>
        public double NormalizedEntropy => MaxPossibleEntropy > 0
            ? Entropy / MaxPossibleEntropy
            : 0;

        /// <summary>
        /// Maximum possible entropy for the current node count (log₂(N)).
        /// </summary>
        public double MaxPossibleEntropy => ActiveNodeCount > 1
            ? Math.Log2(ActiveNodeCount)
            : 0;
    }

    /// <summary>
    /// Computes quality metrics from the current node states.
    /// </summary>
    public static QualitySnapshot Compute(IReadOnlyList<NodeState> nodes)
    {
        if (nodes.Count == 0)
            return new QualitySnapshot(0, 0, 0, 0, 0, 0, DateTime.UtcNow);

        int activeCount = nodes.Count(n => !n.IsInCooldown);
        int cooldownCount = nodes.Count - activeCount;

        var scores = nodes.Select(n => n.Score).ToList();
        double meanScore = scores.Average();
        double scoreStdDev = scores.Count > 1
            ? Math.Sqrt(scores.Average(s => (s - meanScore) * (s - meanScore)))
            : 0;

        // Shannon entropy of the score distribution.
        // Treat each node's score as proportional to its "probability mass."
        double totalScore = scores.Sum();
        double entropy = 0;
        if (totalScore > 0)
        {
            foreach (var s in scores)
            {
                double p = s / totalScore;
                if (p > 0)
                    entropy -= p * Math.Log2(p);
            }
        }

        // P95 latency: 95th percentile of EWMA latency across all nodes.
        var latencies = nodes
            .Select(n => n.EwmaLatencyMs)
            .OrderBy(l => l)
            .ToList();
        int p95Index = Math.Max(0, (int)Math.Ceiling(latencies.Count * 0.95) - 1);
        double p95Latency = latencies[p95Index];

        return new QualitySnapshot(
            entropy, p95Latency, meanScore, scoreStdDev,
            activeCount, cooldownCount, DateTime.UtcNow);
    }
}
