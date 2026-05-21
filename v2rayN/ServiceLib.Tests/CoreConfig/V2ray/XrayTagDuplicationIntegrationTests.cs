using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using AwesomeAssertions;
using Xunit;

namespace ServiceLib.Tests.CoreConfig.V2ray;

/// <summary>
/// xray balancer selector behavior contract test.
///
/// Verified: xray v26.3.27 deduplicates selector entries via prefix-match + dedup
/// in SelectOutbounds(). Tag duplication does NOT create weighted selection.
///   selector [A, A, A, B] → candidates = [A, B] → ~50/50 (NOT 75/25)
///
/// This test serves as a permanent regression detector. If xray ever changes
/// selector semantics (e.g. stops deduplicating), these tests will break and
/// the entire adaptive scheduling architecture must be re-evaluated.
///
/// Strategy: two local HTTP observer servers. Outbound A redirects to observer A,
/// outbound B redirects to observer B. Count connections directly.
/// Each request uses a fresh TCP connection (no keepalive).
/// </summary>
[Trait("Category", "Integration")]
public sealed class XrayTagDuplicationIntegrationTests : IAsyncDisposable
{
    private readonly ITestOutputHelper _output;
    private Process? _xrayProcess;
    private ObserverServer? _observerA;
    private ObserverServer? _observerB;
    private int _socksPort;

    public XrayTagDuplicationIntegrationTests(ITestOutputHelper output)
    {
        _output = output;
    }

    /// <summary>
    /// Sanity check: selector [A] only — confirms the balancer is active and
    /// routing through the correct outbound. Should get ~100% A.
    /// </summary>
    [Fact]
    public async Task XrayBalancer_SingleSelector_ShouldRouteAllToA()
    {
        var (countA, countB) = await RunWithSelectorAsync(["A"]);
        Log($"[TEST] Single [A]: A={countA}, B={countB}");
        countB.Should().Be(0, "B should never be selected when selector is [A] only");
        countA.Should().BeGreaterThan(900, "nearly all requests should reach A");
    }

    /// <summary>
    /// P0 contract test: selector [A, A, A, B] with N=1000 fresh connections.
    /// Because xray deduplicates selector entries, the expected distribution is
    /// ~50/50 (uniform random between unique outbounds), NOT 75/25.
    ///
    /// If this test ever shows A significantly above ~55%, xray may have changed
    /// its selector semantics — trigger full architecture review.
    /// </summary>
    [Fact]
    public async Task XrayBalancer_DuplicateSelector_ShouldBeUniformDueToDedup()
    {
        var (countA, countB) = await RunWithSelectorAsync(["A", "A", "A", "B"]);
        Log($"[TEST] Duplicate [A,A,A,B]: A={countA}, B={countB}");

        double pctA = (double)countA / (countA + countB) * 100.0;
        Log($"[TEST] A hit rate: {pctA:F1}% (expected ~50% due to xray dedup)");

        // xray deduplicates selector entries, so [A,A,A,B] → candidates [A,B] → ~50/50.
        // 95% CI for N=1000, p=0.5: [46.9%, 53.1%]. Use [40%, 60%] for safety.
        pctA.Should().BeInRange(40.0, 60.0,
            $"xray deduplicates selector entries, expected ~50%, got {pctA:F1}%");
    }

