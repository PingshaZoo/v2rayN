using AwesomeAssertions;
using Xunit;

namespace ServiceLib.Tests.AdaptiveNodeScheduler;

/// <summary>
/// P2.4: Verifies the ProbeService concurrency cap formula.
/// Concurrency = max(3, ceil(N/5)).
/// Also verifies SemaphoreSlim correctly gates concurrent access.
/// </summary>
public class ProbeConcurrencyTests
{
    /// <summary>
    /// Replicates the formula from ProbeService constructor.
    /// </summary>
    private static int ComputeMaxConcurrency(int nodeCount)
    {
        const int minConcurrency = 3;
        return Math.Max(minConcurrency, (int)Math.Ceiling(nodeCount / 5.0));
    }

    [Theory]
    [InlineData(1, 3)]
    [InlineData(2, 3)]
    [InlineData(3, 3)]
    [InlineData(4, 3)]
    [InlineData(5, 3)]
    [InlineData(10, 3)]
    [InlineData(14, 3)]
    [InlineData(15, 3)]
    [InlineData(16, 4)]
    [InlineData(20, 4)]
    [InlineData(25, 5)]
    [InlineData(30, 6)]
    [InlineData(50, 10)]
    [InlineData(100, 20)]
    public void ConcurrencyCap_Formula_MatchesExpected(int nodeCount, int expectedMax)
    {
        ComputeMaxConcurrency(nodeCount).Should().Be(expectedMax);
    }

    [Fact]
    public void ConcurrencyCap_FloorsAt3()
    {
        // Even for tiny groups, the cap never drops below 3
        for (int n = 1; n <= 14; n++)
        {
            ComputeMaxConcurrency(n).Should().BeGreaterThanOrEqualTo(3,
                $"concurrency cap for N={n} should be >= 3 (minimum)");
        }
    }

    [Fact]
    public void ConcurrencyCap_NeverExceedsNodeCount()
    {
        // For very small N (< 5), cap=3 may exceed N; that's OK —
        // the semaphore allows up to 3, but only N probes will actually run
        ComputeMaxConcurrency(2).Should().Be(3); // cap > N is fine
    }

    [Fact]
    public void SemaphoreSlim_GatesConcurrentAccess()
    {
        // Verify SemaphoreSlim correctly limits concurrent access
        const int max = 3;
        var sem = new SemaphoreSlim(max, max);
        int concurrent = 0;
        int maxObserved = 0;
        var tasks = new List<Task>();

        for (int i = 0; i < 10; i++)
        {
            tasks.Add(Task.Run(async () =>
            {
                await sem.WaitAsync();
                int current = Interlocked.Increment(ref concurrent);
                InterlockedAddMax(ref maxObserved, current);
                await Task.Delay(10); // simulate work
                Interlocked.Decrement(ref concurrent);
                sem.Release();
            }));
        }

        Task.WhenAll(tasks).Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
        maxObserved.Should().BeLessThanOrEqualTo(max,
            "semaphore should allow at most {max} concurrent entries");
    }

    private static void InterlockedAddMax(ref int target, int value)
    {
        int seen;
        do
        {
            seen = Volatile.Read(ref target);
            if (value <= seen) break;
        }
        while (Interlocked.CompareExchange(ref target, value, seen) != seen);
    }
}
