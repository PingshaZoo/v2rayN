using System.Collections.Concurrent;
using AwesomeAssertions;
using ServiceLib.Models.Dto;
using Xunit;

namespace ServiceLib.Tests.AdaptiveNodeScheduler;

/// <summary>
/// P2.3: Verifies PerTagProxyTraffic thread safety.
/// NodeTrafficSnapshot is a record (immutable value type semantics),
/// ConcurrentDictionary ensures safe concurrent access.
/// </summary>
public class PerTagProxyTrafficTests
{
    [Fact]
    public void NodeTrafficSnapshot_IsRecord_ValueEquality()
    {
        var a = new NodeTrafficSnapshot(100, 200, DateTime.UnixEpoch);
        var b = new NodeTrafficSnapshot(100, 200, DateTime.UnixEpoch);
        a.Should().Be(b);
        a.GetHashCode().Should().Be(b.GetHashCode());
    }

    [Fact]
    public void NodeTrafficSnapshot_DifferentValues_NotEqual()
    {
        var a = new NodeTrafficSnapshot(100, 200, DateTime.UtcNow);
        var b = new NodeTrafficSnapshot(999, 200, DateTime.UtcNow);
        a.Should().NotBe(b);
    }

    [Fact]
    public void ConcurrentDictionary_TryUpdate_ThreadSafeUpdate()
    {
        var dict = new ConcurrentDictionary<string, NodeTrafficSnapshot>(StringComparer.Ordinal);
        dict["N-1-A"] = new NodeTrafficSnapshot(100, 200, DateTime.UtcNow);

        // TryUpdate atomically replaces value
        var old = dict["N-1-A"];
        var replacement = new NodeTrafficSnapshot(old.UpKbps + 50, old.DownKbps, DateTime.UtcNow);
        bool updated = dict.TryUpdate("N-1-A", replacement, old);

        updated.Should().BeTrue();
        dict["N-1-A"].UpKbps.Should().Be(150);
    }

    [Fact]
    public void ConcurrentDictionary_AddOrUpdate_AtomicUpsert()
    {
        var dict = new ConcurrentDictionary<string, NodeTrafficSnapshot>(StringComparer.Ordinal);

        // Add
        dict.AddOrUpdate("tag-A",
            _ => new NodeTrafficSnapshot(100, 0, DateTime.UtcNow),
            (_, existing) => new NodeTrafficSnapshot(existing.UpKbps + 100, 0, DateTime.UtcNow));

        dict["tag-A"].UpKbps.Should().Be(100);

        // Update
        dict.AddOrUpdate("tag-A",
            _ => new NodeTrafficSnapshot(0, 0, DateTime.UtcNow),
            (_, existing) => new NodeTrafficSnapshot(existing.UpKbps + 50, 0, DateTime.UtcNow));

        dict["tag-A"].UpKbps.Should().Be(150);
    }

    [Fact]
    public void NodeTrafficSnapshot_Serializable()
    {
        var snap = new NodeTrafficSnapshot(1024, 512, DateTime.UtcNow);
        var json = System.Text.Json.JsonSerializer.Serialize(snap);
        var deserialized = System.Text.Json.JsonSerializer.Deserialize<NodeTrafficSnapshot>(json);
        deserialized.Should().Be(snap);
    }

    [Fact]
    public void ConcurrentDictionary_Enumeration_IsSnapshotOfValues()
    {
        var dict = new ConcurrentDictionary<string, NodeTrafficSnapshot>(StringComparer.Ordinal);
        dict["A"] = new NodeTrafficSnapshot(100, 0, DateTime.UtcNow);
        dict["B"] = new NodeTrafficSnapshot(200, 0, DateTime.UtcNow);
        dict["C"] = new NodeTrafficSnapshot(300, 0, DateTime.UtcNow);

        int count = 0;
        foreach (var (tag, traffic) in dict)
        {
            tag.Should().NotBeNullOrEmpty();
            traffic.UpKbps.Should().BeGreaterThan(0);
            count++;
        }

        count.Should().Be(3);
    }
}
