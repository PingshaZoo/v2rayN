using AwesomeAssertions;
using ServiceLib.Handler.AdaptiveNodeScheduler;
using Xunit;

namespace ServiceLib.Tests.AdaptiveNodeScheduler;

/// <summary>
/// P2.1: Verifies XrayStatsPoller anomaly detection logic.
/// Uses FakeXrayStatsClient and TriggerPollAsync() for deterministic,
/// timing-free tests.
/// </summary>
public class XrayStatsPollerTests
{
    /// <summary>
    /// Controllable IXrayStatsClient for tests.
    /// </summary>
    private sealed class FakeXrayStatsClient : IXrayStatsClient
    {
        private Dictionary<string, long> _stats = new(StringComparer.Ordinal);

        public void SetStats(Dictionary<string, long> stats)
        {
            _stats = stats;
        }

        public Task<Dictionary<string, long>> GetOutboundStatsAsync()
        {
            return Task.FromResult(
                new Dictionary<string, long>(_stats, StringComparer.Ordinal));
        }
    }

    private static NodeState CreateNode(string tag, double score = 50)
    {
        var node = new NodeState
        {
            Tag = tag,
            Host = "127.0.0.1",
            Port = 1080,
            Protocol = ProxyProtocol.Tcp,
            ChildIndexId = tag,
        };
        node.UpdateScore(500, 0.05, score, 0);
        return node;
    }

    // ── Anomaly detection ──────────────────────────────────────

    [Fact]
    public async Task HighScore_LowThroughput_FiresAnomaly()
    {
        var node = CreateNode("proxy-1", score: 80);
        var fakeClient = new FakeXrayStatsClient();
        var poller = new XrayStatsPoller(fakeClient, new[] { node });

        var anomalies = new List<(string tag, double bps)>();
        poller.ThroughputAnomalyDetected += (tag, bps) => anomalies.Add((tag, bps));

        // Establish baseline
        fakeClient.SetStats(new Dictionary<string, long>(StringComparer.Ordinal)
        { ["proxy-1"] = 10_000 });
        await poller.TriggerPollAsync();

        // +512 bytes in 5s = 102.4 B/s → below 1024 → anomaly
        fakeClient.SetStats(new Dictionary<string, long>(StringComparer.Ordinal)
        { ["proxy-1"] = 10_512 });
        await poller.TriggerPollAsync();

        anomalies.Count.Should().Be(1, "102 B/s with score=80 should trigger anomaly");
        anomalies[0].tag.Should().Be("proxy-1");
        anomalies[0].bps.Should().BeApproximately(102.4, 0.5);
    }

    [Fact]
    public async Task LowScore_LowThroughput_DoesNotFireAnomaly()
    {
        var node = CreateNode("proxy-2", score: 25);
        var fakeClient = new FakeXrayStatsClient();
        var poller = new XrayStatsPoller(fakeClient, new[] { node });

        var anomalies = new List<(string, double)>();
        poller.ThroughputAnomalyDetected += (tag, bps) => anomalies.Add((tag, bps));

        fakeClient.SetStats(new Dictionary<string, long>(StringComparer.Ordinal)
        { ["proxy-2"] = 10_000 });
        await poller.TriggerPollAsync();

        fakeClient.SetStats(new Dictionary<string, long>(StringComparer.Ordinal)
        { ["proxy-2"] = 10_100 }); // 20 B/s
        await poller.TriggerPollAsync();

        anomalies.Should().BeEmpty("score 25 < 30 — anomaly threshold not met");
    }

    [Fact]
    public async Task HighScore_HighThroughput_DoesNotFireAnomaly()
    {
        var node = CreateNode("proxy-3", score: 90);
        var fakeClient = new FakeXrayStatsClient();
        var poller = new XrayStatsPoller(fakeClient, new[] { node });

        var anomalies = new List<(string, double)>();
        poller.ThroughputAnomalyDetected += (tag, bps) => anomalies.Add((tag, bps));

        fakeClient.SetStats(new Dictionary<string, long>(StringComparer.Ordinal)
        { ["proxy-3"] = 0 });
        await poller.TriggerPollAsync();

        // +50,000 bytes in 5s = 10,000 B/s → well above 1024
        fakeClient.SetStats(new Dictionary<string, long>(StringComparer.Ordinal)
        { ["proxy-3"] = 50_000 });
        await poller.TriggerPollAsync();

        anomalies.Should().BeEmpty("10 KB/s with score=90 is healthy throughput");
    }

