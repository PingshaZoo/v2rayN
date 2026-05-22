namespace ServiceLib.Handler.AdaptiveNodeScheduler;

/// <summary>
/// P3.1: Zero-downtime policy applier that uses xray's HandlerService gRPC API
/// to add/remove outbounds dynamically, avoiding the ~1.1s restart penalty
/// of <see cref="ReloadPolicyApplier"/>.
///
/// <h2>Strategy</h2>
/// The initial xray config must include a balancer with a prefix selector
/// (e.g. "proxy-") and all possible outbounds. When the active set changes,
/// this applier diffs the old and new active tags:
///   - Outbounds for tags leaving the active set are REMOVED via the API.
///   - Outbounds for tags entering the active set are ADDED via the API.
/// The balancer's prefix selector automatically picks up only the currently
/// present outbounds — no balancer reconfiguration needed.
///
/// <h2>Fallback</h2>
/// If <see cref="IXrayHandlerClient.IsAvailableAsync"/> returns false,
/// the call is delegated to the <paramref name="fallback"/> applier
/// (typically <see cref="ReloadPolicyApplier"/>), which does a full config reload.
///
/// <h2>API dependency</h2>
/// xray's HandlerService API availability depends on the xray version and
/// whether the API port is configured. When unavailable (the common case today),
/// this class is a transparent pass-through to the fallback. The architecture
/// is ready for the day xray exposes a stable dynamic outbound API.
/// </summary>
public sealed class RuntimePolicyApplier : IAdaptivePolicyApplier
{
    private readonly IXrayHandlerClient _client;
    private readonly IAdaptivePolicyApplier _fallback;
    private HashSet<string> _currentActiveTags = new(StringComparer.Ordinal);
    private bool _disposed;

    public RuntimePolicyApplier(IXrayHandlerClient client, IAdaptivePolicyApplier fallback)
    {
        _client = client;
        _fallback = fallback;
    }

    public async Task ApplyAsync(AdaptiveConfig config, CancellationToken ct = default)
    {
        if (_disposed) return;

        if (!await _client.IsAvailableAsync().ConfigureAwait(false))
        {
            await _fallback.ApplyAsync(config, ct).ConfigureAwait(false);
            return;
        }

        var newActive = new HashSet<string>(config.ActiveTags, StringComparer.Ordinal);
        var added = newActive.Except(_currentActiveTags).ToList();
        var removed = _currentActiveTags.Except(newActive).ToList();

        foreach (var tag in removed)
        {
            if (ct.IsCancellationRequested) break;
            await _client.RemoveOutboundAsync(tag, ct).ConfigureAwait(false);
        }

        foreach (var tag in added)
        {
            if (ct.IsCancellationRequested) break;
            // Host/port are not in AdaptiveConfig — the initial config must
            // include ALL possible outbounds. Adding here means re-adding
            // an outbound that was previously removed (the config template
            // has it). We pass dummy host/port since the API re-adds from
            // the stored template, or the caller can enrich AdaptiveConfig later.
            await _client.AddOutboundAsync(tag, "", 0, ct).ConfigureAwait(false);
        }

        _currentActiveTags = newActive;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        try { await _fallback.DisposeAsync(); } catch { /* best-effort */ }
    }
}
