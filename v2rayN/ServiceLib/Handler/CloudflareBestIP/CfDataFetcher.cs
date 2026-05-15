using System.Net;

namespace ServiceLib.Handler.CloudflareBestIP;

public class CfDataFetcher
{
    private readonly CfBestIpItem _config;
    private readonly HttpClient _httpClient;
    private static readonly Regex _ipRegex = new(
        @"\b((?:(?:25[0-5]|2[0-4]\d|[01]?\d\d?)\.){3}(?:25[0-5]|2[0-4]\d|[01]?\d\d?))\b",
        RegexOptions.Compiled);

    public CfDataFetcher(CfBestIpItem config)
    {
        _config = config;
        _httpClient = new HttpClient();
        _httpClient.Timeout = TimeSpan.FromSeconds(30);
    }

    public async Task<List<string>> FetchAllIpsAsync(Config config, Action<string>? onProgress = null)
    {
        var allIps = new List<string>();
        var seen = new HashSet<string>();

        // Priority-1: Historical good results
        onProgress?.Invoke("Fetching historical good results...");
        foreach (var url in _config.HavePostRes)
        {
            if (url.IsNullOrEmpty()) continue;
            var ips = await FetchIpsFromUrlAsync(url);
            foreach (var ip in ips)
            {
                if (seen.Add(ip)) allIps.Add(ip);
            }
        }

        // Priority-2: Third-party IP libraries
        onProgress?.Invoke("Fetching third-party IP sources...");
        var fetches = _config.IpSetUrls
            .Where(u => u.IsNotEmpty())
            .Select(FetchIpsFromUrlAsync)
            .ToArray();
        var results = await Task.WhenAll(fetches);
        foreach (var ips in results)
        {
            foreach (var ip in ips)
            {
                if (seen.Add(ip)) allIps.Add(ip);
            }
        }

        // Priority-3: Domain list → DNS resolve → CF CIDR filter
        if (_config.DomainsSetUrl.IsNotEmpty())
        {
            onProgress?.Invoke("Fetching and resolving domains...");
            var domains = await FetchDomainsAsync(_config.DomainsSetUrl);
            var resolved = await ResolveDomainsConcurrentAsync(domains);
            foreach (var ip in resolved)
            {
                if (seen.Add(ip)) allIps.Add(ip);
            }
        }

        // CF CIDR filter
        onProgress?.Invoke($"Filtering {allIps.Count} IPs against Cloudflare CIDRs...");
        var cidrs = await FetchCfCidrsAsync();
        allIps = FilterByCidrs(allIps, cidrs);

        onProgress?.Invoke($"Final IP count after dedup & filter: {allIps.Count}");
        return allIps;
    }

    private async Task<List<string>> FetchIpsFromUrlAsync(string url)
    {
        try
        {
            var content = await _httpClient.GetStringAsync(url);
            return _ipRegex.Matches(content)
                .Select(m => m.Value)
                .Distinct()
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    private async Task<List<string>> FetchDomainsAsync(string url)
    {
        try
        {
            var content = await _httpClient.GetStringAsync(url);
            var json = JsonNode.Parse(content);
            var domains = new List<string>();

            if (json is JsonArray arr)
            {
                foreach (var item in arr)
                {
                    var domain = item?.GetValue<string>();
                    if (domain.IsNotEmpty()) domains.Add(domain);
                }
            }
            else if (json is JsonObject obj && obj.TryGetPropertyValue("domains", out var doms) && doms is JsonArray darr)
            {
                foreach (var item in darr)
                {
                    var domain = item?.GetValue<string>();
                    if (domain.IsNotEmpty()) domains.Add(domain);
                }
            }

            return domains;
        }
        catch
        {
            return [];
        }
    }

    private async Task<List<string>> ResolveDomainsConcurrentAsync(List<string> domains)
    {
        var allIps = new ConcurrentBag<string>();
        var semaphore = new SemaphoreSlim(20);

        var tasks = domains.Select(async domain =>
        {
            await semaphore.WaitAsync();
            try
            {
                var entries = await Dns.GetHostEntryAsync(domain);
                foreach (var addr in entries.AddressList)
                {
                    if (addr.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                    {
                        allIps.Add(addr.ToString());
                    }
                }
            }
            catch { }
            finally
            {
                semaphore.Release();
            }
        });

        await Task.WhenAll(tasks);
        return allIps.Distinct().ToList();
    }

    private async Task<List<string>> FetchCfCidrsAsync()
    {
        try
        {
            var content = await _httpClient.GetStringAsync("https://api.cloudflare.com/client/v4/ips");
            var json = JsonNode.Parse(content);
            var cidrs = new List<string>();

            var ipv4Cidrs = json?["result"]?["ipv4_cidrs"];
            if (ipv4Cidrs is JsonArray arr)
            {
                foreach (var item in arr)
                {
                    var cidr = item?.GetValue<string>();
                    if (cidr.IsNotEmpty()) cidrs.Add(cidr);
                }
            }

            return cidrs.Count > 0 ? cidrs : _config.CfDefaultIpv4Cidrs;
        }
        catch
        {
            return _config.CfDefaultIpv4Cidrs;
        }
    }

    private static List<string> FilterByCidrs(List<string> ips, List<string> cidrs)
    {
        var networks = cidrs
            .Select(ParseCidr)
            .Where(n => n.HasValue)
            .Select(n => n!.Value)
            .ToList();

        var filtered = new List<string>();
        foreach (var ip in ips)
        {
            if (!IPAddress.TryParse(ip, out var addr)) continue;
            var ipBytes = addr.GetAddressBytes();
            if (ipBytes.Length != 4) continue;
            var ipInt = (uint)ipBytes[0] << 24 | (uint)ipBytes[1] << 16 | (uint)ipBytes[2] << 8 | ipBytes[3];

            foreach (var (netInt, mask) in networks)
            {
                if ((ipInt & mask) == netInt)
                {
                    filtered.Add(ip);
                    break;
                }
            }
        }
        return filtered;
    }

    private static (uint network, uint mask)? ParseCidr(string cidr)
    {
        var parts = cidr.Split('/');
        if (parts.Length != 2 || !int.TryParse(parts[1], out var prefix) || prefix < 0 || prefix > 32)
            return null;
        if (!IPAddress.TryParse(parts[0], out var addr))
            return null;
        var bytes = addr.GetAddressBytes();
        if (bytes.Length != 4) return null;

        var ipInt = (uint)bytes[0] << 24 | (uint)bytes[1] << 16 | (uint)bytes[2] << 8 | bytes[3];
        var mask = prefix == 0 ? 0 : ~0u << (32 - prefix);
        return ((ipInt & mask), mask);
    }
}
