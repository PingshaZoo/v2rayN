using System.Collections.Concurrent;

namespace ServiceLib.Handler.CloudflareBestIP;

/// <summary>
/// 批量并发探测调度器 / Batch concurrent probing scheduler
///
/// 两阶段调度 / Two-phase scheduling:
///   Phase A — 延迟探测并发（SemaphoreSlim 控制并发数）
///   Phase B — 速度测试串行（逐个 IP 顺序执行，避免并发下载抢带宽）
///
/// Phase A — latency probing concurrent (SemaphoreSlim controls concurrency)
/// Phase B — speed test sequential (one IP at a time, avoids bandwidth competition)
///
/// 并发策略 / Concurrency strategy:
///   Windows: 20 concurrent | Linux/Mac: 10 concurrent
/// </summary>
public class CfIpBatchProber
{
    private readonly CfBestIpItem _config;
    private readonly CfIpProber _prober;
    private int _doneCount;
    private int _failCount;

    public CfIpBatchProber(CfBestIpItem config)
    {
        _config = config;
        _prober = new CfIpProber(config);
    }

    /// <summary>
    /// 对 IP 列表执行：并发延迟探测 → 串行速度测试
    /// Concurrent latency probing for all IPs → sequential speed testing for successful IPs
    /// </summary>
    /// <param name="ips">待探测 IP 列表 / IP list to probe</param>
    /// <param name="onProgress">进度回调（线程安全）/ progress callback (thread-safe)</param>
    /// <param name="speedPassStop">测速早停阈值：达标IP数>=此值时停止后续测速，0=不早停</param>
    /// <returns>所有成功探测结果（含延迟+速度数据）</returns>
    public async Task<List<CfProbeResult>> ProbeAllAsync(
        List<string> ips, Action<string>? onProgress = null, int speedPassStop = 0)
    {
        // ═══════════════════════════════════════════════════════════════
        // Phase A: 并发延迟探测（不测速，不抢带宽）
        // Phase A: concurrent latency probing (no speed test, no bandwidth competition)
        // ═══════════════════════════════════════════════════════════════
        var latencyResults = new ConcurrentBag<CfProbeResult>();
        _doneCount = 0;
        _failCount = 0;

        var concurrency = Utils.IsWindows() ? 20 : 10;
        var semaphore = new SemaphoreSlim(concurrency);

        var tasks = ips.Select(async ip =>
        {
            await semaphore.WaitAsync();
            try
            {
                var result = await _prober.ProbeLatencyOnlyAsync(ip);
                if (result != null)
                {
                    latencyResults.Add(result);
                    Interlocked.Increment(ref _doneCount);
                }
                else
                {
                    Interlocked.Increment(ref _failCount);
                }

                var total = Interlocked.Add(ref _doneCount, 0) + Interlocked.Add(ref _failCount, 0);
                if (total % _config.ProgInterval == 0)
                {
                    onProgress?.Invoke(
                        $"Progress: {total}/{ips.Count} | ok={_doneCount} fail={_failCount}");
                }

                // 打印每个成功 IP 的延迟详情 / Print latency detail for every successful IP
                if (result != null)
                {
                    onProgress?.Invoke(
                        $"  OK  {result.Ip} | colo={result.Colo ?? "?"} | latency={result.AvgLatencyMs:F1}ms");
                }
            }
            catch
            {
                Interlocked.Increment(ref _failCount);
            }
            finally
            {
                semaphore.Release();
            }
        });

        await Task.WhenAll(tasks);
        onProgress?.Invoke(
            $"Latency probing complete: {_doneCount} success, {_failCount} fail (total: {ips.Count})");

        var results = latencyResults.ToList();
        if (results.Count == 0) return results;

        // ═══════════════════════════════════════════════════════════════
        // Phase B: 串行速度测试（一次只测一个 IP，避免并发下载抢带宽）
        // Phase B: sequential speed test (one at a time, avoids bandwidth competition)
        // ═══════════════════════════════════════════════════════════════
        var speedPath = _config.OriginSpeedTestPath;
        if (speedPath.IsNotEmpty())
        {
            onProgress?.Invoke($"Starting sequential speed tests for {results.Count} IPs...");

            var speedPassed = 0;
            var lowestSpeed = _config.LowestSpeed;
            for (var i = 0; i < results.Count; i++)
            {
                var r = results[i];
                var speed = await _prober.ProbeSpeedOnlyAsync(r.Ip, r.Colo);
                r.DownloadSpeedKBs = speed;
                if (speed >= lowestSpeed)
                    speedPassed++;

                onProgress?.Invoke(
                    $"  Speed [{i + 1}/{results.Count}] {r.Ip} | colo={r.Colo ?? "?"} | latency={r.AvgLatencyMs:F1}ms | speed={speed:F0}KB/s");

                if (speedPassStop > 0 && speedPassed >= speedPassStop)
                {
                    onProgress?.Invoke(
                        $"  Batch early stop: {speedPassed} pass in this batch, reached stop target ({speedPassStop})");
                    break;
                }

                if (i < results.Count - 1)
                    await Task.Delay(1000);
            }

            onProgress?.Invoke(
                speedPassStop > 0
                    ? $"  Batch speed done: {speedPassed} pass in this batch (need {speedPassStop}), continuing..."
                    : $"  Batch speed done: {speedPassed}/{results.Count} pass (>= {lowestSpeed}KB/s)");
        }

        return results;
    }
}
