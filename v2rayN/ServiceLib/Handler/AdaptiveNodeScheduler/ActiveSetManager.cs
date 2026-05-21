namespace ServiceLib.Handler.AdaptiveNodeScheduler;

/// <summary>
/// Manages the active set — which nodes are eligible for traffic.
/// Uses hysteresis (Entry=60, Exit=35) to prevent score oscillation from
/// causing frequent active-set changes and xray reloads.
///
/// When the active set changes (node enters/exits cooldown, or score ranking shifts),
/// fires an event so the manager can regenerate the xray balancer config.
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

    public ActiveSetManager(IReadOnlyList<NodeState> nodes)
    {
        _nodes = nodes;
    }

    /// <summary>
    /// Returns node tags that should be in the balancer selector.
    /// Uses hysteresis: nodes already in the active set stay until score &lt; ExitThreshold (35);
    /// nodes outside need score >= EntryThreshold (60) to enter.
    /// Then selects top-K by score + optionally one random explorer.
    /// Cooldown nodes are always excluded.
    /// </summary>
    public List<string> GetActiveTags()
    {
        var eligible = _nodes
            .Where(n => !n.IsInCooldown)
            .ToList();

        if (eligible.Count == 0) return [];
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

        // Explorer: pick one unused node with score >= ExitThreshold (35)
        // to give it exposure. The explorer gets ONE round but does NOT get
        // sticky status — it must pass Entry=60 to enter the sticky set next time.
        // Nodes below Exit=35 are truly dead; no point exposing them.
        var topKSet = new HashSet<string>(active, StringComparer.Ordinal);
        var usedTags = new HashSet<string>(active, StringComparer.Ordinal);
        var explorerPool = eligible
            .Where(n => !usedTags.Contains(n.Tag) && n.Score >= ExitThreshold)
            .ToList();
        if (explorerPool.Count > 0 && active.Count + 1 <= eligible.Count)
        {
            var explorer = explorerPool[Random.Shared.Next(explorerPool.Count)];
            active.Add(explorer.Tag);
        }

        // Only top-K nodes (before explorer) get sticky status.
        // Explorer is excluded so it doesn't bypass the Entry=60 gate permanently.
        lock (_lock) { _currentActiveSet = new HashSet<string>(topKSet, StringComparer.Ordinal); }

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
    /// </summary>
    public bool HasActiveSetChanged()
    {
        var eligible = _nodes
            .Where(n => !n.IsInCooldown)
            .ToList();

        HashSet<string> currentTopK;
        if (eligible.Count <= 2)
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
