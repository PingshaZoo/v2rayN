using ServiceLib.Common;
using ServiceLib.Models.Entities;
using ServiceLib.Models.Configs;
using ServiceLib.Models.CoreConfigs;

namespace ServiceLib.Handler.AdaptiveNodeScheduler;

/// <summary>
/// Control Plane orchestrator for adaptive node scheduling.
/// Maintains the QoS Engine (scoring, cooldown, probing) and delegates policy
/// application to an <see cref="IAdaptivePolicyApplier"/>.
///
/// <h2>Lifecycle</h2>
/// A full scheduling cycle goes through these phases:
///   <b>Phase 1 — Init</b>: <see cref="InitializeNodes"/> — sync, builds node states + allocates probe ports.
///      Returns initial AdaptiveConfig so the first LoadCore includes probe inbounds.
///   <b>Phase 2a — Bootstrap</b>: <see cref="BootstrapAsync"/> — async, restores persisted scores
///      then runs parallel TCP-connect probes to seed initial scores. Call BEFORE LoadCore.
///   <b>Phase 2b — Runtime</b>: <see cref="StartProbesAsync"/> — async, starts ProbeService + ScoreLogger
///      + active-set monitor loop. Call AFTER LoadCore since probes go through xray SOCKS5 inbounds.
///   <b>Shutdown</b>: <see cref="StopAsync"/> or <see cref="IAsyncDisposable.DisposeAsync"/> —
///      cancels monitor loop, disposes ProbeService/ScoreLogger/policyApplier, clears state.
///
/// <h2>Profile / Group Switching</h2>
/// When the user switches to a different node group that has adaptive enabled:
///   1. The caller must stop the current adaptive session (<see cref="StopAsync"/>)
///   2. Re-initialize with <see cref="InitializeNodes"/> + <see cref="BootstrapAsync"/> + <see cref="StartProbesAsync"/>
///   3. After <see cref="StopAsync"/>, <see cref="IsRunning"/> is false and all internal
///      state (_nodes, _probePorts, _tagToIndexId) is cleared — ready for a fresh start.
///
/// <h2>Singleton</h2>
/// This class uses a static <see cref="Lazy{T}"/> singleton (<see cref="Instance"/>).
/// <see cref="StopAsync"/> fully resets state, so re-initialization is safe without
/// creating a new instance. DI would be cleaner but is not available in this codebase;
/// the singleton is the pragmatic choice.
///
/// <h2>Emergency Bypass</h2>
/// <see cref="EmergencyDisableAdaptiveAsync"/> provides a one-click escape hatch:
/// sets Enabled=false, calls StopAsync, and notifies the caller to restore default config.
///
/// When the active set changes meaningfully, <see cref="OnActiveSetChangedAsync"/>
/// builds a new AdaptiveConfig (including current scores) and calls the policy
/// applier. The current <see cref="ReloadPolicyApplier"/> is a <b>Phase 1 fallback</b>
/// that regenerates xray config with trailing debounce. The design goal is a
/// <c>RuntimePolicyApplier</c> that avoids restarts once xray-core provides an API
/// for dynamic balancer/routing updates.
/// </summary>
public sealed class AdaptiveSchedulerManager : IAsyncDisposable
{
    private static readonly Lazy<AdaptiveSchedulerManager> _instance = new(() => new());
    public static AdaptiveSchedulerManager Instance => _instance.Value;

    private readonly ScoreCalculator _scorer = new();
    private readonly CooldownFsm _cooldown = new();
    private readonly BootstrapProber _bootstrapper = new();
    private readonly IClock _clock = new SystemClock();
    private readonly RecoveryConfirmationFsm _recoveryFsm;
    private readonly GlobalFreezeController _freezeController;
    private readonly DnsCacheManager _dnsCache;

    private FailureCollector? _collector;
    private ProbeService? _probeService;
    private ScoreLogger? _scoreLogger;
    private ActiveSetManager? _activeSetManager;
    private IAdaptivePolicyApplier? _policyApplier;
    private List<NodeState> _nodes = [];
    private Dictionary<string, int> _probePorts = new(StringComparer.Ordinal);
    private Dictionary<string, string> _tagToIndexId = new(StringComparer.Ordinal);
    private CancellationTokenSource? _monitorCts;
    private bool _isRunning;
    private bool _nodesInitialized;

