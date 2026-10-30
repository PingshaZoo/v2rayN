using ServiceLib.Handler.CloudflareBestIP;

namespace ServiceLib.Tests.Handler;

public class CloudflareBestIpTests
{
    [Test]
    public async Task ProbeLogWritesMatchingOrderedLinesToBothSinks()
    {
        var history = new List<string>();
        var file = new List<string>();
        var log = new CfProbeLog(history.Add, file.Add);
        await Task.WhenAll(Enumerable.Range(0, 100).Select(i => Task.Run(() => log.Write($"result {i}\r\nnext"))));
        await history.Count.Should().BeEqualTo(100);
        await history.SequenceEqual(file).Should().BeTrue();
        await history.All(line => line.Contains("[CloudflareBestIp]") && !line.Contains('\n') && !line.Contains('\r')).Should().BeTrue();
    }

    [Test]
    public async Task FailedSourcesAndCidrFallbackAreVisibleInLogs()
    {
        var logs = new ConcurrentBag<string>();
        var settings = new CfBestIpItem
        {
            HavePostRes = ["https://test.local/history"],
            IpSetUrls = ["https://test.local/ips"],
            DomainsSetUrl = "https://test.local/domains"
        };
        using var client = new HttpClient(new FakeHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
        using var fetcher = new CfDataFetcher(settings, client, logs.Add);
        var history = await fetcher.FetchHistoricalIpsAsync(logs.Add);
        var general = await fetcher.FetchGeneralIpsAsync([], logs.Add);
        await history.Count.Should().BeEqualTo(0);
        await general.Count.Should().BeEqualTo(0);
        foreach (var expected in new[] { "built-in fallback", "/history: ", "/ips: IP source failed", "/domains: domain source failed", "Final IP count" })
            await logs.Any(line => line.Contains(expected)).Should().BeTrue();
    }

    [Test]
    public async Task CancelledProbeRetainsStartAndStopHistory()
    {
        var logs = new List<string>();
        var config = new Config
        {
            CfBestIpItem = new CfBestIpItem
            {
                OriginSniList = ["test.local"], OriginTestPath = "/ok", OriginSpeedTestPath = "/speed",
                IpSetUrls = ["https://test.local/ips"]
            }
        };
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var didCancel = false;
        try { await new CfBestIpHandler().RunAsync(config, cancellationToken: cancelled.Token, logProgress: new ImmediateProgress(logs.Add)); }
        catch (OperationCanceledException) { didCancel = true; }
        await didCancel.Should().BeTrue();
        await logs.Count.Should().BeGreaterThan(1);
        await logs.Any(line => line.Contains("Probe stopped") || line.Contains("测速已停止")).Should().BeTrue();
    }

    private sealed class ImmediateProgress(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }

    [Test]
    public async Task FreshConfigurationContainsOnlyPublicCidrs()
    {
        var config = new CfBestIpItem();
        await config.HavePostRes.Count.Should().BeEqualTo(0);
        await config.IpSetUrls.Count.Should().BeEqualTo(0);
        await config.PostUrls.Count.Should().BeEqualTo(0);
        await string.IsNullOrWhiteSpace(config.DomainsSetUrl).Should().BeTrue();
        await Global.CfDefaultIpv4Cidrs.Count.Should().BeGreaterThan(0);
    }

    [Test]
    public async Task HistoricalFeedsMergeDeduplicateBySpeedAndFilterCidr()
    {
        var settings = new CfBestIpItem
        {
            HavePostRes = ["https://test.local/history"],
            PostUrls = ["https://test.local/post"]
        };
        using var client = new HttpClient(new FakeHttpHandler(request =>
        {
            if (request.RequestUri!.Host == "api.cloudflare.com")
                return new HttpResponseMessage(HttpStatusCode.OK)
                    { Content = new StringContent("""{"result":{"ipv4_cidrs":["104.16.0.0/13"]}}""") };
            var body = request.RequestUri.AbsolutePath == "/history"
                ? """[{"ip":"104.16.0.1","speed_kb_s":10},{"ip":"104.16.0.2","speed_kb_s":"bad"},{"ip":"8.8.8.8","speed_kb_s":999},{"ip":null},{"ip":"104.16.0.3","speed_kb_s":20}]"""
                : """[{"ip":"104.16.0.1","speed_kb_s":30},{"ip":"104.16.0.4","speed_kb_s":40}]""";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
        }));
        using var fetcher = new CfDataFetcher(settings, client);
        var result = await fetcher.FetchHistoricalIpsAsync();
        await result.Count.Should().BeEqualTo(4);
        await result[0].Ip.Should().BeEqualTo("104.16.0.4");
        await result[1].Ip.Should().BeEqualTo("104.16.0.1");
        await result[1].HistoricalSpeedKBs.Should().BeEqualTo(30);
        await result.All(x => x.Ip != "8.8.8.8").Should().BeTrue();
    }

    [Test]
    public async Task PublicCidrFallbackWorksWhenOfficialApiIsUnavailable()
    {
        var settings = new CfBestIpItem { HavePostRes = ["https://test.local/history"] };
        using var client = new HttpClient(new FakeHttpHandler(request =>
            request.RequestUri!.Host == "api.cloudflare.com"
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                : new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent("""[{"ip":"104.16.0.1","speed_kb_s":850}]""") }));
        using var fetcher = new CfDataFetcher(settings, client);
        var result = await fetcher.FetchHistoricalIpsAsync();
        await result.Count.Should().BeEqualTo(1);
        await result[0].Ip.Should().BeEqualTo("104.16.0.1");
    }

