namespace ServiceLib.Handler.AdaptiveNodeScheduler;

/// <summary>
/// P2.2: Cooldown state machine with node-count boundary handling.
/// - 1 node: cooldown is disabled (ejecting the only node leaves nothing to route).
/// - 2 nodes: at most 1 can be in cooldown.
/// - 3+ nodes: at most 1/3 of all nodes can be in cooldown simultaneously.
///
/// <h2>Hash-based jitter (P1 stability fix)</h2>
/// Jitter uses a deterministic FNV-1a hash of the node tag, NOT
/// <see cref="Random"/>. This guarantees every node has a unique,
/// stable recovery offset that never repeats across restarts,
/// preventing synchronized recovery bursts after regional GFW events.
/// </summary>
public sealed class CooldownFsm
{
    private const double MaxEjectionFraction = 1.0 / 3.0;
    private const double BaseSeconds = 30.0;
    private const double MaxSeconds = 300.0;
    private const int JitterRangeSeconds = 15; // hash offset [0, 15)s

    public void TryEnterCooldown(NodeState node,
                                 IReadOnlyList<NodeState> allNodes)
    {
        if (node.ConsecutiveFailures < 2)
            return;

        int cooldownCount = allNodes.Count(n => n.IsInCooldown);
        int maxAllowed = ComputeMaxCooldown(allNodes.Count);

        if (cooldownCount >= maxAllowed)
            return;

        int n = Math.Max(0, node.ConsecutiveFailures - 2);
        double baseSec = BaseSeconds * Math.Pow(2, n);

        // Hash-based stable offset: each node gets a deterministic 0–14s
        // offset derived from its tag. Prevents synchronized recovery
        // bursts without adding non-determinism (Random jitter changes
        // every restart, making telemetry hard to analyze).
        int hashOffset = ComputeStableOffset(node.Tag);
        double totalSec = Math.Min(baseSec + hashOffset, MaxSeconds);

        node.SetCooldown(DateTime.UtcNow.AddSeconds(totalSec));
        if (node.HealthState == NodeHealthState.Active)
        {
            node.SetHealthState(NodeHealthState.Failed);
            node.ResetRecoveryProbeSuccess();
        }
    }

    /// <summary>
    /// FNV-1a 32-bit hash reduced to [0, JitterRangeSeconds).
    /// FNV-1a is chosen over <c>string.GetHashCode()</c> because .NET
    /// does not guarantee cross-process hash stability (randomized hash
    /// in newer runtimes). FNV-1a is deterministic and portable.
    /// </summary>
    private static int ComputeStableOffset(string tag)
    {
        uint hash = 2166136261;
        unchecked
        {
            foreach (char c in tag)
            {
                hash ^= c;
                hash *= 16777619;
            }
        }
        return (int)(hash % JitterRangeSeconds);
    }

    /// <summary>
    /// P2.2: Computes the maximum number of nodes that can be in cooldown.
    /// - 1 node → 0 (never eject the only node)
    /// - 2 nodes → 1 (at most 1 cooldown)
    /// - 3+ nodes → max(1, floor(N/3))
    /// </summary>
    public static int ComputeMaxCooldown(int nodeCount)
    {
        if (nodeCount <= 1)
            return 0;
        if (nodeCount == 2)
            return 1;
        return Math.Max(1, (int)(nodeCount * MaxEjectionFraction));
    }
}