    private Func<bool, string, Task>? _updateFunc;
    private AdaptiveSchedulerItem? _adaptiveItem;
    private ProtocolExtraItem? _groupAdaptiveSettings;
    /// <summary>v7.6 ReloadCooldown hard floor — minimum interval between xray reloads (§5.1.5).</summary>
    public const int ReloadCooldownMs = 60_000;
    private const string _tag = "AdaptiveScheduler";

    private AdaptiveSchedulerManager()
    {
        _recoveryFsm = new RecoveryConfirmationFsm(_clock);
        _freezeController = new GlobalFreezeController(_clock);
        _dnsCache = new DnsCacheManager(_clock);
        _freezeController.EmergencyDisableRequested += OnFreezeEscalation;
    }

    private void OnFreezeEscalation(string reason)
    {
        _ = HandleFreezeEscalationAsync(reason);
    }

    private async Task HandleFreezeEscalationAsync(string reason)
    {
        await _updateFunc!(false,
            $"[{_tag}] FREEZE_COOLDOWN escalation: {reason}. Triggering EmergencyDisableAdaptive...");
        await EmergencyDisableAdaptiveAsync();
    }

    // ── Public API ──────────────────────────────────────────

    public bool IsRunning => _isRunning;
    public IReadOnlyList<NodeState> Nodes => _nodes.AsReadOnly();
    public IReadOnlyDictionary<string, int> ProbePorts => _probePorts.AsReadOnly();
    public IReadOnlyDictionary<string, string> TagToIndexId => _tagToIndexId.AsReadOnly();

    /// <summary>
    /// Returns the current AdaptiveConfig (used to generate xray config).
    /// </summary>
    public AdaptiveConfig? GetCurrentConfig()
    {
        if (_activeSetManager == null) return null;
        return new AdaptiveConfig
        {
            ActiveTags = _activeSetManager.GetProductionTags(),
            CooldownTags = _activeSetManager.GetCooldownTags(),
            ProbePorts = _probePorts,
            NodeScores = _nodes.ToDictionary(n => n.Tag, n => n.Score),
            TagToIndexId = _tagToIndexId,
        };
    }

    /// <summary>
    /// Emergency bypass: immediately stops all adaptive scheduling, probe services,
    /// and telemetry. Sets Enabled=false so subsequent config generation produces
    /// default (non-adaptive) xray config. Does NOT restart xray — the caller
    /// must regenerate and reload config after calling this.
    /// </summary>
    public async Task EmergencyDisableAdaptiveAsync()
    {
        if (!_isRunning) return;

        if (_adaptiveItem != null)
            _adaptiveItem.Enabled = false;

        await StopAsync();

        await _updateFunc!(false, $"[{_tag}] Adaptive scheduling emergency-disabled. Restore default xray config.");
    }

    // ── Phase 1: synchronous init (call BEFORE LoadCore) ────

