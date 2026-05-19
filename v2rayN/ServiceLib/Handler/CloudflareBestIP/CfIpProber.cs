using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Diagnostics;

namespace ServiceLib.Handler.CloudflareBestIP;

/// <summary>
/// 单 IP 探测引擎 / Single-IP probe engine
///
/// 两个独立方法 / Two independent methods:
///   ProbeLatencyOnlyAsync — TCP → TLS → /cdn-cgi/trace(取colo) → GET testPath(测延迟)
///                          并发安全，不测速，不抢带宽 / concurrency-safe, no bandwidth competition
///   ProbeSpeedOnlyAsync   — 独立 TCP+TLS 连接 → GET speedTestPath → 测下载速度 KB/s
///                          必须串行调用，避免并发下载抢带宽 / MUST be called sequentially
/// </summary>
public class CfIpProber
{
    private readonly CfBestIpItem _config;
    private readonly string _sniHost;

    // colo 代码 → 地理区域映射表（对标 Python 版 _COLO_REGION_MAP）
    // colo code → geographic region map (matches Python _COLO_REGION_MAP)
    private static readonly Dictionary<string, string> _coloRegionMap = new()
    {
        ["HKG"] = "HONGKONG", ["MFM"] = "HONGKONG",
        ["TPE"] = "EastAsia", ["TSA"] = "EastAsia", ["NRT"] = "EastAsia", ["HND"] = "EastAsia",
        ["KIX"] = "EastAsia", ["NGO"] = "EastAsia", ["FUK"] = "EastAsia", ["CTS"] = "EastAsia",
        ["ICN"] = "EastAsia", ["GMP"] = "EastAsia", ["PUS"] = "EastAsia", ["SIN"] = "EastAsia",
        ["LAX"] = "NorthAmerica", ["SFO"] = "NorthAmerica", ["SJC"] = "NorthAmerica",
        ["SEA"] = "NorthAmerica", ["PDX"] = "NorthAmerica", ["DFW"] = "NorthAmerica",
        ["DEN"] = "NorthAmerica", ["ORD"] = "NorthAmerica", ["ATL"] = "NorthAmerica",
        ["MIA"] = "NorthAmerica", ["IAD"] = "NorthAmerica", ["EWR"] = "NorthAmerica",
        ["BOS"] = "NorthAmerica", ["YVR"] = "NorthAmerica", ["YYZ"] = "NorthAmerica",
        ["LHR"] = "Europe", ["CDG"] = "Europe", ["FRA"] = "Europe", ["AMS"] = "Europe",
        ["MAD"] = "Europe", ["DUB"] = "Europe", ["ZRH"] = "Europe", ["VIE"] = "Europe",
        ["BKK"] = "SoutheastAsia", ["KUL"] = "SoutheastAsia", ["MNL"] = "SoutheastAsia",
        ["SGN"] = "SoutheastAsia", ["CGK"] = "SoutheastAsia",
        ["DXB"] = "MiddleEast", ["AUH"] = "MiddleEast", ["DOH"] = "MiddleEast",
        ["JNB"] = "Africa", ["CPT"] = "Africa",
        ["GRU"] = "SouthAmerica", ["GIG"] = "SouthAmerica",
        ["SYD"] = "Oceania", ["MEL"] = "Oceania", ["BNE"] = "Oceania", ["AKL"] = "Oceania",
    };

    // trace 请求头 — 不含 Accept-Encoding，确保 CF 返回明文 colo 信息
    // trace request headers — no Accept-Encoding so CF returns plaintext colo
    private static readonly string _traceHeaders =
        "User-Agent: Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36\r\n" +
        "Accept: text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8\r\n" +
        "Accept-Language: zh-CN,zh;q=0.9,en;q=0.8\r\n";

    // 下载请求头 — 含 Accept-Encoding，模拟 Chrome 128 完整指纹
    // download request headers — with Accept-Encoding, full Chrome 128 fingerprint
    private static readonly string _downloadHeaders =
        "User-Agent: Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36\r\n" +
        "Accept: text/html,application/xhtml+xml,application/xml;q=0.9,image/webp,image/apng,*/*;q=0.8\r\n" +
        "Accept-Language: zh-CN,zh;q=0.9,en;q=0.8\r\n" +
        "Accept-Encoding: gzip, deflate, br\r\n";

    public CfIpProber(CfBestIpItem config)
    {
        _config = config;
        _sniHost = config.OriginSniList?.FirstOrDefault() ?? string.Empty;
    }

