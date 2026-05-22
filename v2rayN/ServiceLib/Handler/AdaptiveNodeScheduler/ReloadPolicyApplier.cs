namespace ServiceLib.Handler.AdaptiveNodeScheduler;

/// <summary>
/// Phase 1 fallback: applies adaptive policy by regenerating and reloading xray-core config.
///
/// Uses <b>trailing debounce</b>: if changes arrive within the reload budget window,
/// the latest config is saved and applied after the window expires — no update is dropped.
///
/// <h2>Reload Budget (P1 stability fix)</h2>
/// A sliding one-hour window counts actual reloads. The debounce interval escalates
/// with reload frequency to prevent reload storms:
///   ≤6 reloads/hour → 15s debounce (normal)
///   7–10 reloads/hour → 60s debounce (extended)
///   >10 reloads/hour → 120s debounce (throttled)
/// The budget is a <b>throttle, not a deny</b> — critical changes always
/// propagate, just with increasing delays. No hard freeze.
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

    // ── Reload budget ──────────────────────────────────────
    private static readonly TimeSpan BudgetWindow = TimeSpan.FromHours(1);
    private const int NormalReloadLimit = 6;     // ≤6/hr → normal debounce
    private const int ExtendedReloadLimit = 10;  // 7-10/hr → extended debounce
    private static readonly TimeSpan NormalInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ExtendedInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ThrottledInterval = TimeSpan.FromSeconds(120);
    private readonly List<DateTime> _reloadTimestamps = [];

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
            _reloadTimestamps.Clear();
        }
        return ValueTask.CompletedTask;
    }

    public async Task ApplyAsync(AdaptiveConfig config, CancellationToken ct = default)
    {
        var effectiveInterval = GetBudgetAdjustedInterval();

        lock (_lock)
        {
            if (_disposed) return;

            var elapsed = DateTime.UtcNow - _lastReload;
            if (elapsed >= effectiveInterval)
            {
                _lastReload = DateTime.UtcNow;
                _pendingConfig = null;
            }
            else
            {
                // Save latest config; a delayed reload is already scheduled
                _pendingConfig = config;
                if (_debounceCts != null) return; // timer already running
                ScheduleLocked(effectiveInterval - elapsed);
                return;
            }
        }

        await _reloadFunc(config);
        RecordReload();
    }

    /// <summary>
    /// Returns the debounce interval based on the number of reloads
    /// in the last hour. Escalates from 15s → 60s → 120s.
    /// Never returns infinite — budget is throttle, not deny.
    /// </summary>
    private TimeSpan GetBudgetAdjustedInterval()
    {
        lock (_lock)
        {
            PruneReloadTimestamps();
            int count = _reloadTimestamps.Count;
            if (count <= NormalReloadLimit)
                return NormalInterval;
            if (count <= ExtendedReloadLimit)
                return ExtendedInterval;
            return ThrottledInterval;
        }
    }

    private void RecordReload()
    {
        lock (_lock)
        {
            _reloadTimestamps.Add(DateTime.UtcNow);
            PruneReloadTimestamps();
        }
    }

    private void PruneReloadTimestamps()
    {
        var cutoff = DateTime.UtcNow - BudgetWindow;
        _reloadTimestamps.RemoveAll(t => t < cutoff);
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
            RecordReload();
        }
        catch (OperationCanceledException)
        {
            // cancelled by a newer debounce — expected
        }
    }
}