    /// <summary>
    /// Build node states and allocate probe ports.
    /// Returns the initial AdaptiveConfig so the first generated core already
    /// includes probe inbounds for all nodes.
    /// </summary>
    public AdaptiveConfig InitializeNodes(
        Config config,
        ProfileItem groupNode,
        Dictionary<string, ProfileItem> childNodes,
        Func<bool, string, Task> updateFunc,
        IAdaptivePolicyApplier policyApplier)
    {
        _updateFunc = updateFunc;
        _policyApplier = policyApplier;
        _nodes = BuildNodeStates(groupNode, childNodes);
        _probePorts = AllocateProbePorts();
        _adaptiveItem = config.AdaptiveSchedulerItem;
        _groupAdaptiveSettings = groupNode.GetProtocolExtra();

        _freezeController.Reset();
        _collector = new FailureCollector(_scorer, _cooldown, null, _freezeController);

        var fraction = _groupAdaptiveSettings?.AdaptiveActiveFraction ?? ActiveSetManager.DefaultActiveFraction;
        var minProd = _groupAdaptiveSettings?.AdaptiveMinProductionNodes ?? ActiveSetManager.DefaultMinProductionNodes;
        var maxProd = _groupAdaptiveSettings?.AdaptiveMaxProductionNodes ?? ActiveSetManager.DefaultMaxProductionNodes;
        Logging.SaveLog($"[Adaptive] InitializeNodes: nodeCount={_nodes.Count} perGroupFraction={_groupAdaptiveSettings?.AdaptiveActiveFraction} perGroupMin={_groupAdaptiveSettings?.AdaptiveMinProductionNodes} perGroupMax={_groupAdaptiveSettings?.AdaptiveMaxProductionNodes} effectiveFraction={fraction:F2} effectiveMin={minProd} effectiveMax={maxProd}");
        _activeSetManager = new ActiveSetManager(_nodes, fraction, minProd, maxProd);
        _tagToIndexId = _nodes.ToDictionary(n => n.Tag, n => n.ChildIndexId);
        _nodesInitialized = true;

        return new AdaptiveConfig
        {
            ActiveTags = _nodes.Select(n => n.Tag).ToList(),
            CooldownTags = [],
            ProbePorts = _probePorts,
            NodeScores = _nodes.ToDictionary(n => n.Tag, n => n.Score),
            TagToIndexId = _tagToIndexId,
        };
    }

    // ── Phase 2a: bootstrap (call BEFORE LoadCore) ──────────

    /// <summary>
    /// Runs parallel TCP-connect probes to all nodes to seed initial scores.
    /// Does NOT require xray-core to be running — probes connect directly to remote hosts.
    /// Call after <see cref="InitializeNodes"/> and before the first LoadCore so the
    /// initial active-set balancer selector uses real latency data, not defaults.
    /// </summary>
    public async Task BootstrapAsync()
    {
        if (!_nodesInitialized || _nodes.Count == 0)
            return;

        await RestorePersistedScoresAsync();

        await _updateFunc!(false, $"[{_tag}] Bootstrap probing {_nodes.Count} nodes...");
        await _bootstrapper.InitializeAsync(_nodes, _scorer, _dnsCache);
        await _updateFunc!(false, $"[{_tag}] Bootstrap complete.");
    }

    // ── Phase 2b: probes + monitor (call AFTER LoadCore) ────

    /// <summary>
    /// Starts the continuous ProbeService and active-set monitor loop.
    /// Call only AFTER LoadCore, because ProbeService sends HTTP HEAD requests
    /// through xray-core's probe SOCKS5 inbounds.
    /// </summary>
    public async Task StartProbesAsync()
    {
        if (!_nodesInitialized || _nodes.Count == 0)
            return;

        _scoreLogger = new ScoreLogger(_nodes);
        _scoreLogger.Start();

        _collector = new FailureCollector(_scorer, _cooldown, _scoreLogger, _freezeController);
        var probeConfig = BuildProbeConfig();
        _probeService = new ProbeService(_nodes, tag => _probePorts[tag], _collector, probeConfig, _recoveryFsm);
        _probeService.Start();

        // Prime the change tracker with the current (post-bootstrap) top-K set
        // so the first monitor check doesn't fire a spurious reload.
        _activeSetManager!.Prime();

        _monitorCts = new CancellationTokenSource();
        _ = MonitorActiveSetAsync(_monitorCts.Token);

        _isRunning = true;
        await _updateFunc!(false, $"[{_tag}] ProbeService and monitor started with {_nodes.Count} nodes.");
    }

    // ── Convenience: single-call bootstrap+probes (call AFTER LoadCore) ──

    /// <summary>
    /// Runs bootstrap THEN starts ProbeService + monitor.
    /// Call AFTER LoadCore. Prefer <see cref="BootstrapAsync"/> before LoadCore
    /// + <see cref="StartProbesAsync"/> after to avoid a double reload.
    /// </summary>
    [Obsolete("Use BootstrapAsync before LoadCore + StartProbesAsync after LoadCore to avoid a double reload.")]
    public async Task StartMonitoringAsync()
    {
        await BootstrapAsync();

        // Bootstrap may have changed scores without changing the top-K set.
        // Force a policy refresh so the weighted balancer uses real scores.
        await OnActiveSetChangedAsync();

        await StartProbesAsync();
    }

