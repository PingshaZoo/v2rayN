using System.Text.Json;
using AwesomeAssertions;
using ServiceLib.Handler.AdaptiveNodeScheduler;
using Xunit;

namespace ServiceLib.Tests.AdaptiveNodeScheduler;

/// <summary>
/// P2.5: Verifies that FailureCollector emits complete replayable telemetry
/// events (probe_result + ewma_update) through ScoreLogger.
/// </summary>
public class ReplayableTelemetryTests
{
    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private static NodeState CreateNode(string tag = "N1", double score = 50)
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

    private static (FailureCollector collector, string logPath) CreateCollectorWithLogger(NodeState node)
    {
        var logPath = Path.GetTempFileName();
        var scorer = new ScoreCalculator();
        var cooldown = new CooldownFsm();
        var logger = new ScoreLogger(new[] { node }, logPath);
        var collector = new FailureCollector(scorer, cooldown, logger);
        return (collector, logPath);
    }

    private static List<Dictionary<string, JsonElement>> ReadJsonlLines(string path)
    {
        var lines = new List<Dictionary<string, JsonElement>>();
        foreach (var line in File.ReadAllLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var parsed = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(line, ReadOptions);
            if (parsed != null)
                lines.Add(parsed);
        }
        return lines;
    }

    [Fact]
    public void RecordSuccess_Emits_ProbeResult_And_EwmaUpdate()
    {
        var node = CreateNode("A");
        var (collector, logPath) = CreateCollectorWithLogger(node);

        collector.RecordSuccess(node, 150.0);

        var events = ReadJsonlLines(logPath);
        events.Count.Should().Be(2, "RecordSuccess emits two events: probe_result + ewma_update");

        events[0]["type"].GetString().Should().Be("probe_result");
        events[0]["node"].GetString().Should().Be("A");
        events[0]["success"].GetBoolean().Should().BeTrue();

        events[1]["type"].GetString().Should().Be("ewma_update");
        events[1]["node"].GetString().Should().Be("A");
        events[1].Should().ContainKey("old_latency_ms");
        events[1].Should().ContainKey("new_latency_ms");
        events[1].Should().ContainKey("alpha");
        events[1].Should().ContainKey("old_score");
        events[1].Should().ContainKey("new_score");

        try { File.Delete(logPath); } catch { /* best-effort */ }
    }

    [Fact]
    public void RecordFailure_Emits_ProbeResult_And_EwmaUpdate_WithFailureFields()
    {
        var node = CreateNode("B");
        var (collector, logPath) = CreateCollectorWithLogger(node);
        var allNodes = new List<NodeState> { node };

        collector.RecordFailure(node, FailureType.Timeout, allNodes);

        var events = ReadJsonlLines(logPath);
        events.Count.Should().Be(2, "RecordFailure emits probe_result + ewma_update");

        events[0]["type"].GetString().Should().Be("probe_result");
        events[0]["success"].GetBoolean().Should().BeFalse();
        events[0]["failure_type"].GetString().Should().Be("timeout");
        events[0].Should().ContainKey("penalty_loss");
        events[0].Should().ContainKey("penalty_latency_ms");

        events[1]["type"].GetString().Should().Be("ewma_update");
        events[1].Should().ContainKey("consecutive_failures");
        events[1].Should().ContainKey("in_cooldown");

        try { File.Delete(logPath); } catch { /* best-effort */ }
    }

    [Fact]
    public void TlsError_Emits_OnlyProbeResult_NoEwmaUpdate()
    {
        var node = CreateNode("C");
        var (collector, logPath) = CreateCollectorWithLogger(node);
        var allNodes = new List<NodeState> { node };

        collector.RecordFailure(node, FailureType.TlsError, allNodes);

        var events = ReadJsonlLines(logPath);
        events.Count.Should().Be(1, "TlsError returns early — only probe_result, no ewma_update");
        events[0]["type"].GetString().Should().Be("probe_result");
        events[0]["success"].GetBoolean().Should().BeFalse();
        events[0]["failure_type"].GetString().Should().Be("tlserror");
        events[0]["note"].GetString().Should().Contain("no penalty");

        try { File.Delete(logPath); } catch { /* best-effort */ }
    }

    [Fact]
    public void NullLogger_DoesNotThrow_OnRecordSuccess()
    {
        var node = CreateNode("D");
        var scorer = new ScoreCalculator();
        var cooldown = new CooldownFsm();
        var collector = new FailureCollector(scorer, cooldown); // no logger

        var act = () => collector.RecordSuccess(node, 100.0);
        act.Should().NotThrow("null logger must be a no-op, not a NullReferenceException");
    }

    [Fact]
    public void NullLogger_DoesNotThrow_OnRecordFailure()
    {
        var node = CreateNode("E");
        var scorer = new ScoreCalculator();
        var cooldown = new CooldownFsm();
        var collector = new FailureCollector(scorer, cooldown);
        var allNodes = new List<NodeState> { node };

        var act = () => collector.RecordFailure(node, FailureType.Refused, allNodes);
        act.Should().NotThrow("null logger must be a no-op, not a NullReferenceException");
    }

    [Fact]
    public void NullLogger_DoesNotThrow_OnTlsError()
    {
        var node = CreateNode("F");
        var scorer = new ScoreCalculator();
        var cooldown = new CooldownFsm();
        var collector = new FailureCollector(scorer, cooldown);
        var allNodes = new List<NodeState> { node };

        var act = () => collector.RecordFailure(node, FailureType.TlsError, allNodes);
        act.Should().NotThrow("TlsError with null logger must not throw");
    }

    [Fact]
    public void RecordSuccess_AllFields_HaveExpectedTypes()
    {
        var node = CreateNode("G");
        var (collector, logPath) = CreateCollectorWithLogger(node);

        collector.RecordSuccess(node, 250.5);

        var events = ReadJsonlLines(logPath);

        events[0]["ttfb_ms"].ValueKind.Should().Be(JsonValueKind.String);

        events[1]["alpha"].ValueKind.Should().Be(JsonValueKind.String);
        events[1]["old_score"].ValueKind.Should().Be(JsonValueKind.String);
        events[1]["new_score"].ValueKind.Should().Be(JsonValueKind.String);

        try { File.Delete(logPath); } catch { /* best-effort */ }
    }

    [Fact]
    public void RecordFailure_RefusedType_EmitsCorrectFailureType()
    {
        var node = CreateNode("H");
        var (collector, logPath) = CreateCollectorWithLogger(node);
        var allNodes = new List<NodeState> { node };

        collector.RecordFailure(node, FailureType.Refused, allNodes);

        var events = ReadJsonlLines(logPath);
        events[0]["failure_type"].GetString().Should().Be("refused");
        events[0]["penalty_loss"].GetString().Should().Be("1.00");

        try { File.Delete(logPath); } catch { /* best-effort */ }
    }

    [Fact]
    public void MultipleCalls_ProduceSequentialJsonlLines()
    {
        var node = CreateNode("I");
        var (collector, logPath) = CreateCollectorWithLogger(node);

        collector.RecordSuccess(node, 120.0);
        collector.RecordSuccess(node, 110.0);
        collector.RecordSuccess(node, 130.0);

        var events = ReadJsonlLines(logPath);
        events.Count.Should().Be(6, "3 successes × 2 events = 6 JSONL lines");

        // All six lines must have "time" and "type" fields
        foreach (var evt in events)
        {
            evt.Should().ContainKey("time");
            evt.Should().ContainKey("type");
        }

        try { File.Delete(logPath); } catch { /* best-effort */ }
    }
}
