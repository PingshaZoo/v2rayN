using System.Net;

namespace ServiceLib.Handler.CloudflareBestIP;

/// <summary>
/// 数据源拉取器 / Data source fetcher
///
/// 两个入口分别服务于两阶段流水线 / Two entry points for the two-phase pipeline:
///   Phase 1: FetchHistoricalIpsAsync() — 反序列化 HavePostRes JSON，按 score 升序返回 IP
///   Phase 2: FetchGeneralIpsAsync()  — IpSetUrls 正则提取 + DomainsSetUrl DNS 解析，CF CIDR 过滤去重
///
/// CIDR 过滤使用手动位运算（无外部 NuGet 依赖）
/// CIDR filtering uses manual bit operations (no external NuGet dependency)
/// </summary>
public class CfDataFetcher
{
    private readonly CfBestIpItem _config;
    private readonly HttpClient _httpClient;

    // 预编译正则：匹配 IPv4 地址 / compiled regex: match IPv4 addresses
    private static readonly Regex _ipRegex = new(
        @"\b((?:(?:25[0-5]|2[0-4]\d|[01]?\d\d?)\.){3}(?:25[0-5]|2[0-4]\d|[01]?\d\d?))\b",
        RegexOptions.Compiled);

    public CfDataFetcher(CfBestIpItem config)
    {
        _config = config;
        _httpClient = new HttpClient();
        _httpClient.Timeout = TimeSpan.FromSeconds(30);
    }

    // ══════════════════════════════════════════════════════════════════════
    // Phase 1: 历史优选 IP（HavePostRes JSON 反序列化）
    // Phase 1: historically-good IPs (HavePostRes JSON deserialization)
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 从 HavePostRes URL 拉取历史优选结果，反序列化 JSON，按 IP 去重（保留最低 score），
    /// 按 score 升序排序后返回 IP 列表。
    ///
    /// Fetch historical best results from HavePostRes URLs, deserialize JSON,
    /// deduplicate by IP (keep lowest score), sort by score ascending, return IP list.
    ///
    /// JSON 格式 / format: [{"ip":"...","score":1.5,"speed_kb_s":8500,"colo":"HKG",...}, ...]
    /// </summary>
    /// <returns>按 score 升序排列的 IP 列表 / IP list sorted by score ascending</returns>
    public async Task<List<CfIpSource>> FetchHistoricalIpsAsync(Action<string>? onProgress = null)
    {
        var urls = _config.HavePostRes.Where(u => u.IsNotEmpty()).ToList();
        if (urls.Count == 0)
        {
            onProgress?.Invoke("Phase 1: HavePostRes is empty, skipping.");
            return [];
        }

        // 并发拉取所有 HavePostRes URL + POST_URLS（去重），反序列化 JSON
        // Concurrently fetch all HavePostRes URLs (merged with POST_URLS, deduped)
        var allUrls = urls.Union(_config.PostUrls ?? []).Distinct().ToList();
        var allItems = new List<CfHistoricalIpItem>();

        var fetches = allUrls.Select(async url =>
        {
            try
            {
                var content = await _httpClient.GetStringAsync(url);
                var json = JsonNode.Parse(content);
                if (json is JsonArray arr)
                {
                    var items = new List<CfHistoricalIpItem>();
                    foreach (var item in arr)
                    {
                        var ip = item?["ip"]?.GetValue<string>();
                        if (ip.IsNullOrEmpty()) continue;
                        items.Add(new CfHistoricalIpItem
                        {
                            Ip = ip,
                            Score = item?["score"]?.GetValue<double>() ?? 999999,
                            SpeedKbS = item?["speed_kb_s"]?.GetValue<double>() ?? 0,
                            Colo = item?["colo"]?.GetValue<string>(),
                            SourceUrl = url,
                        });
                    }
                    onProgress?.Invoke($"  HavePostRes: {url} → {items.Count} items");
                    return items;
                }
            }
            catch { }
            return new List<CfHistoricalIpItem>();
        });

        var results = await Task.WhenAll(fetches);
        foreach (var items in results)
            allItems.AddRange(items);

        if (allItems.Count == 0)
        {
            onProgress?.Invoke("Phase 1: no historical IPs found from HavePostRes URLs.");
            return [];
        }

        // 按 IP 去重：同一 IP 保留 score 最低的记录（对标 Python 版 _dedup_by_best_score）
        // Deduplicate by IP: keep the lowest score for each IP
        var bestByIp = new Dictionary<string, CfHistoricalIpItem>();
        foreach (var item in allItems)
        {
            if (!bestByIp.TryGetValue(item.Ip, out var existing) || item.Score < existing.Score)
                bestByIp[item.Ip] = item;
        }

        // 按 score 升序排序（score 越低越好，最快的 IP score=1）
        // Sort by score ascending (lower is better, fastest IP has score=1)
        var sorted = bestByIp.Values.OrderBy(x => x.Score).ToList();

        onProgress?.Invoke($"  HavePostRes: {allItems.Count} raw → {sorted.Count} unique IPs (sorted by score)");

        // 输出前 10 个 IP 的历史成绩供参考
        // Log top 10 historical scores for reference
        foreach (var item in sorted.Take(10))
        {
            onProgress?.Invoke(
                $"  HIST {item.Ip,-18} score={item.Score,6:F1} speed={item.SpeedKbS,6:F0}KB/s colo={item.Colo ?? "?",-6}");
        }

        return sorted.Select(x => new CfIpSource { Ip = x.Ip, SourceUrl = x.SourceUrl }).ToList();
    }

