using System.Net.Sockets;

namespace ServiceLib.Handler.AdaptiveNodeScheduler;

public sealed class BootstrapProber
{
    private const int TcpTimeoutMs = 2000;
    private const int GlobalTimeoutMs = 3000;

    public async Task InitializeAsync(IReadOnlyList<NodeState> nodes,
                                      ScoreCalculator scorer)
    {
        using var cts = new CancellationTokenSource(GlobalTimeoutMs);

        var tasks = nodes
            .Where(n => n.Protocol == ProxyProtocol.Tcp)
            .Select(n => ProbeOneAsync(n, scorer, cts.Token));

        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private static async Task ProbeOneAsync(NodeState node,
                                            ScoreCalculator scorer,
                                            CancellationToken ct)
    {
        long t0 = Stopwatch.GetTimestamp();
        try
        {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(node.Host, node.Port, ct).ConfigureAwait(false);
            double latencyMs = ElapsedMs(t0);

            double score = scorer.Compute(latencyMs, 0.0);
            node.UpdateScore(latencyMs, 0.0, score, 0);
        }
        catch (OperationCanceledException)
        {
            node.UpdateScore(5000, 1.0, 1.0, 0);
        }
        catch
        {
            node.UpdateScore(5000, 1.0, 1.0, 0);
        }
    }

    private static double ElapsedMs(long t0) =>
        (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
}
