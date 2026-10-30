namespace ServiceLib.Handler.CloudflareBestIP;

/// <summary>One ordered stream for the application's file log and the probe window.</summary>
public sealed class CfProbeLog(Action<string>? onLog = null, Action<string>? writeFile = null)
{
    private readonly object _sync = new();
    private readonly Action<string> _writeFile = writeFile ?? Logging.SaveLog;
    private readonly string _runId = Guid.NewGuid().ToString("N")[..8];

    public void Write(string message)
    {
        lock (_sync)
        {
            var line = $"[{DateTime.Now:HH:mm:ss.fff}] [CloudflareBestIp] [{_runId}] {message.Replace('\r', ' ').Replace('\n', ' ')}";
            _writeFile(line);
            onLog?.Invoke(line);
        }
    }
}
