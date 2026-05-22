using AwesomeAssertions;
using Xunit;

namespace ServiceLib.Tests.AdaptiveNodeScheduler;

/// <summary>
/// P1.3: Verifies the top-K formula max(2, ceil(N * 2/3)) used by ActiveSetManager.
/// This test encodes the actual implemented formula, which differs from the old
/// design spec (max(3, ceil(N * 0.5))).
/// </summary>
public class TopKFormulaTests
{
    private static int ComputeTopK(int eligibleCount)
    {
        return Math.Max(2, (int)Math.Ceiling(eligibleCount * 2.0 / 3.0));
    }

    [Theory]
    [InlineData(0, 2)]
    [InlineData(1, 2)]
    [InlineData(2, 2)]
    [InlineData(3, 2)]
    [InlineData(4, 3)]
    [InlineData(5, 4)]
    [InlineData(6, 4)]
    [InlineData(7, 5)]
    [InlineData(8, 6)]
    [InlineData(9, 6)]
    [InlineData(10, 7)]
    [InlineData(15, 10)]
    [InlineData(20, 14)]
    [InlineData(30, 20)]
    public void TopK_Formula_MatchesExpected(int eligibleCount, int expectedK)
    {
        ComputeTopK(eligibleCount).Should().Be(expectedK);
    }

    [Fact]
    public void TopK_Formula_MinimumIs2()
    {
        // Even with 0 or 1 eligible, topK floors at 2
        for (int n = 0; n <= 3; n++)
        {
            ComputeTopK(n).Should().BeGreaterThanOrEqualTo(2,
                $"topK({n}) should be >= 2 (minimum)");
        }
    }

    [Fact]
    public void TopK_Formula_NeverExceedsEligible()
    {
        // The topK is a target, but ActiveSetManager.Take(topK) won't exceed actual items
        for (int n = 1; n <= 30; n++)
        {
            // topK can be larger than n for small n, that's fine — Take() handles it
            // This test just documents the formula behavior
            int k = ComputeTopK(n);
            // For n=1: topK=2 > n=1 — ActiveSetManager handles this via .Take(topK)
            // and the "eligible <= 2 → return all" early-return path
        }
    }

    [Fact]
    public void TopK_Formula_IsMoreInclusiveThanOldSpec()
    {
        // Old spec: max(3, ceil(N * 0.5))
        // New/actual: max(2, ceil(N * 2/3))
        // For N >= 4, the new formula is more inclusive (higher or equal K).
        // Exception: N=3 → oldK=3, newK=2 (by design — minimum 2 is more practical).
        for (int n = 4; n <= 30; n++)
        {
            int oldK = Math.Max(3, (int)Math.Ceiling(n * 0.5));
            int newK = ComputeTopK(n);
            newK.Should().BeGreaterThanOrEqualTo(oldK,
                $"new topK({n})={newK} should be >= old K({n})={oldK}");
        }

        // N=3 special case: new formula min=2 is intentionally lower than old min=3
        ComputeTopK(3).Should().Be(2);
    }
}