    public async Task StopAsync()
    {
        _isRunning = false;
        _nodesInitialized = false;
        _monitorCts?.Cancel();
        _monitorCts?.Dispose();
        _monitorCts = null;

        _probeService?.Dispose();
        _probeService = null;

        _scoreLogger?.Stop();
        _scoreLogger = null;

        _freezeController.Reset();

        _nodes.Clear();
        _probePorts.Clear();
        _tagToIndexId.Clear();
        if (_policyApplier is not null)
        {
            try { await _policyApplier.DisposeAsync(); }
            catch { /* best-effort */ }
        }
        _policyApplier = null;
        _activeSetManager = null;
        _adaptiveItem = null;

        await Task.CompletedTask;
    }

    public NodeSnapshot[] GetSnapshots()
    {
        return _nodes.Select(n => n.Snapshot()).ToArray();
    }

    // ── Private ─────────────────────────────────────────────

    /// <summary>
    /// Restores persisted scores from ProfileExItem before bootstrap probing.
    /// Nodes with a saved AdaptiveScore > 0 start from their known state rather
    /// than default scores, preserving EWMA state across restarts.
    /// Scores older than 4 hours are treated as stale and reset to 50.
    /// </summary>
    private async Task RestorePersistedScoresAsync()
    {
        var profileExs = await ProfileExManager.Instance.GetProfileExs();
        var now = DateTime.UtcNow;
        var staleThreshold = TimeSpan.FromHours(4);
        int restored = 0;
        int stale = 0;
        int healthRestored = 0;
        foreach (var node in _nodes)
        {
            var ex = profileExs.FirstOrDefault(p => p.IndexId == node.ChildIndexId);
            if (ex == null || ex.AdaptiveScore <= 0) continue;

            if (ex.AdaptiveLastObserved != default && now - ex.AdaptiveLastObserved > staleThreshold)
            {
                // Score is stale — reset to 50, ignore persisted value
                node.UpdateScore(500.0, 0.0, 50.0, 0);
                stale++;
                continue;
            }

            double lat = ex.AdaptiveLatency > 0 ? ex.AdaptiveLatency : 500.0;
            node.UpdateScore(lat, 0.0, ex.AdaptiveScore, 0);
            restored++;

            // P0#1: Restore recovery FSM state
            if (ex.AdaptiveHealthState > 0) // 0 = Active (default), skip for default
            {
                var healthState = (NodeHealthState)ex.AdaptiveHealthState;
                node.SetHealthState(healthState);
                healthRestored++;
            }

            // P2: Restore TrafficTier (0=Production, 1=Standby default → skip)
            if (ex.AdaptiveTrafficTier == 0) // Production
            {
                node.SetTrafficTier(TrafficTier.Production);
            }
        }
        if (restored > 0 || stale > 0 || healthRestored > 0)
            await _updateFunc!(false, $"[{_tag}] Restored scores: {restored} fresh, {stale} stale, {healthRestored} health FSM states out of {_nodes.Count} nodes.");
    }

