using System.Diagnostics;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;

namespace ServiceLib.Handler.CloudflareBestIP;

public class CfIpProber(CfBestIpItem config)
{
    private readonly ConcurrentDictionary<string, string> _coloCache = new();

    private static readonly Dictionary<string, string> ColoRegions = new()
    {
        ["HKG"] = "HONGKONG", ["MFM"] = "HONGKONG",
        ["TPE"] = "EastAsia", ["NRT"] = "EastAsia", ["KIX"] = "EastAsia", ["ICN"] = "EastAsia",
        ["SIN"] = "EastAsia", ["LAX"] = "NorthAmerica", ["SFO"] = "NorthAmerica",
        ["SEA"] = "NorthAmerica", ["DFW"] = "NorthAmerica", ["IAD"] = "NorthAmerica",
        ["LHR"] = "Europe", ["CDG"] = "Europe", ["FRA"] = "Europe", ["AMS"] = "Europe",
        ["BKK"] = "SoutheastAsia", ["KUL"] = "SoutheastAsia", ["DXB"] = "MiddleEast",
        ["JNB"] = "Africa", ["GRU"] = "SouthAmerica", ["SYD"] = "Oceania",
    };

    public async Task<CfProbeResult?> ProbeLatencyOnlyAsync(string ip, int port = 443, CancellationToken cancellationToken = default)
    {
        if (!IPAddress.TryParse(ip, out var address) || address.AddressFamily != AddressFamily.InterNetwork)
            return null;

        var latencies = new List<double>();
        var tcpOk = 0;
        var tlsOk = 0;
        var httpOk = 0;
        string? colo = null;
        string? firstWorkingSni = null;
        string? lastError = null;
        Sample? lastSuccess = null;
        var snis = config.OriginSniList.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var total = snis.Count * config.ProbeRepeat;
        if (total == 0) return null;

        foreach (var sni in snis)
        {
            for (var repeat = 0; repeat < config.ProbeRepeat; repeat++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var sample = await ProbeOnceAsync(address, port, sni, config.ProbeMode == "edge" ? null : config.OriginTestPath,
                    false, null, cancellationToken);
                if (sample.TcpOk) tcpOk++;
                if (sample.TlsOk) tlsOk++;
                if (sample.HttpOk)
                {
                    httpOk++;
                    latencies.Add(sample.LatencyMs);
                    if (sample.Colo is not null)
                    {
                        colo = sample.Colo;
                        firstWorkingSni ??= sni;
                    }
                    lastSuccess = sample;
                }
                lastError = sample.Error;
                if (repeat < config.ProbeRepeat - 1 && config.SleepInterval > 0)
                    await Task.Delay(TimeSpan.FromSeconds(config.SleepInterval), cancellationToken);
            }
        }

        if (latencies.Count == 0) return null;
        latencies.Sort();
        if (latencies.Count >= 3) latencies.RemoveAt(latencies.Count - 1); // Python reference drops only the maximum.
        var last = lastSuccess!;
        return new CfProbeResult
        {
            Ip = ip, Port = port, Sni = firstWorkingSni ?? snis[0], Colo = colo,
            Region = colo is not null && ColoRegions.TryGetValue(colo, out var region) ? region : null,
            AvgLatencyMs = latencies.Average(), TcpLossRate = 1.0 - (double)tcpOk / total,
            TlsLossRate = 1.0 - (double)tlsOk / total, HttpLossRate = 1.0 - (double)httpOk / total,
            TcpMs = last.TcpMs, TlsMs = last.TlsMs, TtfbMs = last.TtfbMs, TotalMs = last.TotalMs,
            Error = lastError
        };
    }

