using AwesomeAssertions;
using ServiceLib.Handler.AdaptiveNodeScheduler;
using Xunit;

namespace ServiceLib.Tests.AdaptiveNodeScheduler;

/// <summary>
/// P0.5: Verifies EmergencyDisableAdaptiveAsync behavior.
/// The emergency bypass must stop all probes, logging, and clear state
/// so that subsequent config generation produces default (non-adaptive) xray config.
/// </summary>
public class EmergencyDisableAdaptiveTests
{
    /// <summary>
    /// Verifies that calling EmergencyDisableAdaptiveAsync when not running
    /// does not throw (idempotent).
    /// </summary>
    [Fact]
    public async Task EmergencyDisable_WhenNotRunning_ShouldNotThrow()
    {
        // The singleton may or may not be running.
        // Emergency disable must be safe to call at any time.
        var instance = AdaptiveSchedulerManager.Instance;

        // If it's not running, this should be a no-op
        await instance.EmergencyDisableAdaptiveAsync();

        instance.IsRunning.Should().BeFalse(
            "after emergency disable, IsRunning should be false");
    }

    /// <summary>
    /// Verifies that after emergency disable, GetCurrentConfig returns null
    /// (no ActiveSetManager), which signals to callers that adaptive is inactive.
    /// </summary>
    [Fact]
    public async Task EmergencyDisable_GetCurrentConfig_ShouldReturnNull()
    {
        var instance = AdaptiveSchedulerManager.Instance;

        await instance.EmergencyDisableAdaptiveAsync();

        var config = instance.GetCurrentConfig();
        config.Should().BeNull(
            "GetCurrentConfig must return null when adaptive is disabled/not initialized");
    }

    /// <summary>
    /// Verifies that after emergency disable, Nodes collection is empty.
    /// </summary>
    [Fact]
    public async Task EmergencyDisable_Nodes_ShouldBeEmpty()
    {
        var instance = AdaptiveSchedulerManager.Instance;

        await instance.EmergencyDisableAdaptiveAsync();

        instance.Nodes.Should().BeEmpty();
    }

    /// <summary>
    /// Verifies that after emergency disable, ProbePorts are empty.
    /// </summary>
    [Fact]
    public async Task EmergencyDisable_ProbePorts_ShouldBeEmpty()
    {
        var instance = AdaptiveSchedulerManager.Instance;

        await instance.EmergencyDisableAdaptiveAsync();

        instance.ProbePorts.Should().BeEmpty();
    }

    /// <summary>
    /// Verifies that after emergency disable, TagToIndexId is empty.
    /// </summary>
    [Fact]
    public async Task EmergencyDisable_TagToIndexId_ShouldBeEmpty()
    {
        var instance = AdaptiveSchedulerManager.Instance;

        await instance.EmergencyDisableAdaptiveAsync();

        instance.TagToIndexId.Should().BeEmpty();
    }
}