    /// <summary>
    /// 仅延迟探测（可并发）：重复 ProbeRepeat 次 TCP+TLS+HTTP 延迟测量，取 colo
    /// Latency-only probe (concurrency-safe): repeat ProbeRepeat times, get colo + latency
    ///
    /// 不包含速度测试 — 速度测试必须串行，由调用方在并发延迟探测完成后单独执行
    /// No speed test included — caller must run sequential speed tests separately
    /// </summary>
    /// <returns>含 colo/延迟/丢包率的探测结果（DownloadSpeedKBs=0），全失败返回 null</returns>
    public async Task<CfProbeResult?> ProbeLatencyOnlyAsync(string ip, int port = 443)
    {
        var latencies = new List<double>();
        var tcpFails = 0;
        var tlsFails = 0;
        string? colo = null;
        // 保存最后一次成功探测的分层耗时，对标 Python last_tcp_ms / last_tls_ms / etc.
        var lastTcpMs = 0.0;
        var lastTlsMs = 0.0;
        var lastTtfbMs = 0.0;
        var lastTotalMs = 0.0;

        for (var i = 0; i < _config.ProbeRepeat; i++)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(_config.Timeout));
                var result = await ProbeFullPathAsync(ip, port, _config.OriginTestPath, colo, cts.Token);
                if (result == null)
                {
                    tcpFails++;
                    tlsFails++;
                }
                else
                {
                    if (result.Value.LatencyMs > 0)
                        latencies.Add(result.Value.LatencyMs);
                    if (result.Value.TcpFailed) tcpFails++;
                    if (result.Value.TlsFailed) tlsFails++;
                    if (result.Value.Colo != null) colo = result.Value.Colo;
                    if (result.Value.TcpMs > 0) lastTcpMs = result.Value.TcpMs;
                    if (result.Value.TlsMs > 0) lastTlsMs = result.Value.TlsMs;
                    lastTtfbMs = result.Value.TtfbMs;
                    lastTotalMs = result.Value.TotalMs;
                }
            }
            catch
            {
                tcpFails++;
                tlsFails++;
            }

            if (_config.SleepInterval > 0 && i < _config.ProbeRepeat - 1)
                await Task.Delay(_config.SleepInterval * 1000);
        }

        if (latencies.Count == 0) return null;

        var avgLatency = CalculateTrimmedMean(latencies.ToArray());
        var tcpLoss = (double)tcpFails / _config.ProbeRepeat;
        var tlsLoss = (double)tlsFails / _config.ProbeRepeat;

        return new CfProbeResult
        {
            Ip = ip,
            Port = port,
            Colo = colo,
            Region = colo != null ? GetRegion(colo) : null,
            AvgLatencyMs = avgLatency,
            TcpLossRate = tcpLoss,
            TlsLossRate = tlsLoss,
            DownloadSpeedKBs = 0,
            TcpMs = lastTcpMs,
            TlsMs = lastTlsMs,
            TtfbMs = lastTtfbMs,
            TotalMs = lastTotalMs,
        };
    }

    /// <summary>
    /// 仅速度测试（必须串行调用）：新建独立连接 → GET speedTestPath → 计算下载速度 KB/s
    /// Speed-only test (MUST be called sequentially): fresh connection → GET speedTestPath → KB/s
    ///
    /// 为什么不能并发：多个并发下载会互相抢占带宽，导致所有 IP 测速结果偏低且无区分度
    /// Why sequential: concurrent downloads compete for bandwidth, making all results low & undifferentiated
    /// </summary>
    /// <param name="knownColo">已知 colo，跳过 trace 请求节省时间</param>
    /// <returns>下载速度 KB/s，失败返回 0</returns>
    public async Task<double> ProbeSpeedOnlyAsync(string ip, string? knownColo = null, int port = 443)
    {
        var speedPath = _config.OriginSpeedTestPath;
        if (speedPath.IsNullOrEmpty()) return 0;

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var result = await ProbeFullPathAsync(ip, port, speedPath, knownColo, cts.Token);
            return result?.DownloadSpeedKBs ?? 0;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// 单次完整路径探测：TCP 连接 → TLS 握手 → (可选)trace取colo → 下载testPath文件测延迟/速度
    /// Single full-path probe: TCP → TLS → (optional)trace for colo → GET testPath for latency/speed
    ///
    /// 对标 Python 版 probe_full_path：
    ///   - trace 请求使用 Connection: keep-alive，不压缩（确保可读 colo）
    ///   - 文件下载使用 Connection: close，含 Accept-Encoding（测真实下载速度）
    /// </summary>
    /// <param name="knownColo">已知 colo 时跳过 trace 请求 / skip trace if already known</param>
    private async Task<ProbeResultRaw?> ProbeFullPathAsync(string ip, int port, string testPath, string? knownColo, CancellationToken ct)
    {
        var hostHeader = _sniHost.IsNotEmpty() ? _sniHost : ip;
        var totalSw = Stopwatch.StartNew();

        // ── 1. TCP 连接 / TCP connect ──
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.ReceiveTimeout = _config.Timeout * 1000;
        socket.SendTimeout = _config.Timeout * 1000;

        var tcpSw = Stopwatch.StartNew();
        try
        {
            await socket.ConnectAsync(new IPEndPoint(IPAddress.Parse(ip), port), ct);
        }
        catch { return null; }
        var tcpMs = tcpSw.Elapsed.TotalMilliseconds;
        tcpSw.Stop();

        // ── 2. TLS 握手 / TLS handshake ──
        var tlsFailed = false;
        var tlsMs = 0.0;
        Stream stream = new NetworkStream(socket, false);
        try
        {
            var tlsSw = Stopwatch.StartNew();
            var sslStream = new SslStream(stream, false,
                (sender, cert, chain, errors) => !_config.OriginVerifyCert || errors == SslPolicyErrors.None);
            await sslStream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = hostHeader,
                EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13,
            }, ct);
            tlsMs = tlsSw.Elapsed.TotalMilliseconds;
            tlsSw.Stop();
            stream = sslStream;
        }
        catch { tlsFailed = true; }

        if (tlsFailed)
        {
            stream.Dispose();
            return new ProbeResultRaw { TcpFailed = false, TlsFailed = true };
        }

        // ── 3. 获取 colo（/cdn-cgi/trace，keep-alive，无压缩）──
        string? colo = knownColo;
        if (colo == null)
        {
            try
            {
                var traceResp = await SendTraceRequestAsync(stream, hostHeader, ct);
                if (traceResp != null)
                    colo = ExtractColo(traceResp);
            }
            catch { }
        }

        // ── 4. 下载测试文件（Connection: close）──
        var ttfbMs = 0.0;
        var downloadSpeed = 0.0;
        try
        {
            var (data, ttfb) = await SendDownloadRequestWithTtfbAsync(stream, hostHeader, testPath, ct);
            ttfbMs = ttfb;
            if (data != null)
            {
                // 对标 Python: download_speed = bytes / time_from_first_byte_to_end
                // Python: download_speed = (data_length / 1024) / (t_end - t_first_byte)
                var speedTime = totalSw.Elapsed.TotalMilliseconds - tcpMs - tlsMs - ttfbMs;
                if (speedTime > 0)
                    downloadSpeed = data.Length / 1024.0 / (speedTime / 1000.0);
            }
        }
        catch { }

        var totalMs = totalSw.Elapsed.TotalMilliseconds;
        totalSw.Stop();
        stream.Dispose();

        return new ProbeResultRaw
        {
            LatencyMs = tcpMs + ttfbMs,   // 对标 Python lat = tcp_ms + ttfb_ms
            Colo = colo,
            DownloadSpeedKBs = downloadSpeed,
            TcpFailed = false,
            TlsFailed = false,
            TcpMs = tcpMs,
            TlsMs = tlsMs,
            TtfbMs = ttfbMs,
            TotalMs = totalMs,
        };
    }

    /// <summary>
    /// 发送 /cdn-cgi/trace 请求（keep-alive，无 Accept-Encoding）
    /// Send /cdn-cgi/trace request with keep-alive, no Accept-Encoding
    /// 为什么不用 Accept-Encoding：CF 压缩后 ExtractColo 无法从二进制中解析 colo=
    /// </summary>
    private static async Task<string?> SendTraceRequestAsync(Stream stream, string host, CancellationToken ct)
    {
        var request = $"GET /cdn-cgi/trace HTTP/1.1\r\n" +
                      $"Host: {host}\r\n" +
                      "Connection: keep-alive\r\n" +
                      _traceHeaders +
                      "\r\n";

        var requestBytes = Encoding.ASCII.GetBytes(request);
        await stream.WriteAsync(requestBytes, ct);
        await stream.FlushAsync(ct);

        using var ms = new MemoryStream();
        var buffer = new byte[4096];
        try
        {
            while (ms.Length < 8192)
            {
                if (stream is SslStream ssl && !ssl.CanRead) break;
                var read = await stream.ReadAsync(buffer, ct);
                if (read == 0) break;
                ms.Write(buffer, 0, read);
                var text = Encoding.ASCII.GetString(ms.ToArray());
                if (text.Contains("colo=") && text.Contains("\n\n"))
                    break;
            }
        }
        catch { }

        return ms.Length > 0 ? Encoding.ASCII.GetString(ms.ToArray()) : null;
    }

    /// <summary>
    /// 发送文件下载请求（Connection: close），返回 (data, ttfbMs)
    /// TTFB 对标 Python: 从请求发送完毕到收到第一个字节的耗时
    /// </summary>
    private static async Task<(byte[]? data, double ttfbMs)> SendDownloadRequestWithTtfbAsync(Stream stream, string host, string path, CancellationToken ct)
    {
        var request = $"GET {path} HTTP/1.1\r\n" +
                      $"Host: {host}\r\n" +
                      "Connection: close\r\n" +
                      _downloadHeaders +
                      "Upgrade-Insecure-Requests: 1\r\n" +
                      "\r\n";

        var requestBytes = Encoding.ASCII.GetBytes(request);
        await stream.WriteAsync(requestBytes, ct);
        await stream.FlushAsync(ct);

        var ttfbSw = Stopwatch.StartNew();
        var totalRead = 0;

        try
        {
            using var ms = new MemoryStream();
            var buffer = new byte[131072];
            var firstByte = true;
            var ttfbMs = 0.0;

            while (totalRead < 10 * 1024 * 1024)
            {
                var read = await stream.ReadAsync(buffer, ct);
                if (read == 0) break;
                if (firstByte)
                {
                    ttfbMs = ttfbSw.Elapsed.TotalMilliseconds;
                    firstByte = false;
                }
                ms.Write(buffer, 0, read);
                totalRead += read;
            }

            return (ms.Length > 0 ? ms.ToArray() : null, ttfbMs);
        }
        catch
        {
            return (null, 0);
        }
    }

    /// <summary>
    /// 从 /cdn-cgi/trace 响应中提取 colo 代码 / Extract colo code from trace response
    /// 响应示例: fl=123f45\nh=www.example.com\nip=1.2.3.4\ncolo=HKG
    /// </summary>
    private static string? ExtractColo(string httpResponse)
    {
        foreach (var line in httpResponse.Split('\n'))
        {
            var trimmed = line.TrimEnd('\r');
            if (trimmed.StartsWith("colo=", StringComparison.OrdinalIgnoreCase))
                return trimmed.Substring(5);
        }
        return null;
    }

    internal static string? GetRegion(string? colo)
    {
        if (colo == null) return null;
        return _coloRegionMap.TryGetValue(colo.ToUpperInvariant(), out var region) ? region : null;
    }

    /// <summary>
    /// 去尾均值：≥3 样本时去掉最小和最大值后取平均，减少异常值影响
    /// Trimmed mean: drop min & max when ≥3 samples to reduce outlier impact
    /// </summary>
    private static double CalculateTrimmedMean(double[] values)
    {
        if (values.Length == 0) return 0;
        Array.Sort(values);
        if (values.Length >= 3)
            return values[1..^1].Average();
        return values.Average();
    }

    private struct ProbeResultRaw
    {
        public double LatencyMs;
        public string? Colo;
        public double DownloadSpeedKBs;
        public bool TcpFailed;
        public bool TlsFailed;
        public double TcpMs;
        public double TlsMs;
        public double TtfbMs;
        public double TotalMs;
    }
}

