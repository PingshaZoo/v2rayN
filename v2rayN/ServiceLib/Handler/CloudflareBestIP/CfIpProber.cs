using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Diagnostics;

namespace ServiceLib.Handler.CloudflareBestIP;

public class CfIpProber
{
    private readonly CfBestIpItem _config;
    private readonly string _sniHost;

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

    // Chrome 128 fingerprint WITHOUT Accept-Encoding (for trace requests — must not be compressed)
    private static readonly string _traceHeaders =
        "User-Agent: Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36\r\n" +
        "Accept: text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8\r\n" +
        "Accept-Language: zh-CN,zh;q=0.9,en;q=0.8\r\n";

    // Full fingerprint WITH Accept-Encoding (for file downloads)
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

    public async Task<CfProbeResult?> ProbeSingleIpAsync(string ip, int port = 443)
    {
        var latencies = new List<double>();
        var tcpFails = 0;
        var tlsFails = 0;
        string? colo = null;

        // Phase 1: latency probes (ProbeRepeat times)
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

        // Phase 2: speed test (single run on fresh connection, using known colo)
        double downloadSpeed = 0;
        var speedPath = _config.OriginSpeedTestPath;
        if (speedPath.IsNotEmpty())
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15)); // longer timeout for speed test
                var speedResult = await ProbeFullPathAsync(ip, port, speedPath, colo, cts.Token);
                if (speedResult?.DownloadSpeedKBs > 0)
                    downloadSpeed = speedResult.Value.DownloadSpeedKBs;
            }
            catch { }
        }

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
            DownloadSpeedKBs = downloadSpeed,
        };
    }

    /// <summary>
    /// Single probe: TCP → TLS → (optional trace for colo) → download testPath file.
    /// Matches Python probe_full_path: keep-alive on trace, close on file download.
    /// </summary>
    private async Task<ProbeResultRaw?> ProbeFullPathAsync(string ip, int port, string testPath, string? knownColo, CancellationToken ct)
    {
        var hostHeader = _sniHost.IsNotEmpty() ? _sniHost : ip;

        // 1. TCP connect
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.ReceiveTimeout = _config.Timeout * 1000;
        socket.SendTimeout = _config.Timeout * 1000;

        var tcpSw = Stopwatch.StartNew();
        try
        {
            await socket.ConnectAsync(new IPEndPoint(IPAddress.Parse(ip), port), ct);
        }
        catch { return null; }
        var tcpTime = tcpSw.Elapsed.TotalMilliseconds;
        tcpSw.Stop();

        // 2. TLS handshake (always required)
        var tlsFailed = false;
        Stream stream = new NetworkStream(socket, false);
        try
        {
            var sslStream = new SslStream(stream, false,
                (sender, cert, chain, errors) => !_config.OriginVerifyCert || errors == SslPolicyErrors.None);
            await sslStream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = hostHeader,
                EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13,
            }, ct);
            stream = sslStream;
        }
        catch { tlsFailed = true; }

        if (tlsFailed)
        {
            stream.Dispose();
            return new ProbeResultRaw { TcpFailed = false, TlsFailed = true };
        }

        // 3. Get colo from /cdn-cgi/trace (keep-alive, no Accept-Encoding — must not be compressed)
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

        // 4. Download test file (Connection: close, with Accept-Encoding)
        var latencySw = Stopwatch.StartNew();
        double latency = tcpTime;
        double downloadSpeed = 0;
        try
        {
            var data = await SendDownloadRequestAsync(stream, hostHeader, testPath, ct);
            latency = latencySw.Elapsed.TotalMilliseconds;
            if (data != null && latencySw.Elapsed.TotalSeconds > 0)
                downloadSpeed = data.Length / 1024.0 / latencySw.Elapsed.TotalSeconds;
        }
        catch { }
        latencySw.Stop();

        stream.Dispose();
        return new ProbeResultRaw
        {
            LatencyMs = latency,
            Colo = colo,
            DownloadSpeedKBs = downloadSpeed,
            TcpFailed = false,
            TlsFailed = false,
        };
    }

    /// <summary>
    /// Send /cdn-cgi/trace request with keep-alive. No Accept-Encoding so CF returns plain text.
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

        // Read response until we have colo= line or headers+body end
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
    /// Send file download request with Connection: close. Full Accept-Encoding headers.
    /// </summary>
    private static async Task<byte[]?> SendDownloadRequestAsync(Stream stream, string host, string path, CancellationToken ct)
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

        using var ms = new MemoryStream();
        var buffer = new byte[131072]; // 128KB buffer like Python
        var totalRead = 0;

        try
        {
            while (totalRead < 10 * 1024 * 1024) // max 10MB
            {
                var read = await stream.ReadAsync(buffer, ct);
                if (read == 0) break;
                ms.Write(buffer, 0, read);
                totalRead += read;
            }
        }
        catch { }

        return ms.Length > 0 ? ms.ToArray() : null;
    }

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
    }
}

public class CfProbeResult
{
    public string Ip { get; set; }
    public int Port { get; set; }
    public string? Colo { get; set; }
    public string? Region { get; set; }
    public double AvgLatencyMs { get; set; }
    public double TcpLossRate { get; set; }
    public double TlsLossRate { get; set; }
    public double DownloadSpeedKBs { get; set; }
    public double Score { get; set; }
}
