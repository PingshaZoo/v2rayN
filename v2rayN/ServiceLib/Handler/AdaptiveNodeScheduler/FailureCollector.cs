namespace ServiceLib.Handler.AdaptiveNodeScheduler;

public enum FailureType { None, Timeout, Refused, TlsError, NetworkError, UnexpectedEof }

public sealed class FailureCollector
{
    private readonly ScoreCalculator _scorer;
    private readonly CooldownFsm _cooldown;

    public FailureCollector(ScoreCalculator scorer, CooldownFsm cooldown)
    {
        _scorer = scorer;
        _cooldown = cooldown;
    }

    public void RecordSuccess(NodeState node, double ttfbMs)
    {
        double alpha = DecayedAlpha(node.LastObserved);

        double newLatency = Ewma(node.EwmaLatencyMs, ttfbMs, alpha);
        double newLoss = Ewma(node.EwmaLossRate, 0.0, alpha);
        double newScore = _scorer.Compute(newLatency, newLoss);

        node.UpdateScore(newLatency, newLoss, newScore,
                         consecutiveFailures: 0);
    }

    public void RecordFailure(NodeState node, FailureType type,
                              IReadOnlyList<NodeState> allNodes)
    {
        // TlsError is a configuration error, not a network quality signal.
        // Do not penalize EWMA and do not enter cooldown.
        if (type == FailureType.TlsError)
        {
            // Log: $"TlsError on {node.Tag} — check TLS configuration"
            return;
        }

        (double penaltyLoss, double penaltyLatencyMs) = GetPenalty(type, node);

        double alpha = DecayedAlpha(node.LastObserved);

        double newLatency = Ewma(node.EwmaLatencyMs, penaltyLatencyMs, alpha);
        double newLoss = Ewma(node.EwmaLossRate, penaltyLoss, alpha);
        double newScore = _scorer.Compute(newLatency, newLoss);
        int newFails = node.ConsecutiveFailures + 1;

        node.UpdateScore(newLatency, newLoss, newScore, newFails);
        _cooldown.TryEnterCooldown(node, allNodes);
    }

    /// <summary>
    /// Returns (penaltyLoss, penaltyLatencyMs) per failure type.
    /// TlsError returns (0, unchanged) — configuration errors are not network quality.
    /// </summary>
    public static (double penaltyLoss, double penaltyLatencyMs) GetPenalty(
        FailureType type, NodeState node) =>
        type switch
        {
            FailureType.Refused => (1.0, 10_000),
            FailureType.Timeout => (0.8, 10_000),
            FailureType.NetworkError => (0.7, 10_000),
            FailureType.UnexpectedEof => (0.4, node.EwmaLatencyMs * 1.5),
            FailureType.TlsError => (0.0, node.EwmaLatencyMs),
            _ => (0.5, 10_000),
        };

    private static double DecayedAlpha(DateTime lastObserved)
    {
        double ageSec = Math.Max(0,
            (DateTime.UtcNow - lastObserved).TotalSeconds);
        return 0.05 + 0.25 * Math.Exp(-ageSec / 60.0);
    }

    private static double Ewma(double old, double current, double alpha) =>
        old * (1 - alpha) + current * alpha;
}
