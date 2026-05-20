namespace ServiceLib.Handler.AdaptiveNodeScheduler;

/// <summary>
/// Phase 1 fallback: applies adaptive policy by regenerating and reloading xray-core config.
///
/// Uses <b>trailing debounce</b>: if changes arrive within the reload budget window,
/// the latest config is saved and applied after the window expires — no update is dropped.
///
/// The long-term goal is a <c>RuntimePolicyApplier</c> that updates the balancer/routing
/// via xray runtime APIs, eliminating restarts entirely. When that API becomes available,
/// swap the implementation without touching the scheduler or config generator.
/// </summary>
public sealed class ReloadPolicyApplier : IAdaptivePolicyApplier
{
    private readonly Func<AdaptiveConfig, Task> _reloadFunc;
    private readonly object _lock = new();
    private DateTime _lastReload = DateTime.MinValue;
    private AdaptiveConfig? _pendingConfig;
    private CancellationTokenSource? _debounceCts;
    private bool _disposed;
    private static readonly TimeSpan MinReloadInterval = TimeSpan.FromSeconds(30);

    public ReloadPolicyApplier(Func<AdaptiveConfig, Task> reloadFunc)
    {
        _reloadFunc = reloadFunc;
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        lock (_lock)
        {
            if (_disposed) return ValueTask.CompletedTask;
            _disposed = true;
            _pendingConfig = null;
            _debounceCts?.Cancel();
            _debounceCts?.Dispose();
            _debounceCts = null;
        }
        return ValueTask.CompletedTask;
    }

    public async Task ApplyAsync(AdaptiveConfig config, CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (_disposed) return;

            var elapsed = DateTime.UtcNow - _lastReload;
            if (elapsed >= MinReloadInterval)
            {
                _lastReload = DateTime.UtcNow;
                _pendingConfig = null;
            }
            else
            {
                // Save latest config; a delayed reload is already scheduled
                _pendingConfig = config;
                if (_debounceCts != null) return; // timer already running
                ScheduleLocked(MinReloadInterval - elapsed);
                return;
            }
        }

        await _reloadFunc(config);
    }

    private void ScheduleLocked(TimeSpan delay)
    {
        if (_disposed) return;

        _debounceCts?.Cancel();
        _debounceCts?.Dispose();
        _debounceCts = new CancellationTokenSource();
        var ct = _debounceCts.Token;
        _ = ApplyAfterDelayAsync(delay, ct);
    }

    private async Task ApplyAfterDelayAsync(TimeSpan delay, CancellationToken ct)
    {
        try
        {
            await Task.Delay(delay, ct).ConfigureAwait(false);

            AdaptiveConfig? pending;
            lock (_lock)
            {
                pending = _pendingConfig;
                _pendingConfig = null;
                _debounceCts?.Dispose();
                _debounceCts = null;

                if (pending == null)
                    return;

                _lastReload = DateTime.UtcNow;
            }

            await _reloadFunc(pending);
        }
        catch (OperationCanceledException)
        {
            // cancelled by a newer debounce — expected
        }
    }
}
