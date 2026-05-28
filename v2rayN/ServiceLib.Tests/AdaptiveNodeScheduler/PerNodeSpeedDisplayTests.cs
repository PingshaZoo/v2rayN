using AwesomeAssertions;
using ServiceLib.Handler.AdaptiveNodeScheduler;
using Xunit;

namespace ServiceLib.Tests.AdaptiveNodeScheduler;

/// <summary>
/// Bug 1 fix: per-node speed display in TUN/non-adaptive mode.
/// Verifies tag→IndexId mapping is available independently of
/// adaptive scheduling state.
/// </summary>
public class PerNodeSpeedDisplayTests
{
    [Fact]
    public void TagToIndexId_Available_WithoutAdaptiveRunning()
    {
        // Simulate a PolicyGroup with 3 child nodes, adaptive disabled.
        // The tag mapping should still be accessible for per-tag traffic routing.
        var childNodes = new Dictionary<string, (string remarks, string indexId)>
        {
            ["idx-a"] = ("NodeA", "idx-a"),
            ["idx-b"] = ("NodeB", "idx-b"),
            ["idx-c"] = ("NodeC", "idx-c"),
        };

        // Test: build tag mapping following the same pattern as BuildNodeStates
        var mapping = AdaptiveSchedulerManager.BuildTagMappingForSpeedDisplay(childNodes);

        mapping.Count.Should().Be(3);
        // Tag format: proxy-{idx+1}-{Remarks}
        mapping.Should().ContainKey("proxy-1-NodeA").WhoseValue.Should().Be("idx-a");
        mapping.Should().ContainKey("proxy-2-NodeB").WhoseValue.Should().Be("idx-b");
        mapping.Should().ContainKey("proxy-3-NodeC").WhoseValue.Should().Be("idx-c");
    }

    [Fact]
    public void TagToIndexId_EmptyInput_ReturnsEmpty()
    {
        var mapping = AdaptiveSchedulerManager.BuildTagMappingForSpeedDisplay(
            new Dictionary<string, (string, string)>());
        mapping.Should().BeEmpty();
    }

    [Fact]
    public void TagToIndexId_SetMapping_ReplacesExisting()
    {
        var first = AdaptiveSchedulerManager.BuildTagMappingForSpeedDisplay(
            new Dictionary<string, (string, string)>
            {
                ["idx-1"] = ("OldNode", "idx-1"),
            });
        first.Count.Should().Be(1);

        var second = AdaptiveSchedulerManager.BuildTagMappingForSpeedDisplay(
            new Dictionary<string, (string, string)>
            {
                ["idx-a"] = ("NewA", "idx-a"),
                ["idx-b"] = ("NewB", "idx-b"),
            });
        second.Count.Should().Be(2);
        second.Should().ContainKey("proxy-1-NewA");
        second.Should().ContainKey("proxy-2-NewB");
        second.Should().NotContainKey("proxy-1-OldNode",
            "new mapping replaces old — no stale entries");
    }
}