public class CfProbeResult
{
    public string Ip { get; set; }
    public int Port { get; set; }
    /// <summary>Cloudflare 数据中心代码 / Cloudflare data center code</summary>
    public string? Colo { get; set; }
    /// <summary>地理区域 / geographic region</summary>
    public string? Region { get; set; }
    /// <summary>平均延迟(ms)，去尾均值 / average latency in ms, trimmed mean</summary>
    public double AvgLatencyMs { get; set; }
    /// <summary>TCP 连接失败率 / TCP connection failure rate</summary>
    public double TcpLossRate { get; set; }
    /// <summary>TLS 握手失败率 / TLS handshake failure rate</summary>
    public double TlsLossRate { get; set; }
    /// <summary>下载速度 (KB/s) / download speed in KB/s</summary>
    public double DownloadSpeedKBs { get; set; }
    /// <summary>综合评分（越低越好）/ composite score (lower is better)</summary>
    public double Score { get; set; }
    /// <summary>数据来源URL / data source URL</summary>
    public string? Source { get; set; }
    /// <summary>TCP 连接耗时(ms) / TCP connect time in ms</summary>
    public double TcpMs { get; set; }
    /// <summary>TLS 握手耗时(ms) / TLS handshake time in ms</summary>
    public double TlsMs { get; set; }
    /// <summary>首字节耗时(ms) / TTFB in ms</summary>
    public double TtfbMs { get; set; }
    /// <summary>总耗时(ms) / total time in ms</summary>
    public double TotalMs { get; set; }
}
