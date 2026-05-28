using AwesomeAssertions;
using ServiceLib.Handler.AdaptiveNodeScheduler;
using ServiceLib.ViewModels;
using Xunit;

namespace ServiceLib.Tests.AdaptiveNodeScheduler;

/// <summary>
/// Verifies AdaptiveStatusVal computation from DB-persisted fields
/// matches the in-memory computation from NodeState objects.
/// Bug: GetProfileItemsEx() used only AdaptiveCooldown + AdaptiveActive booleans,
/// losing HealthState and TrafficTier detail on sort/refresh.
/// </summary>
public class AdaptiveStatusValTests
{
    // ── DB-field → Status string mapping (§v7.6 Bug 3 fix) ──────

    [Fact]
    public void Compute_Cooldown_ReturnsCooldown()
    {
        // Cooldown takes priority regardless of other fields
        var result = ProfilesViewModel.ComputeAdaptiveStatusVal(
            adaptiveCooldown: 1, adaptiveHealthState: 0, adaptiveTrafficTier: 0);
        result.Should().Be("Cooldown");
    }

    [Fact]
    public void Compute_HealthStateFailed_ReturnsFailed()
    {
        var result = ProfilesViewModel.ComputeAdaptiveStatusVal(
            adaptiveCooldown: 0, adaptiveHealthState: (int)NodeHealthState.Failed, adaptiveTrafficTier: 0);
        result.Should().Be("Failed");
    }

    [Fact]
    public void Compute_HealthStateRecoveryProbing_ReturnsRecoveryProbing()
    {
        var result = ProfilesViewModel.ComputeAdaptiveStatusVal(
            adaptiveCooldown: 0, adaptiveHealthState: (int)NodeHealthState.RecoveryProbing, adaptiveTrafficTier: 0);
        result.Should().Be("RecoveryProbing");
    }

    [Fact]
    public void Compute_HealthStateStabilityVerification_ReturnsStabilityVerification()
    {
        var result = ProfilesViewModel.ComputeAdaptiveStatusVal(
            adaptiveCooldown: 0, adaptiveHealthState: (int)NodeHealthState.StabilityVerification, adaptiveTrafficTier: 0);
        result.Should().Be("StabilityVerification");
    }

    [Fact]
    public void Compute_ActiveHealth_ProductionTrafficTier_ReturnsProduction()
    {
        var result = ProfilesViewModel.ComputeAdaptiveStatusVal(
            adaptiveCooldown: 0, adaptiveHealthState: (int)NodeHealthState.Active, adaptiveTrafficTier: (int)TrafficTier.Production);
        result.Should().Be("Production");
    }

    [Fact]
    public void Compute_ActiveHealth_StandbyTrafficTier_ReturnsStandby()
    {
        var result = ProfilesViewModel.ComputeAdaptiveStatusVal(
            adaptiveCooldown: 0, adaptiveHealthState: (int)NodeHealthState.Active, adaptiveTrafficTier: (int)TrafficTier.Standby);
        result.Should().Be("Standby");
    }

    [Fact]
    public void Compute_AllZero_ReturnsEmpty()
    {
        // No adaptive data at all — cooldown=0, healthState=Active, trafficTier=Standby
        var result = ProfilesViewModel.ComputeAdaptiveStatusVal(
            adaptiveCooldown: 0, adaptiveHealthState: 0, adaptiveTrafficTier: 1);
        result.Should().Be("Standby",
            "Active + Standby with no cooldown → Standby");
    }

    [Fact]
    public void Compute_Matches_UpdateAdaptiveQoS_Logic()
    {
        // Verify the DB-field computation matches the in-memory NodeState logic:
        // Cooldown > non-Active HealthState > TrafficTier (Production/Standby)
        // NodeHealthState enum: Active=0, Failed=1, RecoveryProbing=2, StabilityVerification=3
        // TrafficTier enum: Production=0, Standby=1

        // Cooldown
        ProfilesViewModel.ComputeAdaptiveStatusVal(1, 0, 0).Should().Be("Cooldown");
        // Failed
        ProfilesViewModel.ComputeAdaptiveStatusVal(0, 1, 0).Should().Be("Failed");
        // RecoveryProbing
        ProfilesViewModel.ComputeAdaptiveStatusVal(0, 2, 0).Should().Be("RecoveryProbing");
        // StabilityVerification
        ProfilesViewModel.ComputeAdaptiveStatusVal(0, 3, 0).Should().Be("StabilityVerification");
        // Active + Production
        ProfilesViewModel.ComputeAdaptiveStatusVal(0, 0, 0).Should().Be("Production");
        // Active + Standby
        ProfilesViewModel.ComputeAdaptiveStatusVal(0, 0, 1).Should().Be("Standby");
    }
}
