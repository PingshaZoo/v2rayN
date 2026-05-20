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
        double alpha = DecayedAlpha(node.LastObserved);

        double newLatency = Ewma(node.EwmaLatencyMs, 10_000, alpha);
        double newLoss = Ewma(node.EwmaLossRate, 1.0, alpha);
        double newScore = _scorer.Compute(newLatency, newLoss);
        int newFails = node.ConsecutiveFailures + 1;

        node.UpdateScore(newLatency, newLoss, newScore, newFails);
        _cooldown.TryEnterCooldown(node, allNodes);
    }

    private static double DecayedAlpha(DateTime lastObserved)
    {
        double ageSec = Math.Max(0,
            (DateTime.UtcNow - lastObserved).TotalSeconds);
        return 0.05 + 0.25 * Math.Exp(-ageSec / 60.0);
    }

    private static double Ewma(double old, double current, double alpha) =>
        old * (1 - alpha) + current * alpha;
}
