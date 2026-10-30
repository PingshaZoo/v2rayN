namespace ServiceLib.Handler.CloudflareBestIP;

public class CfIpBatchProber(CfBestIpItem config, CfIpProber? prober = null, Action<string>? onLog = null)
{
    private readonly CfIpProber _prober = prober ?? new CfIpProber(config, onLog);

    public async Task<List<CfProbeResult>> ProbeLatencyAsync(IReadOnlyList<CfIpSource> sources,
        Action<int, int>? onProgress = null, CancellationToken cancellationToken = default)
    {
        using var slots = new SemaphoreSlim(Utils.IsWindows() ? 20 : 10);
        var output = new CfProbeResult?[sources.Count];
        var completed = 0;
        var tasks = sources.Select(async (source, index) =>
        {
            await slots.WaitAsync(cancellationToken);
            try
            {
                onLog?.Invoke(CfUi.T($"开始延迟探测：{source.Ip}", $"Latency probe started: {source.Ip}"));
                var result = await _prober.ProbeLatencyOnlyAsync(source.Ip, cancellationToken: cancellationToken);
                if (result is not null)
                {
                    result.Source = source.SourceUrl;
                    output[index] = result;
                    onLog?.Invoke($"{source.Ip} SNI={result.Sni} colo={result.Colo}: latency={result.AvgLatencyMs:F1} ms, TCP/TLS/HTTP loss={result.TcpLossRate:P0}/{result.TlsLossRate:P0}/{result.HttpLossRate:P0}");
                }
                else onLog?.Invoke(CfUi.T($"{source.Ip}：无有效延迟响应", $"{source.Ip}: no valid latency response"));
            }
            finally
            {
                slots.Release();
                onProgress?.Invoke(Interlocked.Increment(ref completed), sources.Count);
            }
        });
        await Task.WhenAll(tasks);
        return output.OfType<CfProbeResult>().ToList();
    }

    public async Task<int> ProbeSpeedAsync(IEnumerable<CfProbeResult> candidates, int stopAfter,
        Action<int, int>? onProgress = null, CancellationToken cancellationToken = default)
    {
        var list = candidates.Where(CfIpScorer.HasReliableLatency).DistinctBy(r => r.Ip).ToList();
        var passed = 0;
        for (var i = 0; i < list.Count && passed < stopAfter; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = list[i];
            if (result.SpeedTested) continue;
            onLog?.Invoke(CfUi.T($"开始下载测速：{result.Ip} SNI={result.Sni}", $"Download probe started: {result.Ip} SNI={result.Sni}"));
            result.DownloadSpeedKBs = await _prober.ProbeSpeedOnlyAsync(result.Ip, result.Colo, result.Port,
                cancellationToken, result.Sni);
            result.SpeedTested = true;
            result.Qualified = CfIpScorer.IsQualified(result, config);
            onLog?.Invoke($"{result.Ip} SNI={result.Sni}: {result.DownloadSpeedKBs:F1} KB/s, minimum={config.LowestSpeed} KB/s, {(result.Qualified ? CfUi.Qualified : CfUi.NotQualified)}");
            if (result.Qualified) passed++;
            onProgress?.Invoke(i + 1, list.Count);
            if (i < list.Count - 1 && passed < stopAfter && config.SleepInterval > 0)
                await Task.Delay(TimeSpan.FromSeconds(config.SleepInterval), cancellationToken);
        }
        return passed;
    }
}