    // ══════════════════════════════════════════════════════════════════════
    // Phase 2: 常规 IP（IpSetUrls 正则提取 + DomainsSetUrl DNS 解析）
    // Phase 2: general IPs (IpSetUrls regex extraction + DomainsSetUrl DNS resolve)
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 从 IpSetUrls 和 DomainsSetUrl 拉取 IP 列表，排除 excludeIps 中已探测的 IP，经 CF CIDR 过滤去重。
    /// Fetch IPs from IpSetUrls and DomainsSetUrl, exclude already-probed IPs, filter by CF CIDRs, deduplicate.
    /// </summary>
    /// <param name="excludeIps">Phase 1 已探测的 IP（跳过）/ Phase 1 already-probed IPs to skip</param>
    public async Task<List<CfIpSource>> FetchGeneralIpsAsync(HashSet<string> excludeIps, Action<string>? onProgress = null)
    {
        var ipToSource = new Dictionary<string, string>();  // IP → source URL
        var seen = new HashSet<string>();

        // 预填排除集合
        foreach (var ip in excludeIps)
            seen.Add(ip);

        // ── IpSetUrls: 第三方 IP 库（并发拉取，记录来源 URL）──
        onProgress?.Invoke("Fetching third-party IP sources...");
        var ipSetUrls = _config.IpSetUrls.Where(u => u.IsNotEmpty()).ToList();
        var fetches = ipSetUrls.Select(async url =>
        {
            try
            {
                var ips = await FetchIpsFromUrlAsync(url);
                return (url, ips);
            }
            catch { return (url, new List<string>()); }
        }).ToArray();
        var fetchResults = await Task.WhenAll(fetches);
        foreach (var (url, ips) in fetchResults)
        {
            foreach (var ip in ips)
            {
                if (seen.Add(ip))
                    ipToSource[ip] = url;
            }
        }

        // ── DomainsSetUrl: 域名列表 → DNS 解析（记录来源域名）──
        if (_config.DomainsSetUrl.IsNotEmpty())
        {
            onProgress?.Invoke("Fetching and resolving domains...");
            var domains = await FetchDomainsAsync(_config.DomainsSetUrl);
            var resolved = await ResolveDomainsConcurrentWithSourceAsync(domains);
            foreach (var (ip, domain) in resolved)
            {
                if (seen.Add(ip))
                    ipToSource[ip] = domain;
            }
        }

        // ── Cloudflare CIDR 过滤 ──
        var allIpList = ipToSource.Keys.ToList();
        onProgress?.Invoke($"Filtering {allIpList.Count} IPs against Cloudflare CIDRs...");
        var cidrs = await FetchCfCidrsAsync();
        var filtered = FilterByCidrs(allIpList, cidrs);

        var result = filtered.Select(ip => new CfIpSource { Ip = ip, SourceUrl = ipToSource.GetValueOrDefault(ip) }).ToList();
        onProgress?.Invoke($"Final IP count after dedup & filter: {result.Count}");
        return result;
    }

