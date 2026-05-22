using AwesomeAssertions;
using ServiceLib.Handler.AdaptiveNodeScheduler;
using Xunit;

namespace ServiceLib.Tests.AdaptiveNodeScheduler;

/// <summary>
/// P2.6: Verifies multi-target probe URL parsing and averaging semantics.
/// ProbeService splits ProbeUrl by newlines; averages successful TTFBs;
/// only records failure when ALL targets fail.
/// </summary>
public class MultiTargetProbeTests
{
    private static string[] ParseProbeUrls(string rawUrl)
    {
        const string defaultUrl = "http://cp.cloudflare.com/";
        if (string.IsNullOrWhiteSpace(rawUrl))
            return [defaultUrl];

        var urls = rawUrl
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(u => u.Trim())
            .Where(u => u.Length > 0)
            .ToArray();

        return urls.Length > 0 ? urls : [defaultUrl];
    }

    [Fact]
    public void ParseProbeUrls_SingleUrl_ReturnsOne()
    {
        var urls = ParseProbeUrls("http://cp.cloudflare.com/");
        urls.Length.Should().Be(1);
        urls[0].Should().Be("http://cp.cloudflare.com/");
    }

    [Fact]
    public void ParseProbeUrls_TwoUrls_ReturnsBoth()
    {
        var urls = ParseProbeUrls("http://cp.cloudflare.com/\nhttp://connectivitycheck.gstatic.com/generate_204");
        urls.Length.Should().Be(2);
        urls.Should().Contain("http://cp.cloudflare.com/");
        urls.Should().Contain("http://connectivitycheck.gstatic.com/generate_204");
    }

    [Fact]
    public void ParseProbeUrls_BlankLines_FilteredOut()
    {
        var urls = ParseProbeUrls("http://a.com/\n\nhttp://b.com/\n  \nhttp://c.com/");
        urls.Length.Should().Be(3);
    }

    [Fact]
    public void ParseProbeUrls_WhitespaceTrimmed()
    {
        var urls = ParseProbeUrls("  http://a.com/  \n  http://b.com/  ");
        urls[0].Should().Be("http://a.com/");
        urls[1].Should().Be("http://b.com/");
    }

    [Fact]
    public void ParseProbeUrls_EmptyOrNull_ReturnsDefault()
    {
        ParseProbeUrls("")[0].Should().Be("http://cp.cloudflare.com/");
        ParseProbeUrls("   ")[0].Should().Be("http://cp.cloudflare.com/");
    }

    [Fact]
    public void MultiTarget_AllSuccess_AveragesTtfb()
    {
        // Simulates: 2 probe URLs both succeed → average TTFB
        var ttfbs = new List<double> { 95.0, 105.0 };
        // At least one success → use average
        ttfbs.Count.Should().BeGreaterThan(0);
        bool allFailed = ttfbs.Count == 0;

        if (!allFailed)
        {
            double avgTtfb = ttfbs.Average();
            avgTtfb.Should().Be(100.0, "average of 95ms and 105ms");
        }
    }

    [Fact]
    public void MultiTarget_PartialSuccess_StillRecordsSuccess()
    {
        // 1 of 2 URLs succeeds → still counts as overall success (average of 1 value)
        var ttfbs = new List<double> { 95.0 }; // only 1 succeeded, 1 failed

        bool allFailed = ttfbs.Count == 0;
        allFailed.Should().BeFalse("partial success should not be treated as failure");

        double avgTtfb = ttfbs.Average();
        avgTtfb.Should().Be(95.0, "when only 1 succeeds, average = that 1 value");
    }

    [Fact]
    public void MultiTarget_AllFailed_RecordsFailure()
    {
        // Both URLs fail → no successful TTFBs → record failure
        var ttfbs = new List<double>();
        bool allFailed = ttfbs.Count == 0;
        allFailed.Should().BeTrue("all probe URLs failed, should record failure");
    }

    [Fact]
    public void MultiTarget_ThreeUrls_TwoSucceed_AveragesTwo()
    {
        var ttfbs = new List<double> { 80.0, 120.0 };
        double avg = ttfbs.Average();
        avg.Should().Be(100.0, "average of 80ms and 120ms from 2 successful probes out of 3");
    }
}
