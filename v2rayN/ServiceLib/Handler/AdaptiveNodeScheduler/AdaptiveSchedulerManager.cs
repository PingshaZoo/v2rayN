using ServiceLib.Models.Entities;
using ServiceLib.Models.Configs;
using ServiceLib.Models.CoreConfigs;

namespace ServiceLib.Handler.AdaptiveNodeScheduler;

/// <summary>
/// Control Plane orchestrator for adaptive node scheduling.
/// Maintains the QoS Engine (scoring, cooldown, probing) and delegates policy
/// application to an <see cref="IAdaptivePolicyApplier"/>.
///
/// Startup sequence:
///   1. InitializeNodes() — sync, builds node states + allocates probe ports.
///      Returns initial AdaptiveConfig so the first LoadCore includes probe inbounds.
///   2. StartMonitoringAsync() — async, runs bootstrap probing, starts ProbeService
///      and active-set monitor loop. Call AFTER LoadCore.
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

    private FailureCollector? _collector;
    private ProbeService? _probeService;
    private ActiveSetManager? _activeSetManager;
    private IAdaptivePolicyApplier? _policyApplier;
    private List<NodeState> _nodes = [];
    private Dictionary<string, int> _probePorts = new(StringComparer.Ordinal);
    private CancellationTokenSource? _monitorCts;
    private bool _isRunning;
    private bool _nodesInitialized;

    private Func<bool, string, Task>? _updateFunc;
    private AdaptiveSchedulerItem? _adaptiveItem;
    private const string _tag = "AdaptiveScheduler";

    private AdaptiveSchedulerManager()
    {
    }

    // ── Public API ──────────────────────────────────────────

    public bool IsRunning => _isRunning;
    public IReadOnlyList<NodeState> Nodes => _nodes.AsReadOnly();
    public IReadOnlyDictionary<string, int> ProbePorts => _probePorts.AsReadOnly();

    /// <summary>
    /// Returns the current AdaptiveConfig (used to generate xray config).
    /// </summary>
    public AdaptiveConfig? GetCurrentConfig()
    {
        if (_activeSetManager == null) return null;
        return new AdaptiveConfig
        {
            ActiveTags = _activeSetManager.GetActiveTags(),
            CooldownTags = _activeSetManager.GetCooldownTags(),
            ProbePorts = _probePorts,
            NodeScores = _nodes.ToDictionary(n => n.Tag, n => n.Score)
        };
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

        _collector = new FailureCollector(_scorer, _cooldown);
        _activeSetManager = new ActiveSetManager(_nodes);
        _nodesInitialized = true;

        return new AdaptiveConfig
        {
            ActiveTags = _nodes.Select(n => n.Tag).ToList(),
            CooldownTags = [],
            ProbePorts = _probePorts,
            NodeScores = _nodes.ToDictionary(n => n.Tag, n => n.Score)
        };
    }

    // ── Phase 2a: bootstrap (call BEFORE LoadCore) ──────────

    /// <summary>
    /// Runs parallel TCP-connect probes to all nodes to seed initial scores.
    /// Does NOT require xray-core to be running — probes connect directly to remote hosts.
    /// Call after <see cref="InitializeNodes"/> and before the first LoadCore so the
    /// initial weighted balancer selector uses real latency data, not defaults.
    /// </summary>
    public async Task BootstrapAsync()
    {
        if (!_nodesInitialized || _nodes.Count == 0)
            return;

        await _updateFunc!(false, $"[{_tag}] Bootstrap probing {_nodes.Count} nodes...");
        await _bootstrapper.InitializeAsync(_nodes, _scorer);
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

        _probeService = new ProbeService(_nodes, tag => _probePorts[tag], _collector!, _adaptiveItem!);
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

        _nodes.Clear();
        _probePorts.Clear();
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

    private async Task MonitorActiveSetAsync(CancellationToken ct)
    {
        const int checkIntervalMs = 5000;
        DateTime lastUpdate = DateTime.MinValue;
        const int minUpdateIntervalMs = 10_000;

        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(checkIntervalMs, ct).ConfigureAwait(false);

            if (_activeSetManager == null || !_isRunning) continue;

            if (_activeSetManager.HasActiveSetChanged())
            {
                var elapsed = (DateTime.UtcNow - lastUpdate).TotalMilliseconds;
                if (elapsed >= minUpdateIntervalMs)
                {
                    lastUpdate = DateTime.UtcNow;
                    await OnActiveSetChangedAsync();
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
    private async Task OnActiveSetChangedAsync()
    {
        if (_activeSetManager == null) return;

        var active = _activeSetManager.GetActiveTags();
        var cooldown = _activeSetManager.GetCooldownTags();

        await _updateFunc!(false,
            $"[{_tag}] Active set changed: active=[{string.Join(",", active)}] cooldown=[{string.Join(",", cooldown)}]");

        var config = new AdaptiveConfig
        {
            ActiveTags = active,
            CooldownTags = cooldown,
            ProbePorts = _probePorts,
            NodeScores = _nodes.ToDictionary(n => n.Tag, n => n.Score)
        };

        AppEvents.ActiveSetChanged.Publish(config);

        if (_policyApplier != null)
            await _policyApplier.ApplyAsync(config);
    }

    private static List<NodeState> BuildNodeStates(
        ProfileItem groupNode,
        Dictionary<string, ProfileItem> childNodes)
    {
        var list = new List<NodeState>();
        int idx = 0;
        foreach (var (_, child) in childNodes)
        {
            if (!child.IsValid()) continue;

            var tag = $"{Global.ProxyTag}-{idx + 1}-{child.Remarks}";
            list.Add(new NodeState
            {
                Tag = tag,
                Host = child.Address ?? "",
                Port = child.Port,
                Protocol = ProxyProtocol.Tcp,
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

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _probeService?.Dispose();
    }
}