    public async Task<double> ProbeSpeedOnlyAsync(string ip, string? knownColo = null, int port = 443,
        CancellationToken cancellationToken = default, string? sni = null)
    {
        if (!IPAddress.TryParse(ip, out var address) || address.AddressFamily != AddressFamily.InterNetwork)
            return 0;
        sni ??= config.OriginSniList.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(sni)) return 0;
        var sample = await ProbeOnceAsync(address, port, sni, config.OriginSpeedTestPath, true, knownColo, cancellationToken);
        return sample.HttpOk ? sample.SpeedKBs : 0;
    }

    private async Task<Sample> ProbeOnceAsync(IPAddress ip, int port, string sni, string? path,
        bool speed, string? knownColo, CancellationToken cancellationToken)
    {
        var sample = new Sample();
        var timing = new WireTiming();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(config.Timeout + (speed ? config.SpeedTestDuration : 0)));
        using var handler = new SocketsHttpHandler
        {
            UseProxy = false, AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.None,
            UseCookies = false, MaxConnectionsPerServer = 1,
            ConnectTimeout = TimeSpan.FromSeconds(config.Timeout),
            SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (_, _, _, errors) =>
                    !config.OriginVerifyCert || errors == SslPolicyErrors.None,
                EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls12
                    | System.Security.Authentication.SslProtocols.Tls13,
            },
            ConnectCallback = async (_, ct) =>
            {
                var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    var start = Stopwatch.GetTimestamp();
                    await socket.ConnectAsync(new IPEndPoint(ip, port), ct);
                    timing.TcpMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                    timing.TcpDoneAt = Stopwatch.GetTimestamp();
                    sample.TcpOk = true;
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            },
            PlaintextStreamFilter = (context, _) =>
            {
                timing.TlsMs = Stopwatch.GetElapsedTime(timing.TcpDoneAt).TotalMilliseconds;
                sample.TlsOk = true;
                return ValueTask.FromResult<Stream>(new FirstByteStream(context.PlaintextStream, timing));
            }
        };
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var root = new UriBuilder("https", sni, port).Uri;
        try
        {
            var colo = knownColo;
            if (colo is null && config.ProbeMode == "full" && _coloCache.TryGetValue($"{ip}|{sni}", out var cached))
                colo = cached;
            if (colo is null)
            {
                timing.RequestAt = Stopwatch.GetTimestamp();
                using var trace = NewRequest(root, "/cdn-cgi/trace");
                using var traceResponse = await client.SendAsync(trace, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
                if (!traceResponse.IsSuccessStatusCode)
                {
                    sample.Error = $"trace HTTP {(int)traceResponse.StatusCode}";
                    return sample;
                }
                await using var traceBody = await traceResponse.Content.ReadAsStreamAsync(deadline.Token);
                using var traceBuffer = new MemoryStream();
                var traceBytes = new byte[4096];
                while (traceBuffer.Length < 65536)
                {
                    var n = await traceBody.ReadAsync(traceBytes, deadline.Token);
                    if (n == 0) break;
                    traceBuffer.Write(traceBytes, 0, n);
                }
                timing.TraceElapsedMs = Stopwatch.GetElapsedTime(timing.RequestAt).TotalMilliseconds;
                timing.FirstByteMs = Math.Max(0, Stopwatch.GetElapsedTime(timing.RequestAt,
                    timing.FirstByteAt != 0 ? timing.FirstByteAt : Stopwatch.GetTimestamp()).TotalMilliseconds
                    - timing.TcpMs - timing.TlsMs);
                colo = ExtractColo(Encoding.ASCII.GetString(traceBuffer.ToArray()));
                if (colo is null)
                {
                    sample.Error = CfUi.T("trace 缺少有效 colo", "Trace response has no valid colo");
                    return sample;
                }
                if (config.ProbeMode == "full") _coloCache[$"{ip}|{sni}"] = colo;
            }
            sample.Colo = colo;
            if (path is null) // edge mode: trace is the timed HTTP request.
            {
                sample.HttpOk = true;
                sample.TcpMs = timing.TcpMs;
                sample.TlsMs = timing.TlsMs;
                sample.TtfbMs = Math.Max(0, timing.FirstByteMs);
                sample.LatencyMs = timing.TraceElapsedMs > 0 ? timing.TraceElapsedMs : timing.TcpMs + timing.TlsMs + timing.FirstByteMs;
                sample.TotalMs = sample.LatencyMs;
                return sample;
            }

            timing.FirstByteAt = 0;
            timing.RequestAt = Stopwatch.GetTimestamp();
            timing.TcpAtRequest = timing.TcpMs;
            timing.TlsAtRequest = timing.TlsMs;
            using var request = NewRequest(root, path);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            var responseAt = Stopwatch.GetTimestamp();
            sample.TcpMs = timing.TcpMs;
            sample.TlsMs = timing.TlsMs;
            sample.TtfbMs = Math.Max(0, Stopwatch.GetElapsedTime(timing.RequestAt, timing.FirstByteAt != 0 ? timing.FirstByteAt : responseAt).TotalMilliseconds
                - (timing.TcpMs - timing.TcpAtRequest) - (timing.TlsMs - timing.TlsAtRequest));
            sample.TotalMs = Stopwatch.GetElapsedTime(timing.RequestAt, responseAt).TotalMilliseconds;
            if (!response.IsSuccessStatusCode)
            {
                sample.Error = $"HTTP {(int)response.StatusCode}";
                return sample;
            }
            if (response.Content.Headers.ContentEncoding.Any(e => !e.Equals("identity", StringComparison.OrdinalIgnoreCase)))
            {
                sample.Error = CfUi.T("测速响应经过压缩", "Download response was compressed");
                return sample;
            }
            if (speed)
            {
                await using var body = await response.Content.ReadAsStreamAsync(deadline.Token);
                var buffer = new byte[65536];
                long bytes = 0;
                var httpStart = timing.RequestAt;
                var limitMs = config.SpeedTestDuration * 1000.0;
                while (Stopwatch.GetElapsedTime(httpStart).TotalMilliseconds < limitMs)
                {
                    int n;
                    try { n = await body.ReadAsync(buffer, deadline.Token); }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && bytes > 0) { break; }
                    if (n == 0) break;
                    bytes += n;
                    if (bytes > 64L * 1024 * 1024) break;
                }
                if (bytes == 0) { sample.Error = CfUi.T("测速响应为空", "Download response was empty"); return sample; }
                var elapsedMs = Math.Max(1, Stopwatch.GetElapsedTime(httpStart).TotalMilliseconds
                    - (timing.TcpMs - timing.TcpAtRequest) - (timing.TlsMs - timing.TlsAtRequest));
                sample.SpeedKBs = bytes / 1024.0 / (elapsedMs / 1000.0);
            }
            sample.HttpOk = true;
            sample.LatencyMs = sample.TcpMs + sample.TlsMs + sample.TtfbMs;
            return sample;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            sample.Error = ex.Message;
            return sample;
        }
    }

    private static HttpRequestMessage NewRequest(Uri root, string path)
    {
        var separator = path.Contains('?') ? '&' : '?';
        var request = new HttpRequestMessage(HttpMethod.Get, new Uri(root, path + separator + "t=" + Stopwatch.GetTimestamp()))
        {
            Version = System.Net.HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };
        request.Headers.TryAddWithoutValidation("Accept-Encoding", "identity");
        request.Headers.ConnectionClose = false;
        return request;
    }

    private static string? ExtractColo(string text)
    {
        foreach (var line in text.Split('\n'))
        {
            if (!line.StartsWith("colo=", StringComparison.OrdinalIgnoreCase)) continue;
            var code = line[5..].Trim().ToUpperInvariant();
            if (code.Length == 3 && code.All(c => c is >= 'A' and <= 'Z')) return code;
        }
        return null;
    }

    private sealed class Sample
    {
        public bool TcpOk, TlsOk, HttpOk;
        public string? Colo, Error;
        public double TcpMs, TlsMs, TtfbMs, TotalMs, LatencyMs, SpeedKBs;
    }

    private sealed class WireTiming
    {
        public double TcpMs, TlsMs, TcpAtRequest, TlsAtRequest, FirstByteMs, TraceElapsedMs;
        public long TcpDoneAt, RequestAt, FirstByteAt;
    }

    private sealed class FirstByteStream(Stream inner, WireTiming timing) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken ct) => inner.FlushAsync(ct);
        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = inner.Read(buffer, offset, count);
            Mark(n);
            return n;
        }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            var n = await inner.ReadAsync(buffer, ct);
            Mark(n);
            return n;
        }
        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        {
            var n = await inner.ReadAsync(buffer, offset, count, ct);
            Mark(n);
            return n;
        }
        private void Mark(int n)
        {
            if (n > 0 && timing.RequestAt != 0)
                Interlocked.CompareExchange(ref timing.FirstByteAt, Stopwatch.GetTimestamp(), 0);
        }
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) => inner.WriteAsync(buffer, ct);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
        public override ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
