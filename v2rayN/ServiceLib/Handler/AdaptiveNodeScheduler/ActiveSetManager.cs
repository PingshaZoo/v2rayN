using ServiceLib.Common;

namespace ServiceLib.Handler.AdaptiveNodeScheduler;

/// <summary>
/// Manages the Production/Standby two-tier active set — which nodes are eligible
/// for production traffic vs probe-only evaluation.
///
/// <h2>Bounded Production Pool (§5.7)</h2>
/// Production pool size is bounded by TargetProductionSize = clamp(ceil(N × 0.35), 3, 6).
/// Nodes enter Production through vacancy-driven promotion only — a Standby node
/// is promoted when ProductionCount &lt; TargetProductionSize. Score-driven replacement
/// (demoting a healthy Production node because a Standby has a higher score) is PROHIBITED.
///
/// <h2>Hysteresis (Entry=60, Exit=35)</h2>
/// Production → Standby demotion: Score &lt; ExitThreshold (35).
/// Standby → Production promotion: Score &gt;= EntryThreshold (60).
/// Once in Production, a node stays until score drops below 35 — sticky protection.
///
/// <h2>TrafficTier (§5.7.5)</h2>
/// TrafficTier is orthogonal to HealthState. A node with HealthState=Active can be
/// Standby if the Production Pool is full. TrafficTier only affects selector membership.
///
/// <h2>Decision traceability</h2>
/// When the production set changes, <see cref="LastAdded"/> and <see cref="LastRemoved"/>
/// record which nodes were promoted/demoted so the scheduler can log a causal trace.
/// </summary>
public sealed class ActiveSetManager
{
    /// <summary>Score must be >= this to enter the Production pool from Standby.</summary>
    public const double EntryThreshold = 60.0;
    /// <summary>Score must drop below this to be demoted from Production to Standby.</summary>
    public const double ExitThreshold = 35.0;
    /// <summary>Minimum score for fallback promotion (buffer above Exit to prevent immediate re-demotion).</summary>
    public const double FallbackPromotionThreshold = 48.0;

    /// <summary>Default fraction of eligible nodes targeted for Production pool.</summary>
    public const double DefaultActiveFraction = 0.35;
    /// <summary>Default minimum Production pool size.</summary>
    public const int DefaultMinProductionNodes = 3;
    /// <summary>Default maximum Production pool size (hard cap).</summary>
    public const int DefaultMaxProductionNodes = 6;

    private readonly IReadOnlyList<NodeState> _nodes;
    private readonly object _lock = new();
    private readonly double _activeFraction;
    private readonly int _minProductionNodes;
    private readonly int _maxProductionNodes;
    private HashSet<string> _lastProductionSet = new(StringComparer.Ordinal);
    private bool _lastEligibleWasEmpty;
    private DecisionTrace _lastDecisionTrace;

    private sealed class DecisionTrace
    {
        public int TotalNodes, EligibleCount, TargetSize;
        public double ActiveFraction;
        public int MinProduction, MaxProduction;
        public int CurrentProductionCount, StandbyCount, KeepCount, DemotedCount, Vacancy;
        public int StandardPromotedCount, FallbackPromotedCount, FinalProductionCount;
        public bool SafetyNetTriggered;
        public List<string> Tags = new();
        public List<string> DemotedTags = new();
        public List<string> StandardPromotedTags = new();
        public List<string> FallbackPromotedTags = new();
    }

    /// <summary>Tags promoted to Production in the most recent change, if any.</summary>
    public IReadOnlyList<string> LastAdded { get; private set; } = Array.Empty<string>();
    /// <summary>Tags demoted from Production in the most recent change, if any.</summary>
    public IReadOnlyList<string> LastRemoved { get; private set; } = Array.Empty<string>();
    /// <summary>True when the last computation had zero eligible nodes (HealthState=Active &amp;&amp; !IsInCooldown). Used for catastrophic debounce bypass.</summary>
    public bool IsEligiblePoolEmpty => _lastEligibleWasEmpty;

    /// <summary>The production tags from the most recent computation (via GetProductionTags or HasActiveSetChanged).</summary>
    public List<string> CurrentProductionTags
    {
        get { lock (_lock) { return _lastProductionSet.ToList(); } }
    }

    public ActiveSetManager(IReadOnlyList<NodeState> nodes)
        : this(nodes, DefaultActiveFraction, DefaultMinProductionNodes, DefaultMaxProductionNodes) { }

    public ActiveSetManager(IReadOnlyList<NodeState> nodes, double activeFraction, int minProductionNodes, int maxProductionNodes)
    {
        _nodes = nodes;
        _activeFraction = Math.Clamp(activeFraction, 0.15, 0.60);
        _minProductionNodes = Math.Clamp(minProductionNodes, 2, 8);
        _maxProductionNodes = Math.Clamp(maxProductionNodes, 3, 12);
        Logging.SaveLog($"[Adaptive] ActiveSetManager init: totalNodes={nodes.Count}, fraction={_activeFraction:F2}, minProduction={_minProductionNodes}, maxProduction={_maxProductionNodes}");
    }

