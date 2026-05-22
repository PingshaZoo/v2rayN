namespace ServiceLib.Handler.AdaptiveNodeScheduler;

public enum FailureType { None, Timeout, Refused, TlsError, NetworkError, UnexpectedEof, DnsResolutionFailure, DnsPoisoningSuspected }

public sealed class FailureCollector
{
    private readonly ScoreCalculator _scorer;
    private readonly CooldownFsm _cooldown;
    private readonly ScoreLogger? _logger;
    private readonly GlobalFreezeController? _freezeController;

    public FailureCollector(ScoreCalculator scorer, CooldownFsm cooldown)
    {
        _scorer = scorer;
        _cooldown = cooldown;
    }

    /// <summary>
    /// P2.5: Optional ScoreLogger for replayable telemetry.
    /// When set, probe_result and ewma_update events are written to adaptive.log.
    /// </summary>
    public FailureCollector(ScoreCalculator scorer, CooldownFsm cooldown, ScoreLogger? logger)
        : this(scorer, cooldown)
    {
        _logger = logger;
    }

    /// <summary>
    /// P0#1: Optional GlobalFreezeController and ScoreLogger for full P0 integration.
    /// </summary>
    public FailureCollector(ScoreCalculator scorer, CooldownFsm cooldown,
                            ScoreLogger? logger, GlobalFreezeController? freezeController)
        : this(scorer, cooldown, logger)
    {
        _freezeController = freezeController;
    }

    public void RecordSuccess(NodeState node, double ttfbMs)
    {
        double alpha = DecayedAlpha(node.LastObserved);

        double oldLatency = node.EwmaLatencyMs;
        double oldScore = node.Score;
        double newLatency = Ewma(oldLatency, ttfbMs, alpha);
        double newLoss = Ewma(node.EwmaLossRate, 0.0, alpha);
        double newScore = _scorer.Compute(newLatency, newLoss);

        node.UpdateScore(newLatency, newLoss, newScore,
                         consecutiveFailures: 0);

        // P2.5: emit telemetry events
        _logger?.LogEvent("probe_result", new Dictionary<string, object?>
        {
            ["node"] = node.Tag,
            ["ttfb_ms"] = ttfbMs.ToString("F1"),
            ["success"] = true,
        });
        _logger?.LogEvent("ewma_update", new Dictionary<string, object?>
        {
            ["node"] = node.Tag,
            ["old_latency_ms"] = oldLatency.ToString("F0"),
            ["new_latency_ms"] = newLatency.ToString("F0"),
            ["alpha"] = alpha.ToString("F3"),
            ["old_score"] = oldScore.ToString("F1"),
            ["new_score"] = newScore.ToString("F1"),
        });
    }

