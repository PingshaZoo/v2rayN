namespace ServiceLib.Handler.AdaptiveNodeScheduler;

/// <summary>
/// P3.1: Abstraction over xray's gRPC HandlerService API for dynamic outbound management.
/// When available, allows RuntimePolicyApplier to add/remove outbounds without restarting xray.
/// When unavailable, RuntimePolicyApplier falls back to full config reload.
/// </summary>
public interface IXrayHandlerClient
{
    /// <summary>
    /// Returns true if the xray HandlerService API is reachable and accepting commands.
    /// </summary>
    Task<bool> IsAvailableAsync();

    /// <summary>
    /// Dynamically adds an outbound with the given tag and proxy settings.
    /// Returns true on success.
    /// </summary>
    Task<bool> AddOutboundAsync(string tag, string host, int port, CancellationToken ct = default);

    /// <summary>
    /// Dynamically removes an outbound by tag.
    /// Returns true on success.
    /// </summary>
    Task<bool> RemoveOutboundAsync(string tag, CancellationToken ct = default);
}
