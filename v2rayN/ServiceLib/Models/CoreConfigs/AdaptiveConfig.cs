namespace ServiceLib.Models.CoreConfigs;

/// <summary>
/// Adaptive scheduling configuration passed to the core config generator.
/// Describes the probe inbounds and weighted balancer selector.
/// </summary>
public record AdaptiveConfig
{
    /// <summary>Node tags currently in the balancer selector (active, not in cooldown).</summary>
    public required List<string> ActiveTags { get; init; }

    /// <summary>Node tags currently in cooldown (excluded from balancer).</summary>
    public required List<string> CooldownTags { get; init; }

    /// <summary>Maps node tag → local probe SOCKS5 port (127.0.0.1 only).</summary>
    public required IReadOnlyDictionary<string, int> ProbePorts { get; init; }

    /// <summary>Maps node tag → current QoS score [1, 100]. Used by config generator
    /// to build a weighted selector via tag duplication (high-score nodes appear
    /// more times in the balancer's random selector).</summary>
    public IReadOnlyDictionary<string, double> NodeScores { get; init; } = new Dictionary<string, double>();

    /// <summary>Maps node outbound tag → child ProfileItem IndexId.
    /// Used by StatisticsManager to attribute per-outbound traffic to child rows.</summary>
    public IReadOnlyDictionary<string, string> TagToIndexId { get; init; } = new Dictionary<string, string>();
}
