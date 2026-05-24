namespace ServiceLib.Models.Dto;

[Serializable]
public class ProfileItemModel : ReactiveObject
{
    public bool IsActive { get; set; }
    public string IndexId { get; set; }
    public EConfigType ConfigType { get; set; }
    public string Remarks { get; set; }
    public string Address { get; set; }
    public int Port { get; set; }
    public string Network { get; set; }
    public string StreamSecurity { get; set; }
    public string Subid { get; set; }
    public string SubRemarks { get; set; }
    public int Sort { get; set; }

    [Reactive]
    public int Delay { get; set; }

    public decimal Speed { get; set; }

    [Reactive]
    public string DelayVal { get; set; }

    [Reactive]
    public string SpeedVal { get; set; }

    [Reactive]
    public string TodayUp { get; set; }

    [Reactive]
    public string TodayDown { get; set; }

    [Reactive]
    public string TotalUp { get; set; }

    [Reactive]
    public string TotalDown { get; set; }

    // ── Adaptive QoS display ───────────────────────────────────

    [Reactive]
    public string AdaptiveScoreVal { get; set; }

    [Reactive]
    public string AdaptiveLatencyVal { get; set; }

    /// <summary>Combined status: Cooldown > HealthState > TrafficTier.</summary>
    [Reactive]
    public string AdaptiveStatusVal { get; set; }

    // Sort backing values — non-string versions for sorting
    public int AdaptiveScore { get; set; }
    public int AdaptiveLatency { get; set; }
    public bool AdaptiveCooldown { get; set; }
    public bool AdaptiveActive { get; set; }

    public string GetSummary()
    {
        var summary = $"[{ConfigType}] {Remarks}";
        if (!ConfigType.IsComplexType())
        {
            summary += $"({Address}:{Port})";
        }

        return summary;
    }
}