    private async Task MonitorActiveSetAsync(CancellationToken ct)
    {
        const int checkIntervalMs = 5000;
        DateTime lastUpdate = DateTime.MinValue;
        const int minUpdateIntervalMs = ReloadCooldownMs; // v7.6: 60s hard floor (§5.1.5)

        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(checkIntervalMs, ct).ConfigureAwait(false);

            if (_activeSetManager == null || !_isRunning) continue;

            // P0#1: Advance recovery FSM
            // Step 1: FAILED nodes whose cooldown expired → RECOVERY_PROBING
            // Step 2: STABILITY_VERIFICATION nodes whose verification period elapsed → ACTIVE
            bool recoveryPromoted = false;
            foreach (var node in _nodes)
            {
                if (node.HealthState == NodeHealthState.Failed && !node.IsInCooldown)
                {
                    _recoveryFsm.TransitionToRecoveryProbing(node);
                }
                if (_recoveryFsm.ShouldPromoteToActive(node))
                {
                    node.ResetHealthFsm();
                    recoveryPromoted = true;
                    _scoreLogger?.LogEvent("recovery_promoted", new Dictionary<string, object?>
                    {
                        ["node"] = node.Tag,
                        ["from"] = "stability_verification",
                        ["to"] = "active",
                    });
                }
            }

            // P2: Check for active-set changes first (computes production tags)
            bool hasChanged = _activeSetManager.HasActiveSetChanged();

            // P0#1: Evaluate global freeze state using the current production set
            var activeTags = _activeSetManager.CurrentProductionTags;
            var freezeDecision = _freezeController.Evaluate(activeTags);

            switch (freezeDecision.Type)
            {
                case FreezeDecisionType.TriggerFreeze:
                    _scoreLogger?.LogEvent("global_freeze", new Dictionary<string, object?>
                    {
                        ["reason"] = freezeDecision.Reason,
                        ["frozen_active_tags"] = freezeDecision.FrozenActiveTags,
                        ["freeze_duration_s"] = 60,
                    });
                    await _updateFunc!(false,
                        $"[{_tag}] GLOBAL FREEZE triggered: {freezeDecision.Reason}. Active set frozen for 60s.");
                    continue; // skip active-set check this cycle

                case FreezeDecisionType.BlockMutation:
                    // Freeze still active — skip all mutations, probe continues
                    continue;

                case FreezeDecisionType.Unfreeze:
                {
                    _scoreLogger?.LogEvent("global_freeze_end", new Dictionary<string, object?>
                    {
                        ["freeze_duration_s"] = (_clock.UtcNow - _freezeController.FreezeStartedAt).TotalSeconds,
                        ["current_active_tags"] = activeTags,
                    });
                    await _updateFunc!(false,
                        $"[{_tag}] Global freeze ended. Resuming normal active-set management.");
                    _activeSetManager.MarkDirty(); // force re-evaluation next cycle
                    continue;
                }

                case FreezeDecisionType.EmergencyDisable:
                    // Handled by the event subscription → EmergencyDisableAdaptiveAsync
                    continue;
            }

            // Normal operation — reload if active-set changed or recovery promoted
            if (hasChanged || recoveryPromoted)
            {
                bool catastrophic = _activeSetManager.IsEligiblePoolEmpty;
                var elapsed = (DateTime.UtcNow - lastUpdate).TotalMilliseconds;
                // Catastrophic (all eligible gone) MUST bypass minUpdateInterval (§6.4)
                if (catastrophic || elapsed >= minUpdateIntervalMs)
                {
                    if (catastrophic)
                        Logging.SaveLog($"[Adaptive] Monitor: CATASTROPHIC bypass — eligible pool empty, reloading immediately (elapsed={elapsed:F0}ms)");
                    lastUpdate = DateTime.UtcNow;
                    await OnActiveSetChangedAsync(catastrophic);
                }
            }
        }
    }

    /// <summary>
    /// Called when the score-ranked top-K set changes (node entered/left cooldown,
    /// or ranking shifted). Builds a fresh AdaptiveConfig including current scores
    /// and delegates to the policy applier. The <see cref="ReloadPolicyApplier"/>
    /// enforces a reload budget to prevent config thrashing.
    /// </summary>
    private async Task OnActiveSetChangedAsync(bool bypassDebounce = false)
    {
        if (_activeSetManager == null) return;

        var active = _activeSetManager.GetProductionTags();
        var cooldown = _activeSetManager.GetCooldownTags();

        await _updateFunc!(false,
            $"[{_tag}] Active set changed: active=[{string.Join(",", active)}] cooldown=[{string.Join(",", cooldown)}]");

        _scoreLogger?.LogEvent("active_set_change", new Dictionary<string, object?>
        {
            ["active_tags"] = active,
            ["cooldown_tags"] = cooldown,
            ["scores"] = _nodes.ToDictionary(n => n.Tag, n => (object)n.Score),
            ["added"] = _activeSetManager.LastAdded,
            ["removed"] = _activeSetManager.LastRemoved,
            ["change_reasons"] = BuildChangeReasons(_activeSetManager.LastAdded, _activeSetManager.LastRemoved),
            ["bypass_debounce"] = bypassDebounce,
        });

        var config = new AdaptiveConfig
        {
            ActiveTags = active,
            CooldownTags = cooldown,
            ProbePorts = _probePorts,
            NodeScores = _nodes.ToDictionary(n => n.Tag, n => n.Score),
            TagToIndexId = _tagToIndexId,
        };

        AppEvents.ActiveSetChanged.Publish(config);

        if (_policyApplier != null)
        {
            if (bypassDebounce)
            {
                await _policyApplier.ApplyImmediateAsync(config);
            }
            else
            {
                await _policyApplier.ApplyAsync(config);
            }
            _scoreLogger?.LogEvent("xray_reload", new Dictionary<string, object?>
            {
                ["active_tags"] = active,
                ["trigger"] = bypassDebounce ? "catastrophic_bypass" : "active_set_change",
            });
        }
    }

    private static List<NodeState> BuildNodeStates(
        ProfileItem groupNode,
        Dictionary<string, ProfileItem> childNodes)
    {
        var list = new List<NodeState>();
        int idx = 0;
        foreach (var (childId, child) in childNodes)
        {
            if (!child.IsValid()) continue;

            var tag = $"{Global.ProxyTag}-{idx + 1}-{child.Remarks}";
            list.Add(new NodeState
            {
                Tag = tag,
                Host = child.Address ?? "",
                Port = child.Port,
                Protocol = ProxyProtocol.Tcp,
                ChildIndexId = childId,
            });
            idx++;
        }
        return list;
    }

    private Dictionary<string, int> AllocateProbePorts()
    {
        var ports = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var node in _nodes)
        {
            int port = Utils.GetFreePort();
            ports[node.Tag] = port;
        }
        return ports;
    }

    /// <summary>
    /// Builds a per-node reason map explaining WHY each node entered or left
    /// the sticky top-K set. This is decision traceability (§3.4): every
    /// active-set change must be explainable after the fact.
    /// </summary>
    private Dictionary<string, string> BuildChangeReasons(
        IReadOnlyList<string> added, IReadOnlyList<string> removed)
    {
        var reasons = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var tag in added)
        {
            var node = _nodes.FirstOrDefault(n => n.Tag == tag);
            if (node == null) continue;
            if (!node.IsInCooldown && node.Score >= ActiveSetManager.EntryThreshold)
                reasons[tag] = $"score_crossed_entry: score={node.Score:F1} >= {ActiveSetManager.EntryThreshold}";
            else if (!node.IsInCooldown)
                reasons[tag] = $"score_ranking: entered top-K at score={node.Score:F1}";
            else
                reasons[tag] = $"cooldown_cleared: score={node.Score:F1}";
        }
        foreach (var tag in removed)
        {
            var node = _nodes.FirstOrDefault(n => n.Tag == tag);
            if (node == null) continue;
            if (node.IsInCooldown)
                reasons[tag] = $"entered_cooldown: score={node.Score:F1}, consecutive_failures={node.ConsecutiveFailures}";
            else if (node.Score < ActiveSetManager.ExitThreshold)
                reasons[tag] = $"score_below_exit: score={node.Score:F1} < {ActiveSetManager.ExitThreshold}";
            else
                reasons[tag] = $"score_ranking: displaced from top-K at score={node.Score:F1}";
        }
        return reasons;
    }

    /// <summary>
    /// Builds probe config by merging per-group settings (ProtocolExtra) with global defaults.
    /// Per-group values override global AdaptiveSchedulerItem when explicitly set.
    /// </summary>
    private AdaptiveSchedulerItem BuildProbeConfig()
    {
        var global = _adaptiveItem ?? new AdaptiveSchedulerItem();
        var group = _groupAdaptiveSettings;

        return new AdaptiveSchedulerItem
        {
            Enabled = global.Enabled, // Global engine switch still gatekeeps
            ProbeUrl = group?.AdaptiveProbeUrl ?? global.ProbeUrl,
            ProbeIntervalSec = group?.AdaptiveProbeIntervalSec ?? global.ProbeIntervalSec,
            ProbeTimeoutMs = group?.AdaptiveProbeTimeoutMs ?? global.ProbeTimeoutMs,
        };
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _probeService?.Dispose();
    }
}