    [Fact]
    public async Task FirstPoll_NoBaseline_DoesNotFireAnomaly()
    {
        var node = CreateNode("proxy-4", score: 80);
        var fakeClient = new FakeXrayStatsClient();
        var poller = new XrayStatsPoller(fakeClient, new[] { node });

        var anomalies = new List<(string, double)>();
        poller.ThroughputAnomalyDetected += (tag, bps) => anomalies.Add((tag, bps));

        // Only one poll — no prior baseline, so no delta computation
        fakeClient.SetStats(new Dictionary<string, long>(StringComparer.Ordinal)
        { ["proxy-4"] = 10_000 });
        await poller.TriggerPollAsync();

        anomalies.Should().BeEmpty("first poll has no baseline for delta calculation");
    }

    // ── Counter reset ──────────────────────────────────────────

    [Fact]
    public async Task CounterReset_NegativeDelta_ReBaselines()
    {
        var node = CreateNode("proxy-5", score: 80);
        var fakeClient = new FakeXrayStatsClient();
        var poller = new XrayStatsPoller(fakeClient, new[] { node });

        var anomalies = new List<(string, double)>();
        poller.ThroughputAnomalyDetected += (tag, bps) => anomalies.Add((tag, bps));

        // Establish baseline at 100K
        fakeClient.SetStats(new Dictionary<string, long>(StringComparer.Ordinal)
        { ["proxy-5"] = 100_000 });
        await poller.TriggerPollAsync();

        // Counter reset (xray restart): drops to 100 — negative delta → re-baseline
        fakeClient.SetStats(new Dictionary<string, long>(StringComparer.Ordinal)
        { ["proxy-5"] = 100 });
        await poller.TriggerPollAsync();

        // After re-baselining, tiny increase → anomaly with new baseline
        fakeClient.SetStats(new Dictionary<string, long>(StringComparer.Ordinal)
        { ["proxy-5"] = 200 }); // 100 bytes in 5s = 20 B/s
        await poller.TriggerPollAsync();

        anomalies.Count.Should().Be(1, "after re-baseline, 20 B/s with score=80 → anomaly");
    }

    // ── Throughput at threshold boundary ────────────────────────

    [Fact]
    public async Task Throughput_ExactlyBelowThreshold_FiresAnomaly()
    {
        var node = CreateNode("proxy-6", score: 75);
        var fakeClient = new FakeXrayStatsClient();
        var poller = new XrayStatsPoller(fakeClient, new[] { node });

        var anomalies = new List<(string, double)>();
        poller.ThroughputAnomalyDetected += (tag, bps) => anomalies.Add((tag, bps));

        // 5115 bytes in 5s = 1023 B/s → just below threshold
        fakeClient.SetStats(new Dictionary<string, long>(StringComparer.Ordinal)
        { ["proxy-6"] = 0 });
        await poller.TriggerPollAsync();

        fakeClient.SetStats(new Dictionary<string, long>(StringComparer.Ordinal)
        { ["proxy-6"] = 5_115 }); // 1023 B/s
        await poller.TriggerPollAsync();

        anomalies.Count.Should().Be(1, "1023 B/s < 1024 threshold → anomaly");
    }

    [Fact]
    public async Task Throughput_ExactlyAboveThreshold_DoesNotFire()
    {
        var node = CreateNode("proxy-7", score: 75);
        var fakeClient = new FakeXrayStatsClient();
        var poller = new XrayStatsPoller(fakeClient, new[] { node });

        var anomalies = new List<(string, double)>();
        poller.ThroughputAnomalyDetected += (tag, bps) => anomalies.Add((tag, bps));

        fakeClient.SetStats(new Dictionary<string, long>(StringComparer.Ordinal)
        { ["proxy-7"] = 0 });
        await poller.TriggerPollAsync();

        fakeClient.SetStats(new Dictionary<string, long>(StringComparer.Ordinal)
        { ["proxy-7"] = 5_121 }); // 1024.2 B/s
        await poller.TriggerPollAsync();

        anomalies.Should().BeEmpty("1024.2 B/s >= 1024 threshold → no anomaly");
    }

