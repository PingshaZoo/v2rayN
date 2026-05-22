namespace ServiceLib.Handler.AdaptiveNodeScheduler;

/// <summary>
/// Manages the active set — which nodes are eligible for production traffic.
/// Uses hysteresis (Entry=60, Exit=35) to prevent score oscillation from
/// causing frequent active-set changes and xray reloads.
///
/// <h2>Stability Objective (§3.4)</h2>
/// All decisions serve one goal: <b>minimize active-set churn while maximizing
/// healthy-node exposure</b>. Priority: Stability > Responsiveness > Optimality.
///
/// <h2>Explorer isolation</h2>
/// Explorer nodes receive probe traffic only — they do NOT enter the production
/// xray balancer selector. An explorer must earn its way into the sticky set by
/// crossing Entry=60 through sustained probe quality. This prevents experimental
/// traffic from mixing into production.
///
/// <h2>Decision traceability</h2>
/// When the sticky top-K set changes, <see cref="LastChange"/> records which nodes
/// were added/removed so the scheduler can log a causal trace in the
/// <c>active_set_change</c> JSONL event.
/// </summary>
public sealed class ActiveSetManager
{
    /// <summary>Score must be >= this to enter the active set for the first time.</summary>
    public const double EntryThreshold = 60.0;
    /// <summary>Score must drop below this to be evicted from the active set.</summary>
    public const double ExitThreshold = 35.0;

    private readonly IReadOnlyList<NodeState> _nodes;
    private readonly object _lock = new();
    private HashSet<string> _lastTopKSet = new(StringComparer.Ordinal);
    private HashSet<string> _currentActiveSet = new(StringComparer.Ordinal);

    /// <summary>Tags added in the most recent sticky-set change, if any.</summary>
    public IReadOnlyList<string> LastAdded { get; private set; } = Array.Empty<string>();
    /// <summary>Tags removed in the most recent sticky-set change, if any.</summary>
    public IReadOnlyList<string> LastRemoved { get; private set; } = Array.Empty<string>();

    public ActiveSetManager(IReadOnlyList<NodeState> nodes)
    {
        _nodes = nodes;
    }

    /// <summary>
    /// Returns node tags that should be in the balancer selector.
    /// Uses hysteresis: nodes already in the active set stay until score &lt; ExitThreshold (35);
    /// nodes outside need score >= EntryThreshold (60) to enter.
    /// Then selects top-K by score.
    ///
    /// Explorer nodes receive probe traffic only — they do NOT enter the
    /// production selector. Explorer must cross Entry=60 to enter sticky.
    /// Cooldown nodes are always excluded.
    /// </summary>
    public List<string> GetActiveTags()
    {
        var eligible = _nodes
            .Where(n => !n.IsInCooldown)
            .ToList();

        if (eligible.Count == 0)
        {
            // All nodes in cooldown — select the one with shortest remaining
            // cooldown time. Better to route through a soon-to-recover node
            // than to leave the balancer with an empty selector.
            var soonest = _nodes
                .OrderBy(n => (n.CooldownUntil - DateTime.UtcNow).TotalMilliseconds)
                .First();
            var tags = new List<string> { soonest.Tag };
            lock (_lock) { _currentActiveSet = new HashSet<string>(tags, StringComparer.Ordinal); }
            return tags;
        }
        if (eligible.Count <= 2)
        {
            var tags = eligible.Select(n => n.Tag).ToList();
            lock (_lock) { _currentActiveSet = new HashSet<string>(tags, StringComparer.Ordinal); }
            return tags;
        }

        // Separate nodes into "sticky" (currently active, protected by ExitThreshold)
        // and "candidate" (not currently active, must pass EntryThreshold).
        var sticky = new List<NodeState>();
        var candidates = new List<NodeState>();
        HashSet<string> currentSet;
        lock (_lock) { currentSet = new HashSet<string>(_currentActiveSet, StringComparer.Ordinal); }

        foreach (var node in eligible)
        {
            if (currentSet.Contains(node.Tag))
            {
                // Node is in the current active set — use ExitThreshold
                if (node.Score >= ExitThreshold)
                    sticky.Add(node);
                // else: falls below ExitThreshold → ejected
            }
            else
            {
                // Node is not in the current active set — use EntryThreshold
                if (node.Score >= EntryThreshold)
                    candidates.Add(node);
            }
        }

        // topK = max(2, ceil(N * 2/3)) — at least 2 nodes, up to 2/3 of eligible.
        // This is more inclusive than the old design (ceil(N * 0.5)) to keep
        // enough nodes in the active set for meaningful uniform random distribution.
        int topK = Math.Max(2, (int)Math.Ceiling(eligible.Count * 2.0 / 3.0));

        var sortedSticky = sticky.OrderByDescending(n => n.Score).ToList();
        var sortedCandidates = candidates.OrderByDescending(n => n.Score).ToList();

        // Fill the active set: sticky nodes first (they have priority due to hysteresis),
        // then fill remaining slots with top candidates.
        var active = new List<string>();
        active.AddRange(sortedSticky.Take(topK).Select(n => n.Tag));

        int remainingSlots = topK - active.Count;
        if (remainingSlots > 0)
        {
            active.AddRange(sortedCandidates.Take(remainingSlots).Select(n => n.Tag));
        }

        // Only top-K nodes get sticky status and enter the production selector.
        // Explorer nodes receive probe traffic only (ProbeService probes all nodes),
        // not production traffic. Explorer must pass Entry=60 to enter sticky.
        var topKSet = new HashSet<string>(active, StringComparer.Ordinal);
        lock (_lock) { _currentActiveSet = new HashSet<string>(topKSet, StringComparer.Ordinal); }

        // Safety net: if hysteresis would produce an empty active set but eligible
        // nodes exist (all scores in [35, 60) on first call), fall back to raw
        // top-K by score. The balancer must never have an empty selector.
        if (active.Count == 0 && eligible.Count > 0)
        {
            active = eligible
                .OrderByDescending(n => n.Score)
                .Take(topK)
                .Select(n => n.Tag)
                .ToList();
            var fallbackSet = new HashSet<string>(active, StringComparer.Ordinal);
            lock (_lock) { _currentActiveSet = fallbackSet; }
        }

        return active;
    }

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

