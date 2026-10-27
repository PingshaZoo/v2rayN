namespace ServiceLib.Handler.CloudflareBestIP;

public class CfProbeResult
{
    public string Ip { get; set; } = string.Empty;
    public int Port { get; set; } = 443;
    public string Sni { get; set; } = string.Empty;
    public string? Colo { get; set; }
    public string? Region { get; set; }
    public double AvgLatencyMs { get; set; }
    public double TcpLossRate { get; set; }
    public double TlsLossRate { get; set; }
    public double HttpLossRate { get; set; }
    public double DownloadSpeedKBs { get; set; }
    public bool SpeedTested { get; set; }
    public bool Qualified { get; set; }
    public double Score { get; set; } = 999999;
    public string? Source { get; set; }
    public string? Error { get; set; }
    public double TcpMs { get; set; }
    public double TlsMs { get; set; }
    public double TtfbMs { get; set; }
    public double TotalMs { get; set; }
    public string Status => Qualified ? CfUi.Qualified : SpeedTested ? CfUi.NotQualified : CfUi.NotTested;
}

public record CfProbeProgress(string Stage, int Completed, int Total, int Qualified, string Message)
{
    public double Percent => Total > 0 ? Math.Clamp(100.0 * Completed / Total, 0, 100) : 0;
}

public record CfProbeRunResult(List<CfProbeResult> Results, List<CfProbeResult> TopResults);