    /// <summary>
    /// Measure xray restart (kill + start → SOCKS5 ready) time.
    /// This is the connection-interruption duration that users experience
    /// every time the adaptive scheduler triggers a config reload.
    /// </summary>
    [Fact]
    public async Task XrayRestart_ShouldCompleteWithinFiveSeconds()
    {
        int portA = GetFreePort();
        _socksPort = GetFreePort();

        _observerA = new ObserverServer(portA);
        _observerA.Start();

        var configJson = BuildConfig(portA, 0, ["A"]);
        var configPath = Path.Combine(Path.GetTempPath(), $"xray-test-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(configPath, configJson);

        try
        {
            // First start
            _xrayProcess = StartXray(configPath);
            Log($"[TEST] First xray-core PID={_xrayProcess.Id}");
            await WaitForSocksReadyAsync();
            Log("[TEST] First SOCKS5 ready");

            // Kill old process
            var oldPid = _xrayProcess.Id;
            var t0 = Stopwatch.GetTimestamp();
            _xrayProcess.Kill(entireProcessTree: true);
            await Task.Delay(500); // let OS release the port
            Log($"[TEST] Killed old process PID={oldPid}");

            // Start new process
            _xrayProcess = StartXray(configPath);
            Log($"[TEST] New xray-core PID={_xrayProcess.Id}");

            // Measure time to SOCKS5 ready
            await WaitForSocksReadyAsync();
            double restartMs = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
            Log($"[TEST] xray restart time: {restartMs:F0}ms");

            restartMs.Should().BeLessThan(5000,
                $"xray restart should complete within 5s, took {restartMs:F0}ms");
        }
        finally
        {
            try { File.Delete(configPath); } catch { /* best-effort */ }
        }
    }

    // ── Shared test harness ──────────────────────────────────

    private async Task<(int CountA, int CountB)> RunWithSelectorAsync(string[] selector)
    {
        int portA = GetFreePort();
        int portB = GetFreePort();
        _socksPort = GetFreePort();

        _observerA = new ObserverServer(portA);
        _observerB = new ObserverServer(portB);
        _observerA.Start();
        _observerB.Start();
        Log($"[TEST] Observer A on :{portA}, Observer B on :{portB}");

        var configJson = BuildConfig(portA, portB, selector);
        var configPath = Path.Combine(Path.GetTempPath(), $"xray-test-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(configPath, configJson);

        try
        {
            _xrayProcess = StartXray(configPath);
            Log($"[TEST] xray-core PID={_xrayProcess.Id}");

            await WaitForSocksReadyAsync();
            Log("[TEST] xray-core SOCKS5 ready");

            const int N = 1000;
            await SendRequestsAsync(N);
            Log($"[TEST] Sent {N} requests");

            int countA = _observerA.Count;
            int countB = _observerB.Count;
            Log($"[TEST] Observer A={countA}, B={countB}, total={countA + countB}");

            if (countA + countB < N * 0.9)
            {
                Assert.Fail(
                    $"Too few connections reached observers ({countA + countB}/{N}).");
            }

            return (countA, countB);
        }
        finally
        {
            try { File.Delete(configPath); } catch { /* best-effort */ }
        }
    }

    // ── Config builder ────────────────────────────────────────

    private string BuildConfig(int redirectPortA, int redirectPortB, string[]? selector = null)
    {
        var config = new
        {
            log = new { loglevel = "warning" },
            inbounds = new[]
            {
                new
                {
                    tag = "socks-in",
                    listen = "127.0.0.1",
                    port = _socksPort,
                    protocol = "socks",
                    settings = new { auth = "noauth", udp = false }
                }
            },
            outbounds = new object[]
            {
                new
                {
                    tag = "A",
                    protocol = "freedom",
                    settings = new { redirect = $"127.0.0.1:{redirectPortA}" }
                },
                new
                {
                    tag = "B",
                    protocol = "freedom",
                    settings = new { redirect = $"127.0.0.1:{redirectPortB}" }
                }
            },
            routing = new
            {
                rules = new[]
                {
                    new
                    {
                        type = "field",
                        network = "tcp",
                        balancerTag = "proxy-round"
                    }
                },
                balancers = new[]
                {
                    new
                    {
                        tag = "proxy-round",
                        selector = selector ?? new[] { "A", "A", "A", "B" },
                        strategy = new { type = "random" }
                    }
                }
            }
        };

        return JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
    }

    // ── Process management ────────────────────────────────────

    private Process StartXray(string configPath)
    {
        var xrayPath = FindXrayCorePath();
        Log($"[TEST] xray binary: {xrayPath}");

        var psi = new ProcessStartInfo
        {
            FileName = xrayPath,
            Arguments = $"run -c \"{configPath}\"",
            UseShellExecute = false,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        var process = new Process { StartInfo = psi };
        process.Start();

        // Drain stderr
        _ = Task.Run(() =>
        {
            while (!process.HasExited)
            {
                try
                {
                    var line = process.StandardError.ReadLine();
                    if (line != null) Log($"[xray] {line}");
                }
                catch { break; }
            }
        });

        return process;
    }

    private async Task WaitForSocksReadyAsync(int timeoutMs = 15_000)
    {
        using var cts = new CancellationTokenSource(timeoutMs);

        while (!cts.IsCancellationRequested)
        {
            try
            {
                var ok = await TrySocks5ProbeAsync(cts.Token);
                if (ok) return;
            }
            catch { }

            await Task.Delay(500, cts.Token);
        }

        throw new TimeoutException("xray SOCKS5 not ready");
    }

    private async Task<bool> TrySocks5ProbeAsync(CancellationToken ct)
    {
        using var sock = new Socket(SocketType.Stream, ProtocolType.Tcp);
        await sock.ConnectAsync(new DnsEndPoint("127.0.0.1", _socksPort), ct);
        using var ns = new NetworkStream(sock, ownsSocket: false);

        // Auth negotiation
        await ns.WriteAsync(new byte[] { 0x05, 0x01, 0x00 }, ct);
        var authResp = new byte[2];
        await ns.ReadExactlyAsync(authResp, ct);
        return authResp[0] == 0x05 && authResp[1] == 0x00;
    }

    // ── Request sender ────────────────────────────────────────

    private async Task SendRequestsAsync(int N)
    {
        // Each request gets a fresh TCP connection → fresh balancer selection.
        // PooledConnectionLifetime = Zero ensures no connection reuse.
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.Zero,
            MaxConnectionsPerServer = int.MaxValue,
            ConnectCallback = async (ctx, ct) =>
                await Socks5ConnectAsync(ctx.DnsEndPoint, ct),
        };

        using var httpClient = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(10),
        };

        // Send requests to any host — freedom redirect overrides the destination.
        // Using example.com as the nominal target; the outbound redirect sends
        // everything to the observer server regardless.
        var parallelOptions = new ParallelOptions { MaxDegreeOfParallelism = 25 };

        await Parallel.ForEachAsync(
            Enumerable.Range(0, N),
            parallelOptions,
            async (_, ct) =>
            {
                try
                {
                    var request = new HttpRequestMessage(HttpMethod.Get,
                        "http://redirect.example/");
                    request.Headers.ConnectionClose = true;

                    using var resp = await httpClient.SendAsync(request, ct);
                    // Drain response body
                    await resp.Content.ReadAsByteArrayAsync(ct);
                }
                catch
                {
                    // Individual failures are tolerated;
                    // the assertion on total count catches systemic issues.
                }
            });
    }

    private async ValueTask<Stream> Socks5ConnectAsync(DnsEndPoint target, CancellationToken ct)
    {
        var sock = new Socket(SocketType.Stream, ProtocolType.Tcp);
        await sock.ConnectAsync(new DnsEndPoint("127.0.0.1", _socksPort), ct);
        var ns = new NetworkStream(sock, ownsSocket: false);

        // Auth
        await ns.WriteAsync(new byte[] { 0x05, 0x01, 0x00 }, ct);
        var authResp = new byte[2];
        await ns.ReadExactlyAsync(authResp, ct);
        if (authResp[0] != 0x05 || authResp[1] != 0x00)
            throw new InvalidOperationException($"SOCKS5 auth rejected: {authResp[1]}");

        // CONNECT
        var hostBytes = IPAddress.Parse(target.Host).GetAddressBytes();
        var port = (ushort)target.Port;
        var cmd = new byte[10];
        cmd[0] = 0x05; cmd[1] = 0x01; cmd[2] = 0x00; cmd[3] = 0x01;
        Array.Copy(hostBytes, 0, cmd, 4, 4);
        cmd[8] = (byte)(port >> 8); cmd[9] = (byte)(port & 0xFF);

        await ns.WriteAsync(cmd, ct);
        var connResp = new byte[10];
        await ns.ReadExactlyAsync(connResp, ct);
        if (connResp[1] != 0x00)
            throw new InvalidOperationException($"SOCKS5 CONNECT rejected: {connResp[1]}");

        return ns;
    }

    // ── xray binary discovery ─────────────────────────────────

    private static string FindXrayCorePath()
    {
        var envPath = Environment.GetEnvironmentVariable("XRAY_CORE_PATH");
        if (!string.IsNullOrEmpty(envPath) && File.Exists(envPath))
            return envPath;

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            foreach (var candidate in new[]
            {
                Path.Combine(dir.FullName, "v2rayN", "v2rayN", "bin"),
                Path.Combine(dir.FullName, "v2rayN", "bin"),
            })
            {
                if (!Directory.Exists(candidate)) continue;
                var found = FindXrayInDir(candidate);
                if (found != null) return found;
            }
            dir = dir.Parent;
        }

        throw new FileNotFoundException(
            "Cannot find xray-core. Set XRAY_CORE_PATH environment variable.");
    }

