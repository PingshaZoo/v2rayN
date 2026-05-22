using AwesomeAssertions;
using ServiceLib.Handler.AdaptiveNodeScheduler;
using ServiceLib.Models.CoreConfigs;
using Xunit;

namespace ServiceLib.Tests.AdaptiveNodeScheduler;

/// <summary>
/// P3.1: Verifies RuntimePolicyApplier fallback and diff logic.
/// Uses FakeXrayHandlerClient to control API availability.
/// </summary>
public class RuntimePolicyApplierTests
{
    private sealed class FakeXrayHandlerClient : IXrayHandlerClient
    {
        public bool IsAvailable { get; set; }
        public List<string> AddedTags { get; } = new();
        public List<string> RemovedTags { get; } = new();

        public Task<bool> IsAvailableAsync() => Task.FromResult(IsAvailable);

        public Task<bool> AddOutboundAsync(string tag, string host, int port, CancellationToken ct = default)
        {
            AddedTags.Add(tag);
            return Task.FromResult(true);
        }

        public Task<bool> RemoveOutboundAsync(string tag, CancellationToken ct = default)
        {
            RemovedTags.Add(tag);
            return Task.FromResult(true);
        }
    }

    private sealed class FakeAdaptivePolicyApplier : IAdaptivePolicyApplier
    {
        public List<AdaptiveConfig> AppliedConfigs { get; } = new();
        public bool Disposed { get; private set; }

