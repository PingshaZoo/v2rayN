namespace ServiceLib.Handler.AdaptiveNodeScheduler;

/// <summary>
/// Abstraction for applying adaptive scheduling policy changes.
/// The current implementation (<see cref="ReloadPolicyApplier"/>) regenerates
/// and reloads xray config. Future implementations may use xray runtime APIs
/// to update balancer/routing without a full restart.
/// </summary>
public interface IAdaptivePolicyApplier : IAsyncDisposable
{
    /// <summary>
    /// Apply the given adaptive config with debounce. Called when the active set
    /// changes meaningfully under normal conditions.
    /// </summary>
    Task ApplyAsync(AdaptiveConfig config, CancellationToken ct = default);

    /// <summary>
    /// Apply the given adaptive config immediately, bypassing any debounce window.
    /// Used for catastrophic scenarios (e.g., all eligible nodes disappeared).
    /// </summary>
    Task ApplyImmediateAsync(AdaptiveConfig config, CancellationToken ct = default);
}