    // ── Production Pool (Tier A) ──────────────────────────────────

    /// <summary>
    /// Returns node tags that should enter the xray balancer selector (Production Pool, Tier A).
    ///
    /// Algorithm:
    /// 1. Filter eligible: HealthState=Active, !IsInCooldown.
    /// 2. Compute TargetProductionSize = clamp(ceil(N × ActiveFraction), Min, Max).
    /// 3. Keep current Production nodes with score >= ExitThreshold (sticky).
    /// 4. If vacancy exists, promote top-scoring Standby nodes with score >= EntryThreshold.
    /// 5. If still short, fallback: promote Standby with score >= ExitThreshold (temporary fill).
    /// 6. Score-driven replacement is PROHIBITED — a higher-score Standby NEVER replaces
    ///    a healthy Production node.
    /// </summary>
    public List<string> GetProductionTags()
    {
        var eligible = _nodes
            .Where(n => !n.IsInCooldown && n.HealthState == NodeHealthState.Active)
            .ToList();

        if (eligible.Count == 0)
        {
            _lastEligibleWasEmpty = true;
            // Fallback priority (§8.4):
            //   1. RECOVERY_PROBING nodes → highest probe success count
            //   2. STABILITY_VERIFICATION nodes → passed basic reachability
            //   3. Cooldown nodes → shortest remaining cooldown time
            var fallback = _nodes
                .OrderBy(n =>
                {
                    if (n.HealthState == NodeHealthState.RecoveryProbing)
                        return -1000 - n.RecoveryProbeSuccessCount;
                    if (n.HealthState == NodeHealthState.StabilityVerification)
                        return -500;
                    return (n.CooldownUntil - DateTime.UtcNow).TotalMilliseconds;
                })
                .First();
            Logging.SaveLog($"[Adaptive] ProductionPool: eligible=0 → fallback tag={fallback.Tag} healthState={fallback.HealthState} cooldownUntil={fallback.CooldownUntil:yyyy-MM-dd HH:mm:ss}");
            return new List<string> { fallback.Tag };
        }
        _lastEligibleWasEmpty = false;

        int targetSize = ComputeTargetSize(eligible.Count);

        // Separate eligible nodes by current TrafficTier
        var currentProduction = eligible
            .Where(n => n.TrafficTier == TrafficTier.Production)
            .ToList();
        var standby = eligible
            .Where(n => n.TrafficTier == TrafficTier.Standby)
            .ToList();

        // Step 1: Keep Production nodes above Exit (sticky protection)
        var keep = currentProduction
            .Where(n => n.Score >= ExitThreshold)
            .OrderByDescending(n => n.Score)
            .ToList();

        // Demote Production nodes that fell below Exit
        var demoted = currentProduction.Where(n => n.Score < ExitThreshold).ToList();
        foreach (var node in demoted)
            node.SetTrafficTier(TrafficTier.Standby);

        var production = new List<NodeState>();
        production.AddRange(keep);

        int vacancy = targetSize - production.Count;

        // Step 2: Fill vacancies from Standby (score >= Entry, sorted desc)
        List<NodeState> promoteStandard = new();
        List<NodeState> promoteFallback = new();
        if (vacancy > 0)
        {
            promoteStandard = standby
                .Where(n => n.Score >= EntryThreshold)
                .OrderByDescending(n => n.Score)
                .Take(vacancy)
                .ToList();
            foreach (var node in promoteStandard)
                node.SetTrafficTier(TrafficTier.Production);
            production.AddRange(promoteStandard);

            // Step 3: If still short, fallback to score >= Exit (temporary fill)
            int stillShort = targetSize - production.Count;
            if (stillShort > 0)
            {
                promoteFallback = standby
                    .Except(promoteStandard)
                    .Where(n => n.Score >= FallbackPromotionThreshold)
                    .OrderByDescending(n => n.Score)
                    .Take(stillShort)
                    .ToList();
                foreach (var node in promoteFallback)
                    node.SetTrafficTier(TrafficTier.Production);
                production.AddRange(promoteFallback);
            }
        }

        var tags = production.Select(n => n.Tag).ToList();

        // Safety net: if hysteresis+promotion produced empty but eligible exists
        bool safetyNetTriggered = false;
        if (tags.Count == 0 && eligible.Count > 0)
        {
            safetyNetTriggered = true;
            tags = eligible
                .OrderByDescending(n => n.Score)
                .Take(targetSize)
                .Select(n => n.Tag)
                .ToList();
            foreach (var node in eligible.Take(targetSize))
                node.SetTrafficTier(TrafficTier.Production);
        }

        // P2 state snapshot — stored for HasActiveSetChanged to log on change
        _lastDecisionTrace = new DecisionTrace
        {
            TotalNodes = _nodes.Count,
            EligibleCount = eligible.Count,
            TargetSize = targetSize,
            ActiveFraction = _activeFraction,
            MinProduction = _minProductionNodes,
            MaxProduction = _maxProductionNodes,
            CurrentProductionCount = currentProduction.Count,
            StandbyCount = standby.Count,
            KeepCount = keep.Count,
            DemotedCount = demoted.Count,
            Vacancy = vacancy,
            StandardPromotedCount = promoteStandard.Count,
            FallbackPromotedCount = promoteFallback.Count,
            FinalProductionCount = tags.Count,
            SafetyNetTriggered = safetyNetTriggered,
            Tags = tags,
            DemotedTags = demoted.Select(n => $"{n.Tag}(score={n.Score:F1})").ToList(),
            StandardPromotedTags = promoteStandard.Select(n => $"{n.Tag}(score={n.Score:F1})").ToList(),
            FallbackPromotedTags = promoteFallback.Select(n => $"{n.Tag}(score={n.Score:F1})").ToList(),
        };

        return tags;
    }