        public Task ApplyAsync(AdaptiveConfig config, CancellationToken ct = default)
        {
            AppliedConfigs.Add(config);
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

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

    // ── Fallback path ──────────────────────────────────────────

    [Fact]
    public async Task ApiUnavailable_FallsBackToFallbackApplier()
    {
        var client = new FakeXrayHandlerClient { IsAvailable = false };
        var fallback = new FakeAdaptivePolicyApplier();
        var applier = new RuntimePolicyApplier(client, fallback);

        var config = CreateConfig("A", "B");
        await applier.ApplyAsync(config);

        fallback.AppliedConfigs.Count.Should().Be(1, "API unavailable → fallback called once");
        fallback.AppliedConfigs[0].ActiveTags.Should().Contain(["A", "B"]);
    }

    [Fact]
    public async Task ApiUnavailable_DoesNotCallAddOrRemove()
    {
        var client = new FakeXrayHandlerClient { IsAvailable = false };
        var fallback = new FakeAdaptivePolicyApplier();
        var applier = new RuntimePolicyApplier(client, fallback);

        await applier.ApplyAsync(CreateConfig("A"));

        client.AddedTags.Should().BeEmpty("API unavailable → no AddOutbound calls");
        client.RemovedTags.Should().BeEmpty("API unavailable → no RemoveOutbound calls");
    }

    [Fact]
    public async Task TwoSuccessiveFallsbackCalls_EachReachesFallback()
    {
        var client = new FakeXrayHandlerClient { IsAvailable = false };
        var fallback = new FakeAdaptivePolicyApplier();
        var applier = new RuntimePolicyApplier(client, fallback);

        await applier.ApplyAsync(CreateConfig("A", "B"));
        await applier.ApplyAsync(CreateConfig("B", "C"));

        fallback.AppliedConfigs.Count.Should().Be(2);
    }

    // ── Runtime path ───────────────────────────────────────────

    [Fact]
    public async Task ApiAvailable_FirstCall_AddsAllActiveTags()
    {
        var client = new FakeXrayHandlerClient { IsAvailable = true };
        var fallback = new FakeAdaptivePolicyApplier();
        var applier = new RuntimePolicyApplier(client, fallback);

        await applier.ApplyAsync(CreateConfig("A", "B", "C"));

        client.AddedTags.Should().Contain(["A", "B", "C"]);
        client.RemovedTags.Should().BeEmpty("first call has no previous active set");
        fallback.AppliedConfigs.Should().BeEmpty("API available → no fallback");
    }

    [Fact]
    public async Task ApiAvailable_SecondCall_DiffsActiveSet()
    {
        var client = new FakeXrayHandlerClient { IsAvailable = true };
        var fallback = new FakeAdaptivePolicyApplier();
        var applier = new RuntimePolicyApplier(client, fallback);

        // First: A, B, C
        await applier.ApplyAsync(CreateConfig("A", "B", "C"));
        client.AddedTags.Clear();

        // Second: B, C, D — A leaves, D enters
        await applier.ApplyAsync(CreateConfig("B", "C", "D"));

        client.AddedTags.Should().ContainSingle().Which.Should().Be("D");
        client.RemovedTags.Should().ContainSingle().Which.Should().Be("A");
        fallback.AppliedConfigs.Should().BeEmpty("API available → no fallback");
    }

    [Fact]
    public async Task ApiAvailable_NoChange_NoApiCalls()
    {
        var client = new FakeXrayHandlerClient { IsAvailable = true };
        var fallback = new FakeAdaptivePolicyApplier();
        var applier = new RuntimePolicyApplier(client, fallback);

        await applier.ApplyAsync(CreateConfig("A", "B"));
        client.AddedTags.Clear();
        client.RemovedTags.Clear();

        // Same active set — nothing should change
        await applier.ApplyAsync(CreateConfig("A", "B"));

        client.AddedTags.Should().BeEmpty("no new tags");
        client.RemovedTags.Should().BeEmpty("no removed tags");
        fallback.AppliedConfigs.Should().BeEmpty("no fallback needed");
    }

    [Fact]
    public async Task ApiAvailable_ClearActiveSet_RemovesAll()
    {
        var client = new FakeXrayHandlerClient { IsAvailable = true };
        var fallback = new FakeAdaptivePolicyApplier();
        var applier = new RuntimePolicyApplier(client, fallback);

        await applier.ApplyAsync(CreateConfig("A", "B", "C"));
        client.AddedTags.Clear();
        client.RemovedTags.Clear();

        // Empty active set — remove everything
        await applier.ApplyAsync(CreateConfig());

        client.AddedTags.Should().BeEmpty("no new tags");
        client.RemovedTags.Should().Contain(["A", "B", "C"]);
    }

    [Fact]
    public async Task ApiAvailable_DisjointSet_ReplacesAll()
    {
        var client = new FakeXrayHandlerClient { IsAvailable = true };
        var fallback = new FakeAdaptivePolicyApplier();
        var applier = new RuntimePolicyApplier(client, fallback);

        await applier.ApplyAsync(CreateConfig("A", "B"));
        client.AddedTags.Clear();

        // Completely different set
        await applier.ApplyAsync(CreateConfig("X", "Y", "Z"));

        client.AddedTags.Should().Contain(["X", "Y", "Z"]);
        client.RemovedTags.Should().Contain(["A", "B"]);
    }

    // ── Disposal ───────────────────────────────────────────────

    [Fact]
    public async Task Dispose_DisposesFallbackApplier()
    {
        var client = new FakeXrayHandlerClient { IsAvailable = false };
        var fallback = new FakeAdaptivePolicyApplier();
        var applier = new RuntimePolicyApplier(client, fallback);

        await applier.DisposeAsync();

        fallback.Disposed.Should().BeTrue("dispose must propagate to fallback");
    }

    [Fact]
    public async Task DisposedApplier_ApplyAsyncIsNoOp()
    {
        var client = new FakeXrayHandlerClient { IsAvailable = true };
        var fallback = new FakeAdaptivePolicyApplier();
        var applier = new RuntimePolicyApplier(client, fallback);

        await applier.DisposeAsync();

        await applier.ApplyAsync(CreateConfig("A"));

        client.AddedTags.Should().BeEmpty("disposed → no-op");
        fallback.AppliedConfigs.Should().BeEmpty("disposed → no-op");
    }

    [Fact]
    public async Task DoubleDispose_DoesNotThrow()
    {
        var client = new FakeXrayHandlerClient();
        var fallback = new FakeAdaptivePolicyApplier();
        var applier = new RuntimePolicyApplier(client, fallback);

        await applier.DisposeAsync();
        var act = () => applier.DisposeAsync().AsTask();
        await act.Should().NotThrowAsync();
    }
}