    [Test]
    public async Task RankingRequiresRealSpeedAndKeepsHttpLossPenalty()
    {
        var settings = new CfBestIpItem { LowestSpeed = 500, TopN = 2 };
        var scorer = new CfIpScorer(settings);
        var slow = Result("104.16.0.1", 400, 20, 0);
        var fast = Result("104.16.0.2", 900, 40, .5);
        var fastest = Result("104.16.0.3", 1100, 30, 0);
        var untested = Result("104.16.0.4", 9999, 10, 0);
        untested.SpeedTested = false;
        var top = scorer.ScoreAndRank([slow, fast, fastest, untested]);
        await top.Count.Should().BeEqualTo(2);
        await top[0].Ip.Should().BeEqualTo(fastest.Ip);
        await top[1].Ip.Should().BeEqualTo(fast.Ip);
        await fast.Score.Should().BeEqualTo(2);
        await slow.Qualified.Should().BeFalse();
        await untested.Qualified.Should().BeFalse();
        await (scorer.LatencyScore(fast) > scorer.LatencyScore(fastest)).Should().BeTrue();
    }

    [Test]
    public async Task LegacyImportSettingsAreIgnoredWithoutLosingProbeSettings()
    {
        const string legacy = """
            {"TopN":7,"OriginSniList":["example.com"],"PostUrls":["https://example.com/report"],
             "TemplateIndexId":"old-template","SelectedProtocol":"vless","Uuid":"old-credential","WsPath":"/ws"}
            """;
        var settings = JsonUtils.Deserialize<CfBestIpItem>(legacy)!;
        await settings.TopN.Should().BeEqualTo(7);
        await settings.OriginSniList.Should().Contain("example.com");
        await settings.PostUrls.Should().Contain("https://example.com/report");

        var saved = JsonUtils.Serialize(settings);
        foreach (var oldKey in new[] { "TemplateIndexId", "SelectedProtocol", "Uuid", "WsPath" })
            await saved.Contains($"\"{oldKey}\"", StringComparison.Ordinal).Should().BeFalse();
    }

    [Test]
    public async Task CandidateSelectionKeepsRegionalQuotas()
    {
        var settings = new CfBestIpItem();
        var scorer = new CfIpScorer(settings);
        var results = Enumerable.Range(1, 25).Select(i =>
        {
            var r = Result($"104.16.0.{i}", 0, i, 0);
            r.Region = i <= 10 ? "NorthAmerica" : i <= 20 ? "HONGKONG" : "EastAsia";
            return r;
        }).ToList();
        var chosen = CfBestIpHandler.SelectSpeedCandidates(results, scorer);
        await chosen.Count.Should().BeEqualTo(15);
        await chosen.Count(x => x.Region == "NorthAmerica").Should().BeEqualTo(5);
        await chosen.Count(x => x.Region == "HONGKONG").Should().BeEqualTo(5);
        await chosen.Count(x => x.Region == "EastAsia").Should().BeEqualTo(5);
    }

    private static CfProbeResult Result(string ip, double speed, double latency, double httpLoss) => new()
    {
        Ip = ip, Sni = "example.com", Colo = "HKG", AvgLatencyMs = latency,
        DownloadSpeedKBs = speed, SpeedTested = true, HttpLossRate = httpLoss
    };

    private sealed class FakeHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(respond(request));
        }
    }
}