    // ══════════════════════════════════════════════════════════════════════
    // 私有辅助方法 / Private helpers (unchanged)
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 从 URL 获取内容，正则提取所有 IPv4 地址
    /// Fetch content from URL and extract all IPv4 addresses via regex
    /// </summary>
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

    /// <summary>
    /// 从 API 获取域名列表（支持 JSON 数组 或 {"domains": [...]} 对象格式）
    /// Fetch domain list from API (supports JSON array or {"domains": [...]} object format)
    /// </summary>
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

    /// <summary>
    /// 并发 DNS 解析域名列表（20 并发，仅取 IPv4），返回 (ip, domain) 以追踪来源
    /// </summary>
    private async Task<List<(string ip, string domain)>> ResolveDomainsConcurrentWithSourceAsync(List<string> domains)
    {
        var results = new ConcurrentBag<(string ip, string domain)>();
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
                        results.Add((addr.ToString(), domain));
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
        return results.Distinct().ToList();
    }

    /// <summary>
    /// 从 Cloudflare 官方 API 获取 IPv4 CIDR 列表，失败时回退到兜底列表
    /// Fetch IPv4 CIDRs from Cloudflare official API, fallback to built-in list on failure
    /// </summary>
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

    /// <summary>
    /// CIDR 位运算过滤：将 IP 转为 uint，与每个 CIDR 网段的 (network & mask) 比对
    /// CIDR bit-operation filtering: convert IP to uint, compare against each CIDR (network & mask)
    ///
    /// 为什么不用 IPNetwork.Parse 等 NuGet 包：
    ///   项目惯例避免引入外部依赖，手动位运算性能更好且代码量小
    /// Why manual bit ops instead of a NuGet package:
    ///   project convention avoids external deps, manual ops are faster and concise
    /// </summary>
    private static List<string> FilterByCidrs(List<string> ips, List<string> cidrs)
    {
        // 解析所有 CIDR → (网络地址uint, 子网掩码uint)
        // Parse all CIDRs → (network address uint, subnet mask uint)
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

            // 将 4 字节 IP 合并为一个 uint（大端序）
            // Combine 4-byte IP into a single uint (big-endian)
            var ipInt = (uint)ipBytes[0] << 24 | (uint)ipBytes[1] << 16 | (uint)ipBytes[2] << 8 | ipBytes[3];

            // 检查 IP 是否匹配任一 CIDR 网段
            // Check if IP matches any CIDR range
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

    /// <summary>
    /// 解析 CIDR 字符串 "x.x.x.x/n" → (网络地址uint, 子网掩码uint)
    /// Parse CIDR string "x.x.x.x/n" → (network address uint, subnet mask uint)
    ///
    /// 示例 / Example: "1.2.3.0/24" → (0x01020300, 0xFFFFFF00)
    /// </summary>
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
        // 子网掩码：prefix=0 时 mask=0（匹配所有），否则计算 ~0u << (32-prefix)
        // Subnet mask: prefix=0 → mask=0 (match all), otherwise ~0u << (32-prefix)
        var mask = prefix == 0 ? 0 : ~0u << (32 - prefix);
        return ((ipInt & mask), mask);
    }
}

/// <summary>
/// IP + 数据来源，贯穿整个探测管线
/// </summary>
public class CfIpSource
{
    public string Ip { get; set; } = string.Empty;
    public string? SourceUrl { get; set; }
}

/// <summary>
/// HavePostRes JSON 反序列化模型（仅 CfDataFetcher 内部使用 / internal use only）
///
/// 对标 Python post_all_results 上报的 JSON 格式 / Matches Python post_all_results JSON format:
///   {"ip":"1.2.3.4","score":1.5,"speed_kb_s":8500,"colo":"HKG","lat":45.2,...}
/// </summary>
internal class CfHistoricalIpItem
{
    public string Ip { get; set; } = string.Empty;
    public double Score { get; set; }
    public double SpeedKbS { get; set; }
    public string? Colo { get; set; }
    public string? SourceUrl { get; set; }
}
