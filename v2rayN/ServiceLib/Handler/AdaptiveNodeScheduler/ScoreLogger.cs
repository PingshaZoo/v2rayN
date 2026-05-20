namespace ServiceLib.Handler.AdaptiveNodeScheduler;

public class ScoreLogger
{
    private readonly IReadOnlyList<NodeState> _nodes;
    private readonly Action<string> _logAction;
    private CancellationTokenSource? _cts;

    public ScoreLogger(IReadOnlyList<NodeState> nodes, Action<string> logAction)
    {
        _nodes = nodes;
        _logAction = logAction;
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _ = RunAsync(_cts.Token);
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(30_000, ct).ConfigureAwait(false);
            if (ct.IsCancellationRequested) break;

            try
            {
                var snapshots = _nodes
                    .Select(n => n.Snapshot())
                    .OrderByDescending(s => s.Score);

                foreach (var s in snapshots)
                {
                    _logAction($"Node {s.Tag}: score={s.Score:F1} lat={s.LatencyMs:F0}ms " +
                               $"loss={s.LossRate:P1} cooldown={s.InCooldown}");
                }
            }
            catch
            {
                // ignore logging errors
            }
        }
    }
}
