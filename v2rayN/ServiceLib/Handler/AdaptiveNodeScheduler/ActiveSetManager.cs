namespace ServiceLib.Handler.AdaptiveNodeScheduler;

/// <summary>
/// Manages the active set — which nodes are eligible for traffic.
/// When the active set changes (node enters/exits cooldown, or score ranking shifts),
/// fires an event so the manager can regenerate the xray balancer config.
/// </summary>
public sealed class ActiveSetManager
{
    private readonly IReadOnlyList<NodeState> _nodes;
    private readonly object _lock = new();
    private HashSet<string> _lastTopKSet = new(StringComparer.Ordinal);

    public ActiveSetManager(IReadOnlyList<NodeState> nodes)
    {
        _nodes = nodes;
    }

    /// <summary>
    /// Returns node tags that should be in the balancer selector.
    /// Uses QoS score for top-K selection + optionally one random explorer
    /// to prevent stagnation. Cooldown nodes are always excluded.
    /// </summary>
    public List<string> GetActiveTags()
    {
        var eligible = _nodes
            .Where(n => !n.IsInCooldown)
            .ToList();

        if (eligible.Count == 0) return [];
        if (eligible.Count <= 2) return eligible.Select(n => n.Tag).ToList();

        var sorted = eligible.OrderByDescending(n => n.Score).ToList();
        int topK = Math.Max(2, (int)Math.Ceiling(eligible.Count * 2.0 / 3.0));

        var active = sorted.Take(topK).Select(n => n.Tag).ToList();

        // Only add an explorer when it doesn't undo filtering —
        // i.e. when topK + 1 still leaves at least one node excluded.
        var remaining = sorted.Skip(topK).ToList();
        if (remaining.Count > 0 && topK + 1 < eligible.Count)
        {
            var explorer = remaining[Random.Shared.Next(remaining.Count)];
            active.Add(explorer.Tag);
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
    /// Checks whether the meaningful active set (topK by score) has changed
    /// since last call. Explorer rotations alone do NOT trigger a change.
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
            int topK = Math.Max(2, (int)Math.Ceiling(eligible.Count * 2.0 / 3.0));
            currentTopK = new HashSet<string>(
                eligible.OrderByDescending(n => n.Score).Take(topK).Select(n => n.Tag),
                StringComparer.Ordinal);
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
