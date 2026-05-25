using AwesomeAssertions;
using ServiceLib.Handler.AdaptiveNodeScheduler;
using Xunit;

namespace ServiceLib.Tests.AdaptiveNodeScheduler;

/// <summary>
/// Tests for ReloadPolicyApplier debounce behavior and catastrophic bypass (P1).
/// </summary>
public class ReloadPolicyApplierTests
{
    private static AdaptiveConfig CreateConfig(params string[] activeTags)
    {
        return new AdaptiveConfig
        {
            ActiveTags = activeTags.ToList(),
            CooldownTags = new List<string>(),
            ProbePorts = new Dictionary<string, int>(),
            NodeScores = new Dictionary<string, double>(),
            TagToIndexId = new Dictionary<string, string>(),
        };
    }

    [Fact]
    public async Task ApplyImmediateAsync_BypassesDebounce()
    {
        var appliedConfigs = new List<AdaptiveConfig>();
        var applier = new ReloadPolicyApplier(config =>
        {
            appliedConfigs.Add(config);
            return Task.CompletedTask;
        });

        var config1 = CreateConfig("A", "B");
        var config2 = CreateConfig("C");

        // Apply normally (starts debounce window)
        await applier.ApplyAsync(config1);
        appliedConfigs.Should().HaveCount(1);

        // ApplyImmediateAsync should fire immediately, even within debounce window
        await applier.ApplyImmediateAsync(config2);
        appliedConfigs.Should().HaveCount(2, "ApplyImmediateAsync should bypass debounce");

        appliedConfigs[1].ActiveTags.Should().ContainSingle().Which.Should().Be("C");
    }

    [Fact]
    public async Task ApplyImmediateAsync_CancelsPendingDebouncedReload()
    {
        var appliedConfigs = new List<AdaptiveConfig>();
        var applier = new ReloadPolicyApplier(config =>
        {
            appliedConfigs.Add(config);
            return Task.CompletedTask;
        });

        var config1 = CreateConfig("A");
        var config2 = CreateConfig("B");
        var immediateConfig = CreateConfig("C");

        // First apply starts debounce
        await applier.ApplyAsync(config1);
        appliedConfigs.Should().HaveCount(1);

        // Second apply within debounce window → queued (pending)
        var applyTask = applier.ApplyAsync(config2);

        // ApplyImmediateAsync cancels pending and fires immediately
        await applier.ApplyImmediateAsync(immediateConfig);
        appliedConfigs.Should().HaveCount(2, "immediate should fire right away");

        // The pending debounced reload should be cancelled (superseded)
        await applyTask;
        appliedConfigs.Should().HaveCount(2,
            "pending debounced reload was superseded by immediate apply");
    }

    [Fact]
    public async Task ApplyAsync_RespectsReloadBudget()
    {
        // Verify that rapid ApplyAsync calls are debounced (only one fires immediately,
        // subsequent calls are queued for the debounce window).
        var appliedConfigs = new List<AdaptiveConfig>();
        var applier = new ReloadPolicyApplier(config =>
        {
            appliedConfigs.Add(config);
            return Task.CompletedTask;
        });

        var config1 = CreateConfig("A");
        var config2 = CreateConfig("B");

        await applier.ApplyAsync(config1);
        await applier.ApplyAsync(config2);

        // config1 fires immediately, config2 is within debounce window
        // → config2 is either queued (pending) or coalesced.
        // Only config1 should have been applied synchronously.
        appliedConfigs.Should().HaveCount(1,
            "second apply within debounce window should be queued, not fired immediately");
        appliedConfigs[0].ActiveTags.Should().Contain("A");
    }

    // ── ReloadCooldown 60s hard floor (§5.1.5, v7.6) ──────────

    [Fact]
    public void ReloadCooldown_HardFloor_60Seconds()
    {
        AdaptiveSchedulerManager.ReloadCooldownMs.Should().Be(60_000,
            "ReloadCooldown must be exactly 60s — the 4th anti-churn layer");
    }

    [Fact]
    public void ReloadCooldown_GreaterThanOrEqual_NormalInterval()
    {
        // ReloadCooldown (60s) is the hard floor — NormalInterval (30s in ReloadPolicyApplier)
        // is an internal implementation detail. The scheduler's ReloadCooldown must be
        // at least 60s to prevent reload storms in 60+ node environments.
        AdaptiveSchedulerManager.ReloadCooldownMs.Should().BeGreaterThanOrEqualTo(60_000);
        AdaptiveSchedulerManager.ReloadCooldownMs.Should().BeLessThanOrEqualTo(120_000,
            "ReloadCooldown max 120s to avoid excessive staleness");
    }
}