    private static string? FindXrayInDir(string binDir)
    {
        foreach (var netDir in Directory.GetDirectories(binDir, "net*", SearchOption.TopDirectoryOnly))
        {
            var path = Path.Combine(netDir, "bin", "xray", "xray.exe");
            if (File.Exists(path)) return path;
        }
        return null;
    }

    // ── Utilities ─────────────────────────────────────────────

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private void Log(string msg) => _output.WriteLine(msg);

    // ── Cleanup ───────────────────────────────────────────────

    public async ValueTask DisposeAsync()
    {
        if (_xrayProcess is { HasExited: false })
        {
            try { _xrayProcess.Kill(entireProcessTree: true); } catch { }
            _xrayProcess.Dispose();
        }

        _observerA?.Dispose();
        _observerB?.Dispose();

        await Task.CompletedTask;
    }

    // ── Embedded observer: HTTP server that counts connections ─

    private sealed class ObserverServer : IDisposable
    {
        private readonly HttpListener _listener;
        private int _count;

        public int Count => _count;

        public ObserverServer(int port)
        {
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        }

        public void Start()
        {
            _listener.Start();
            _ = AcceptLoopAsync();
        }

        private async Task AcceptLoopAsync()
        {
            while (_listener.IsListening)
            {
                try
                {
                    var ctx = await _listener.GetContextAsync();
                    Interlocked.Increment(ref _count);
                    ctx.Response.StatusCode = 200;
                    ctx.Response.ContentType = "text/plain";
                    ctx.Response.ContentLength64 = 2;
                    using var writer = new StreamWriter(ctx.Response.OutputStream);
                    writer.Write("OK");
                }
                catch (HttpListenerException) { break; }
                catch (ObjectDisposedException) { break; }
            }
        }

        public void Dispose()
        {
            try { _listener.Stop(); } catch { }
            try { _listener.Close(); } catch { }
        }
    }
}
