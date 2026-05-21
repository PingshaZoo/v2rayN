namespace ServiceLib.Models.Entities;

[Serializable]
public class ProfileExItem
{
    [PrimaryKey]
    public string IndexId { get; set; }

    public int Delay { get; set; }
    public decimal Speed { get; set; }
    public int Sort { get; set; }
    public string? Message { get; set; }

    /// <summary>Adaptive QoS score [0,100], round-trip from NodeState.Score.</summary>
    public int AdaptiveScore { get; set; }
    /// <summary>EWMA latency (ms) from adaptive probing.</summary>
    public int AdaptiveLatency { get; set; }
    /// <summary>Non-zero when node is in cooldown (excluded from balancer).</summary>
    public int AdaptiveCooldown { get; set; }
    /// <summary>Non-zero when node is in the active set.</summary>
    public int AdaptiveActive { get; set; }
}
