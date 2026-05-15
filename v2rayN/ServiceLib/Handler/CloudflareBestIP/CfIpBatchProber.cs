using System.Collections.Concurrent;

namespace ServiceLib.Handler.CloudflareBestIP;

public class CfIpBatchProber
{
    private readonly CfBestIpItem _config;
    private readonly CfIpProber _prober;
    private int _doneCount;
    private int _failCount;
    private readonly object _lock = new();

    public CfIpBatchProber(CfBestIpItem config)
    {
        _config = config;
        _prober = new CfIpProber(config);
    }

    public async Task<List<CfProbeResult>> ProbeAllAsync(
        List<string> ips, Action<string>? onProgress = null)
    {
        var results = new ConcurrentBag<CfProbeResult>();
        _doneCount = 0;
        _failCount = 0;

        var concurrency = Utils.IsWindows() ? 20 : 10;
        var semaphore = new SemaphoreSlim(concurrency);

        var tasks = ips.Select(async ip =>
        {
            await semaphore.WaitAsync();
            try
            {
                var result = await _prober.ProbeSingleIpAsync(ip);
                if (result != null)
                {
                    results.Add(result);
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

                // Per-IP detail: log first few successful and failed probes
                if (result != null && _doneCount <= 5)
                {
                    onProgress?.Invoke(
                        $"  OK  {result.Ip} | colo={result.Colo ?? "?"} | latency={result.AvgLatencyMs:F1}ms | speed={result.DownloadSpeedKBs:F0}KB/s");
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
            $"Probing complete: {_doneCount} success, {_failCount} fail (total: {ips.Count})");

        return results.ToList();
    }
}
