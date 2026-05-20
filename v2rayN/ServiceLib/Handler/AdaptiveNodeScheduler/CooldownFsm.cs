namespace ServiceLib.Handler.AdaptiveNodeScheduler;

public sealed class CooldownFsm
{
    private const double MaxEjectionFraction = 1.0 / 3.0;
    private const double BaseSeconds = 30.0;
    private const double JitterFactor = 0.20;
    private const double MaxSeconds = 300.0;

    public void TryEnterCooldown(NodeState node,
                                 IReadOnlyList<NodeState> allNodes)
    {
        if (node.ConsecutiveFailures < 2)
            return;

        int cooldownCount = allNodes.Count(n => n.IsInCooldown);
        int maxAllowed = Math.Max(1, (int)(allNodes.Count * MaxEjectionFraction));

        if (cooldownCount >= maxAllowed)
            return;

        int n = Math.Max(0, node.ConsecutiveFailures - 2);
        double baseSec = BaseSeconds * Math.Pow(2, n);
        double jitter = baseSec * JitterFactor * (Random.Shared.NextDouble() - 0.5);
        double totalSec = Math.Min(baseSec + jitter, MaxSeconds);

        node.SetCooldown(DateTime.UtcNow.AddSeconds(totalSec));
    }
}