    // ── Lifecycle ───────────────────────────────────────────────

    [Fact]
    public void DoubleStart_DoesNotThrow()
    {
        var node = CreateNode("proxy-9");
        var fakeClient = new FakeXrayStatsClient();
        var poller = new XrayStatsPoller(fakeClient, new[] { node });

        poller.Start();
        poller.Start(); // second call should be idempotent

        poller.Stop();
    }

    [Fact]
    public async Task Score_ExactlyAtThreshold_DoesNotFire()
    {
        var node = CreateNode("proxy-10", score: 30);
        var fakeClient = new FakeXrayStatsClient();
        var poller = new XrayStatsPoller(fakeClient, new[] { node });

        var anomalies = new List<(string, double)>();
        poller.ThroughputAnomalyDetected += (tag, bps) => anomalies.Add((tag, bps));

        fakeClient.SetStats(new Dictionary<string, long>(StringComparer.Ordinal)
        { ["proxy-10"] = 10_000 });
        await poller.TriggerPollAsync();

        fakeClient.SetStats(new Dictionary<string, long>(StringComparer.Ordinal)
        { ["proxy-10"] = 10_100 }); // 20 B/s
        await poller.TriggerPollAsync();

        anomalies.Should().BeEmpty("score == 30, anomaly requires score > 30 (strict)");
    }

    // ── Multi-node ──────────────────────────────────────────────

    [Fact]
    public async Task MultiNode_OnlyAnomalousNodeFires()
    {
        var nodeA = CreateNode("proxy-A", score: 85);
        var nodeB = CreateNode("proxy-B", score: 80);
        var fakeClient = new FakeXrayStatsClient();
        var poller = new XrayStatsPoller(fakeClient, new[] { nodeA, nodeB });

        var anomalies = new List<(string tag, double bps)>();
        poller.ThroughputAnomalyDetected += (tag, bps) => anomalies.Add((tag, bps));

        // Baseline: both at 100K
        fakeClient.SetStats(new Dictionary<string, long>(StringComparer.Ordinal)
        {
            ["proxy-A"] = 100_000,
            ["proxy-B"] = 100_000,
        });
        await poller.TriggerPollAsync();

        // A: high throughput (OK), B: low throughput (anomaly)
        fakeClient.SetStats(new Dictionary<string, long>(StringComparer.Ordinal)
        {
            ["proxy-A"] = 150_000, // +50KB in 5s = 10KB/s → fine
            ["proxy-B"] = 100_512, // +512 bytes in 5s = 102 B/s → anomaly
        });
        await poller.TriggerPollAsync();

        anomalies.Count.Should().Be(1, "only B is anomalous");
        anomalies[0].tag.Should().Be("proxy-B");
    }

    [Fact]
    public async Task NodeNotInState_IgnoresUnknownTag()
    {
        var node = CreateNode("proxy-X", score: 80);
        var fakeClient = new FakeXrayStatsClient();
        var poller = new XrayStatsPoller(fakeClient, new[] { node });

        var anomalies = new List<(string tag, double bps)>();
        poller.ThroughputAnomalyDetected += (tag, bps) => anomalies.Add((tag, bps));

        fakeClient.SetStats(new Dictionary<string, long>(StringComparer.Ordinal)
        {
            ["proxy-X"] = 10_000,
            ["proxy-unknown"] = 5_000,
        });
        await poller.TriggerPollAsync();

        fakeClient.SetStats(new Dictionary<string, long>(StringComparer.Ordinal)
        {
            ["proxy-X"] = 10_512, // 102 B/s → anomaly
            ["proxy-unknown"] = 5_512,
        });
        await poller.TriggerPollAsync();

        anomalies.Count.Should().Be(1, "only proxy-X is tracked");
        anomalies[0].tag.Should().Be("proxy-X");
    }
}