    public void RecordFailure(NodeState node, FailureType type,
                              IReadOnlyList<NodeState> allNodes)
    {
        // TlsError is a configuration error, not a network quality signal.
        // DnsResolutionFailure / DnsPoisoningSuspected are DNS-layer issues,
        // not node failures. Do not penalize EWMA and do not enter cooldown.
        if (type is FailureType.TlsError or FailureType.DnsResolutionFailure or FailureType.DnsPoisoningSuspected)
        {
            string note = type switch
            {
                FailureType.TlsError => "TlsError — no penalty, config issue",
                FailureType.DnsResolutionFailure => "DnsResolutionFailure — no penalty, DNS layer issue",
                FailureType.DnsPoisoningSuspected => "DnsPoisoningSuspected — no penalty, possible GFW poisoning",
                _ => "no penalty",
            };
            _logger?.LogEvent("probe_result", new Dictionary<string, object?>
            {
                ["node"] = node.Tag,
                ["success"] = false,
                ["failure_type"] = type.ToString().ToLowerInvariant(),
                ["note"] = note,
            });
            return;
        }

        (double penaltyLoss, double penaltyLatencyMs) = GetPenalty(type, node);

        double alpha = DecayedAlpha(node.LastObserved);

        double oldLatency = node.EwmaLatencyMs;
        double oldScore = node.Score;
        double newLatency = Ewma(oldLatency, penaltyLatencyMs, alpha);
        double newLoss = Ewma(node.EwmaLossRate, penaltyLoss, alpha);
        double newScore = _scorer.Compute(newLatency, newLoss);

        // §11.8 Freeze gate: during global freeze, observation continues (EWMA update)
        // but state transitions are blocked — no consecutiveFailure increment, no cooldown.
        // This prevents latent cooldown explosion when freeze ends.
        if (_freezeController is { IsFrozen: true })
        {
            node.UpdateScore(newLatency, newLoss, newScore,
                             consecutiveFailures: node.ConsecutiveFailures); // keep existing count
            _freezeController.RecordFailure(node.Tag);

            _logger?.LogEvent("probe_result", new Dictionary<string, object?>
            {
                ["node"] = node.Tag,
                ["success"] = false,
                ["failure_type"] = type.ToString().ToLowerInvariant(),
                ["penalty_loss"] = penaltyLoss.ToString("F2"),
                ["penalty_latency_ms"] = penaltyLatencyMs.ToString("F0"),
                ["freeze_gate"] = true,
            });
            _logger?.LogEvent("ewma_update", new Dictionary<string, object?>
            {
                ["node"] = node.Tag,
                ["old_latency_ms"] = oldLatency.ToString("F0"),
                ["new_latency_ms"] = newLatency.ToString("F0"),
                ["alpha"] = alpha.ToString("F3"),
                ["old_score"] = oldScore.ToString("F1"),
                ["new_score"] = newScore.ToString("F1"),
                ["consecutive_failures"] = node.ConsecutiveFailures,
                ["freeze_gate"] = true,
            });
            return;
        }

        int newFails = node.ConsecutiveFailures + 1;

        node.UpdateScore(newLatency, newLoss, newScore, newFails);
        _cooldown.TryEnterCooldown(node, allNodes);

        // P0#1: Notify freeze controller of this failure
        _freezeController?.RecordFailure(node.Tag);

        // P2.5: emit telemetry events
        _logger?.LogEvent("probe_result", new Dictionary<string, object?>
        {
            ["node"] = node.Tag,
            ["success"] = false,
            ["failure_type"] = type.ToString().ToLowerInvariant(),
            ["penalty_loss"] = penaltyLoss.ToString("F2"),
            ["penalty_latency_ms"] = penaltyLatencyMs.ToString("F0"),
        });
        _logger?.LogEvent("ewma_update", new Dictionary<string, object?>
        {
            ["node"] = node.Tag,
            ["old_latency_ms"] = oldLatency.ToString("F0"),
            ["new_latency_ms"] = newLatency.ToString("F0"),
            ["alpha"] = alpha.ToString("F3"),
            ["old_score"] = oldScore.ToString("F1"),
            ["new_score"] = newScore.ToString("F1"),
            ["consecutive_failures"] = newFails,
            ["in_cooldown"] = node.IsInCooldown,
        });
    }

    /// <summary>
    /// Returns (penaltyLoss, penaltyLatencyMs) per failure type.
    /// TlsError returns (0, unchanged) — configuration errors are not network quality.
    /// </summary>
    public static (double penaltyLoss, double penaltyLatencyMs) GetPenalty(
        FailureType type, NodeState node) =>
        type switch
        {
            FailureType.Refused => (1.0, 10_000),
            FailureType.Timeout => (0.8, 10_000),
            FailureType.NetworkError => (0.7, 10_000),
            FailureType.UnexpectedEof => (0.4, node.EwmaLatencyMs * 1.5),
            FailureType.TlsError => (0.0, node.EwmaLatencyMs),
            FailureType.DnsResolutionFailure => (0.0, node.EwmaLatencyMs),
            FailureType.DnsPoisoningSuspected => (0.0, node.EwmaLatencyMs),
            _ => (0.5, 10_000),
        };

    private static double DecayedAlpha(DateTime lastObserved)
    {
        double ageSec = Math.Max(0,
            (DateTime.UtcNow - lastObserved).TotalSeconds);
        return 0.05 + 0.25 * Math.Exp(-ageSec / 60.0);
    }

    private static double Ewma(double old, double current, double alpha) =>
        old * (1 - alpha) + current * alpha;
}
