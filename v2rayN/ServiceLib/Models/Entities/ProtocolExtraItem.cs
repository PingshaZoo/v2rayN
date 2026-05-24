namespace ServiceLib.Models.Entities;

public record ProtocolExtraItem
{
    public bool? Uot { get; init; }
    public string? CongestionControl { get; init; }

    // vmess
    public string? AlterId { get; init; }
    public string? VmessSecurity { get; init; }

    // vless
    public string? Flow { get; init; }
    public string? VlessEncryption { get; init; }
    //public string? VisionSeed { get; init; }

    // shadowsocks
    //public string? PluginArgs { get; init; }
    public string? SsMethod { get; init; }

    // wireguard
    public string? WgPublicKey { get; init; }
    public string? WgPresharedKey { get; init; }
    public string? WgInterfaceAddress { get; init; }
    public string? WgReserved { get; init; }
    public int? WgMtu { get; init; }

    // hysteria2
    public string? SalamanderPass { get; init; }
    public int? UpMbps { get; init; }
    public int? DownMbps { get; init; }
    public string? Ports { get; init; }
    public string? HopInterval { get; init; }

    // naiveproxy
    public int? InsecureConcurrency { get; init; }
    public bool? NaiveQuic { get; init; }

    // group profile
    public string? GroupType { get; init; }
    public string? ChildItems { get; init; }
    public string? SubChildItems { get; init; }
    public string? Filter { get; init; }
    public EMultipleLoad? MultipleLoad { get; init; }
    public bool? AdaptiveEnabled { get; init; }

    // Adaptive per-group probe settings (§15.1 Per-Group layer)
    public string? AdaptiveProbeUrl { get; init; }
    public int? AdaptiveProbeIntervalSec { get; init; }
    public int? AdaptiveProbeTimeoutMs { get; init; }
    public double? AdaptiveProbeHeavyFraction { get; init; }

    // Adaptive per-group Production Pool sizing (§5.7)
    public double? AdaptiveActiveFraction { get; init; }
    public int? AdaptiveMinProductionNodes { get; init; }
    public int? AdaptiveMaxProductionNodes { get; init; }
}