    /// <summary>
    /// Checks whether the meaningful active set (topK by score, with hysteresis)
    /// has changed since last call. Explorer rotations alone do NOT trigger a change.
    /// Returns true if a config update is needed.
    ///
    /// When a change is detected, <see cref="LastAdded"/> and <see cref="LastRemoved"/>
    /// are populated so the scheduler can log a causal trace.
    /// </summary>
    public bool HasActiveSetChanged()
    {
        var eligible = _nodes
            .Where(n => !n.IsInCooldown)
            .ToList();

        HashSet<string> currentTopK;
        if (eligible.Count == 0)
        {
            var soonest = _nodes
                .OrderBy(n => (n.CooldownUntil - DateTime.UtcNow).TotalMilliseconds)
                .First();
            currentTopK = new HashSet<string>(StringComparer.Ordinal) { soonest.Tag };
        }
        else if (eligible.Count <= 2)
        {
            currentTopK = new HashSet<string>(
                eligible.Select(n => n.Tag), StringComparer.Ordinal);
        }
        else
        {
            // Recalculate top-K with hysteresis applied,
            // but strip the explorer for comparison purposes.
            HashSet<string> currentSet;
            lock (_lock) { currentSet = new HashSet<string>(_currentActiveSet, StringComparer.Ordinal); }

            var sticky = new List<NodeState>();
            var candidates = new List<NodeState>();
            foreach (var node in eligible)
            {
                if (currentSet.Contains(node.Tag))
                {
                    if (node.Score >= ExitThreshold) sticky.Add(node);
                }
                else
                {
                    if (node.Score >= EntryThreshold) candidates.Add(node);
                }
            }

            int topK = Math.Max(2, (int)Math.Ceiling(eligible.Count * 2.0 / 3.0));
            var active = new HashSet<string>(StringComparer.Ordinal);
            foreach (var n in sticky.OrderByDescending(n => n.Score).Take(topK))
                active.Add(n.Tag);

            int remainingSlots = topK - active.Count;
            if (remainingSlots > 0)
            {
                foreach (var n in candidates.OrderByDescending(n => n.Score).Take(remainingSlots))
                    active.Add(n.Tag);
            }

            currentTopK = active;
        }

        lock (_lock)
        {
            if (_lastTopKSet.SetEquals(currentTopK))
                return false;

            // Decision traceability: record what changed so the scheduler
            // can include a causal trace in the active_set_change JSONL event.
            LastAdded = currentTopK.Except(_lastTopKSet).ToList();
            LastRemoved = _lastTopKSet.Except(currentTopK).ToList();
            _lastTopKSet = currentTopK;
            return true;
        }
    }

    /// <summary>
    /// Force the next HasActiveSetChanged() to return true.
    /// </summary>
    public void MarkDirty()
    {
        lock (_lock) { _lastTopKSet.Clear(); }
    }

    /// <summary>
    /// Prime the change tracker with the current top-K set so the next
    /// HasActiveSetChanged() only returns true if an actual change occurs.
    /// Call after initialization/bootstrap to prevent a spurious first reload.
    /// </summary>
    public void Prime()
    {
        HasActiveSetChanged(); // computes and saves current topK, ignores result
    }
}
