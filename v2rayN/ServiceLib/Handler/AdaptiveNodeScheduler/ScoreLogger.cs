using System.Text.Json;

namespace ServiceLib.Handler.AdaptiveNodeScheduler;

/// <summary>
/// JSONL telemetry logger. Writes structured events to <c>guiLogs/adaptive.log</c>.
/// Each line is a self-contained JSON object with at least "time" and "type" fields.
///
/// Event types:
///   score_snapshot    — periodic per-node score dump (every 30s)
///   active_set_change — top-K sticky set changed (added/removed)
///   xray_reload       — xray config reload triggered
/// </summary>
public class ScoreLogger
{
    private readonly string _logPath;
    private readonly IReadOnlyList<NodeState> _nodes;
    private readonly object _writeLock = new();
    private CancellationTokenSource? _cts;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public ScoreLogger(IReadOnlyList<NodeState> nodes)
        : this(nodes, Utils.GetLogPath("adaptive.log"))
    {
    }

    /// <summary>
    /// Constructor with explicit log path — useful for tests that want to
    /// verify the telemetry output without relying on the production path.
    /// </summary>
    public ScoreLogger(IReadOnlyList<NodeState> nodes, string logPath)
    {
        _nodes = nodes;
        _logPath = logPath;
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

    /// <summary>
    /// Write an arbitrary event as a JSONL line.
    /// The caller provides event-specific fields; "time" and "type" are added automatically.
    /// </summary>
    public void LogEvent(string type, IReadOnlyDictionary<string, object?> data)
    {
        var entry = new Dictionary<string, object?>(data)
        {
            ["time"] = DateTime.UtcNow.ToString("o"),
            ["type"] = type,
        };
        WriteLine(JsonSerializer.Serialize(entry, JsonOptions));
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(30_000, ct).ConfigureAwait(false);
            if (ct.IsCancellationRequested) break;

            try
            {
                foreach (var s in _nodes.Select(n => n.Snapshot()).OrderByDescending(s => s.Score))
                {
                    var entry = new Dictionary<string, object?>
                    {
                        ["time"] = DateTime.UtcNow.ToString("o"),
                        ["type"] = "score_snapshot",
                        ["node"] = s.Tag,
                        ["score"] = s.Score.ToString("F1"),
                        ["latency_ms"] = s.LatencyMs.ToString("F0"),
                        ["loss_rate"] = s.LossRate.ToString("F3"),
                        ["in_cooldown"] = s.InCooldown,
                    };
                    WriteLine(JsonSerializer.Serialize(entry, JsonOptions));
                }
            }
            catch
            {
                // ignore logging errors
            }
        }
    }

    private void WriteLine(string line)
    {
        lock (_writeLock)
        {
            try
            {
                File.AppendAllText(_logPath, line + "\n");
            }
            catch
            {
                // best-effort telemetry — never crash on log write failure
            }
        }
    }
}