    // ── Standby Pool (Tier B) ────────────────────────────────────

    /// <summary>
    /// Returns tags of Standby nodes (HealthState=Active, !IsInCooldown, not in Production).
    /// These nodes receive probe traffic only — they do NOT enter the production selector.
    /// </summary>
    public List<string> GetStandbyTags()
    {
        var eligible = _nodes
            .Where(n => !n.IsInCooldown && n.HealthState == NodeHealthState.Active
                        && n.TrafficTier == TrafficTier.Standby)
            .Select(n => n.Tag)
            .ToList();
        return eligible;
    }

    // ── Cooldown ─────────────────────────────────────────────────

    /// <summary>
    /// Returns tags of nodes currently in cooldown.
    /// </summary>
    public List<string> GetCooldownTags()
    {
        return _nodes
            .Where(n => n.IsInCooldown)
            .Select(n => n.Tag)
            .ToList();
    }

    // ── Change Detection ─────────────────────────────────────────

    /// <summary>
    /// Checks whether the Production Pool has changed since last call.
    /// Delegates to <see cref="GetProductionTags"/> for computation and compares
    /// the result with the last known production set.
    ///
    /// When a change is detected, <see cref="LastAdded"/> and <see cref="LastRemoved"/>
    /// are populated so the scheduler can log a causal trace.
    /// </summary>
    public bool HasActiveSetChanged()
    {
        var productionTags = GetProductionTags();
        var productionSet = new HashSet<string>(productionTags, StringComparer.Ordinal);

        lock (_lock)
        {
            if (_lastProductionSet.SetEquals(productionSet))
                return false;

            LastAdded = productionSet.Except(_lastProductionSet).ToList();
            LastRemoved = _lastProductionSet.Except(productionSet).ToList();
            _lastProductionSet = productionSet;

            var t = _lastDecisionTrace;
            Logging.SaveLog(
                $"[Adaptive] ProductionPool CHANGED: added=[{string.Join(",", LastAdded)}] removed=[{string.Join(",", LastRemoved)}] " +
                $"totalN={t.TotalNodes} eligible={t.EligibleCount} target={t.TargetSize} (N×{t.ActiveFraction:F2} clamp [{t.MinProduction},{t.MaxProduction}]) " +
                $"currentProd={t.CurrentProductionCount} standby={t.StandbyCount} keep={t.KeepCount} demoted={t.DemotedCount} vacancy={t.Vacancy} " +
                $"stdPromo={t.StandardPromotedCount} fallbackPromo={t.FallbackPromotedCount} finalProd={t.FinalProductionCount} safetyNet={t.SafetyNetTriggered} " +
                $"tags=[{string.Join(",", t.Tags)}]");
            foreach (var d in t.DemotedTags)
                Logging.SaveLog($"[Adaptive] ProductionPool DEMOTE: {d} (score<Exit={ExitThreshold})");
            foreach (var p in t.StandardPromotedTags)
                Logging.SaveLog($"[Adaptive] ProductionPool PROMOTE(standard>=Entry={EntryThreshold}): {p}");
            foreach (var p in t.FallbackPromotedTags)
                Logging.SaveLog($"[Adaptive] ProductionPool PROMOTE(fallback>={FallbackPromotionThreshold}): {p}");
            return true;
        }
    }

    /// <summary>
    /// Force the next HasActiveSetChanged() to return true.
    /// </summary>
    public void MarkDirty()
    {
        lock (_lock) { _lastProductionSet.Clear(); }
    }

    /// <summary>
    /// Prime the change tracker with the current Production Pool so the next
    /// HasActiveSetChanged() only returns true if an actual change occurs.
    /// Call after initialization/bootstrap to prevent a spurious first reload.
    /// </summary>
    public void Prime()
    {
        HasActiveSetChanged(); // computes and saves current production set, ignores result
    }

    // ── Helpers ──────────────────────────────────────────────────

    /// <summary>
    /// TargetProductionSize = clamp(ceil(eligible_count × ActiveFraction), MinProductionNodes, MaxProductionNodes).
    /// Bounded elastic: small pools use all nodes, large pools cap at max.
    /// </summary>
    public int ComputeTargetSize(int eligibleCount)
    {
        int raw = (int)Math.Ceiling(eligibleCount * _activeFraction);
        return Math.Clamp(raw, _minProductionNodes, _maxProductionNodes);
    }
}
