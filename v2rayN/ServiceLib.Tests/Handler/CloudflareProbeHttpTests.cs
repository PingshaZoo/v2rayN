using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using ServiceLib.Handler.CloudflareBestIP;

namespace ServiceLib.Tests.Handler;

public class CloudflareProbeHttpTests
{
    [Test]
    public async Task LocalTlsServerExercisesStatusChunkedTruncationAndBothModes()
    {
        await using var server = new LocalTlsServer();
        var settings = new CfBestIpItem
        {
            OriginSniList = ["localhost", "alternate.localhost"],
            OriginTestPath = "/ok", OriginSpeedTestPath = "/speed",
            ProbeRepeat = 2, SleepInterval = 0, Timeout = 3, SpeedTestDuration = 2,
            OriginVerifyCert = false
        };
        var logs = new List<string>();
        var prober = new CfIpProber(settings, logs.Add);
        CfProbeResult? result;
        try { result = await prober.ProbeLatencyOnlyAsync("127.0.0.1", server.Port); }
        catch (Exception ex) { throw new InvalidOperationException($"{ex.Message}\nSERVER: {string.Join(" | ", server.Errors)}", ex); }
        await (result is not null).Should().BeTrue();
        await result!.Colo.Should().BeEqualTo("HKG");
        await result.Sni.Should().BeEqualTo("localhost");
        await result.TcpLossRate.Should().BeEqualTo(0);
        await result.TlsLossRate.Should().BeEqualTo(0);
        await result.HttpLossRate.Should().BeEqualTo(0);
        await (result.AvgLatencyMs > 0).Should().BeTrue();
        await server.SeenHosts.Contains("alternate.localhost").Should().BeTrue();

        var speed = await prober.ProbeSpeedOnlyAsync("127.0.0.1", result.Colo, server.Port,
            sni: result.Sni);
        await (speed > 2 && speed < 20).Should().BeTrue();

        settings.OriginTestPath = "/bad";
        var bad = await new CfIpProber(settings, logs.Add).ProbeLatencyOnlyAsync("127.0.0.1", server.Port);
        await (bad is null).Should().BeTrue();
        await logs.Any(line => line.Contains("127.0.0.1") && line.Contains("HTTP 503") && line.Contains("SNI=localhost")).Should().BeTrue();

        settings.OriginSpeedTestPath = "/truncated";
        var truncated = await new CfIpProber(settings, logs.Add).ProbeSpeedOnlyAsync("127.0.0.1", "HKG", server.Port);
        await truncated.Should().BeEqualTo(0);
        await logs.Any(line => line.Contains("download:")).Should().BeTrue();

        settings.ProbeMode = "edge";
        var edge = await new CfIpProber(settings).ProbeLatencyOnlyAsync("127.0.0.1", server.Port);
        await (edge is not null && edge.AvgLatencyMs > 0).Should().BeTrue();

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var didCancel = false;
        try { await new CfIpProber(settings).ProbeLatencyOnlyAsync("127.0.0.1", server.Port, cancelled.Token); }
        catch (OperationCanceledException) { didCancel = true; }
        await didCancel.Should().BeTrue();
    }

    private sealed class LocalTlsServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stopping = new();
        private readonly X509Certificate2 _certificate;
        private readonly Task _acceptLoop;
        public ConcurrentBag<string> SeenHosts { get; } = [];
        public ConcurrentBag<string> Errors { get; } = [];
        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public LocalTlsServer()
        {
            using var rsa = RSA.Create(2048);
            var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var san = new SubjectAlternativeNameBuilder();
            san.AddDnsName("localhost");
            request.CertificateExtensions.Add(san.Build());
            using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
            _certificate = X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pfx), null,
                X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.PersistKeySet);
            _listener.Start();
            _acceptLoop = AcceptLoopAsync();
        }

        private async Task AcceptLoopAsync()
        {
            while (!_stopping.IsCancellationRequested)
            {
                try
                {
                    var tcp = await _listener.AcceptTcpClientAsync(_stopping.Token);
                    _ = ServeAsync(tcp);
                }
                catch (OperationCanceledException) { break; }
                catch (SocketException) when (_stopping.IsCancellationRequested) { break; }
            }
        }

        private async Task ServeAsync(TcpClient tcp)
        {
            try
            {
                using (tcp)
                await using (var stream = new SslStream(tcp.GetStream(), false))
                {
                    await stream.AuthenticateAsServerAsync(_certificate, false, SslProtocols.Tls12 | SslProtocols.Tls13, false);
                    while (!_stopping.IsCancellationRequested)
                    {
                        var headers = await ReadHeadersAsync(stream, _stopping.Token);
                        if (headers is null) break;
                        var path = headers.Split(' ')[1].Split('?')[0];
                        foreach (var line in headers.Split("\r\n"))
                            if (line.StartsWith("Host:", StringComparison.OrdinalIgnoreCase))
                                SeenHosts.Add(line[5..].Trim().Split(':')[0]);
                        switch (path)
                        {
                            case "/cdn-cgi/trace":
                                await WriteAsync(stream, "HTTP/1.1 200 OK\r\nContent-Length: 9\r\nConnection: keep-alive\r\n\r\ncolo=HKG\n");
                                break;
                            case "/ok":
                                await WriteAsync(stream, "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: keep-alive\r\n\r\nok");
                                break;
                            case "/bad":
                                await WriteAsync(stream, "HTTP/1.1 503 Bad\r\nContent-Length: 0\r\nConnection: keep-alive\r\n\r\n");
                                break;
                            case "/speed":
                                await WriteAsync(stream, "HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\nConnection: keep-alive\r\n\r\n");
                                await Task.Delay(200, _stopping.Token);
                                await WriteAsync(stream, "400\r\n" + new string('x', 1024) + "\r\n0\r\n\r\n");
                                break;
                            case "/truncated":
                                await WriteAsync(stream, "HTTP/1.1 200 OK\r\nContent-Length: 100\r\nConnection: close\r\n\r\nxx");
                                return;
                        }
                    }
                }
            }
            catch (Exception ex) { Errors.Add(ex.ToString()); }
        }

        private static async Task<string?> ReadHeadersAsync(Stream stream, CancellationToken ct)
        {
            using var bytes = new MemoryStream();
            var one = new byte[1];
            while (bytes.Length < 8192)
            {
                var read = await stream.ReadAsync(one, ct);
                if (read == 0) return null;
                bytes.WriteByte(one[0]);
                var n = bytes.Length;
                if (n >= 4)
                {
                    var data = bytes.GetBuffer();
                    if (data[n - 4] == 13 && data[n - 3] == 10 && data[n - 2] == 13 && data[n - 1] == 10)
                        return Encoding.ASCII.GetString(data, 0, (int)n);
                }
            }
            return null;
        }

        private static Task WriteAsync(Stream stream, string value) =>
            stream.WriteAsync(Encoding.ASCII.GetBytes(value)).AsTask();

        public async ValueTask DisposeAsync()
        {
            _stopping.Cancel();
            _listener.Stop();
            await _acceptLoop;
            _certificate.Dispose();
            _stopping.Dispose();
        }
    }
}
