namespace ServiceLib.Handler.CloudflareBestIP;

public static class CfProbeValidation
{
    public static List<string> Lines(string? text) => (text ?? string.Empty)
        .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(s => s.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    public static string? Validate(CfBestIpItem config)
    {
        if (config.OriginSniList is not { Count: > 0 }
            || config.OriginSniList.Any(s => Uri.CheckHostName(s) != UriHostNameType.Dns))
            return CfUi.T("请填写有效的 SNI 域名（每行一个）。", "Enter valid SNI hostnames, one per line.");
        if (config.ProbeMode is not ("full" or "edge")) return CfUi.T("探测模式必须为 full 或 edge。", "Probe mode must be full or edge.");
        if ((config.ProbeMode == "full" && !IsPath(config.OriginTestPath)) || !IsPath(config.OriginSpeedTestPath))
            return CfUi.T("请填写以 / 开头的测速路径；full 模式还需填写延迟测试路径。", "Enter paths beginning with /; full mode also needs a latency path.");
        if (config.TopN is < 1 or > 100 || config.ProbeRepeat is < 1 or > 20
            || config.Timeout is < 1 or > 120 || config.LowestSpeed <= 0
            || config.SpeedTestDuration is < 1 or > 60 || config.SleepInterval is < 0 or > 60
            || config.ProgInterval <= 0 || !double.IsFinite(config.WeightLatency) || config.WeightLatency < 0
            || !double.IsFinite(config.WeightLoss) || config.WeightLoss < 0
            || !double.IsFinite(config.LossPenaltyMs) || config.LossPenaltyMs < 0)
            return CfUi.T("测速次数、超时、阈值或权重超出有效范围。", "Repeats, timeouts, thresholds or weights are outside the allowed range.");
        var sources = (config.HavePostRes ?? []).Concat(config.IpSetUrls ?? []).ToList();
        if (!string.IsNullOrWhiteSpace(config.DomainsSetUrl)) sources.Add(config.DomainsSetUrl);
        if (sources.Count == 0) return CfUi.T("请至少填写一个 IP 或域名数据源。", "Enter at least one IP or domain source.");
        if (sources.Concat(config.PostUrls ?? []).Any(s => !IsHttpUrl(s))) return CfUi.T("数据源或上报地址必须为 HTTP(S) URL。", "Source and report URLs must use HTTP(S).");
        return null;
    }

    private static bool IsPath(string? value) => !string.IsNullOrWhiteSpace(value)
        && value.StartsWith('/') && !value.StartsWith("//") && !value.Any(char.IsControl);

    public static bool IsHttpUrl(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme is "http" or "https" && string.IsNullOrEmpty(uri.UserInfo);
}
