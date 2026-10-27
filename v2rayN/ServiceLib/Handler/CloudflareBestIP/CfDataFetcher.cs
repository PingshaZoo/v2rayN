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
public class CfDataFetcher : IDisposable
{
    private readonly CfBestIpItem _config;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsClient;
    private readonly object _cidrLock = new();
    private Task<List<string>>? _cidrsTask;

    // 预编译正则：匹配 IPv4 地址 / compiled regex: match IPv4 addresses
    private static readonly Regex _ipRegex = new(
        @"\b((?:(?:25[0-5]|2[0-4]\d|[01]?\d\d?)\.){3}(?:25[0-5]|2[0-4]\d|[01]?\d\d?))\b",
        RegexOptions.Compiled);

    public CfDataFetcher(CfBestIpItem config, HttpClient? httpClient = null)
    {
        _config = config;
        _ownsClient = httpClient is null;
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    public void Dispose() { if (_ownsClient) _httpClient.Dispose(); }

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
    public async Task<List<CfIpSource>> FetchHistoricalIpsAsync(Action<string>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        var urls = (_config.HavePostRes ?? []).Concat(_config.PostUrls ?? [])
            .Where(CfProbeValidation.IsHttpUrl).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (urls.Count == 0) return [];
        var cidrs = await GetCfCidrsAsync(cancellationToken);
        var downloads = urls.Select(async url =>
        {
            try
            {
                var content = await _httpClient.GetStringAsync(url, cancellationToken);
                var array = JsonNode.Parse(content) as JsonArray;
                var items = new List<CfHistoricalIpItem>();
                if (array is null) return items;
                foreach (var row in array)
                {
                    try
                    {
                        var ip = row?["ip"]?.ToString();
                        if (!IsIpv4(ip)) continue;
                        var rawSpeed = row?["speed_kb_s"]?.ToString();
                        var speed = double.TryParse(rawSpeed, System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out var parsed)
                            && double.IsFinite(parsed) && parsed > 0 ? parsed : 0;
                        items.Add(new CfHistoricalIpItem { Ip = ip!, SpeedKbS = speed, SourceUrl = url });
                    }
                    catch { /* One malformed row must not discard the feed. */ }
                }
                onProgress?.Invoke($"{url}: {items.Count} 条历史结果");
                return items;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex) { onProgress?.Invoke($"{url}: {ex.Message}"); return []; }
        });
        var all = (await Task.WhenAll(downloads)).SelectMany(x => x).ToList();
        var filtered = new HashSet<string>(FilterByCidrs(all.Select(x => x.Ip).Distinct().ToList(), cidrs));
        return all.Where(x => filtered.Contains(x.Ip))
            .GroupBy(x => x.Ip)
            .Select(g => g.OrderByDescending(x => x.SpeedKbS).First())
            .OrderByDescending(x => x.SpeedKbS)
            .Select(x => new CfIpSource { Ip = x.Ip, SourceUrl = x.SourceUrl, HistoricalSpeedKBs = x.SpeedKbS })
            .ToList();
    }

    private static bool IsIpv4(string? ip) => IPAddress.TryParse(ip, out var parsed)
        && parsed.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork;

    // ══════════════════════════════════════════════════════════════════════
    // Phase 2: 常规 IP（IpSetUrls 正则提取 + DomainsSetUrl DNS 解析）
    // Phase 2: general IPs (IpSetUrls regex extraction + DomainsSetUrl DNS resolve)
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 从 IpSetUrls 和 DomainsSetUrl 拉取 IP 列表，排除 excludeIps 中已探测的 IP，经 CF CIDR 过滤去重。
    /// Fetch IPs from IpSetUrls and DomainsSetUrl, exclude already-probed IPs, filter by CF CIDRs, deduplicate.
    /// </summary>
    /// <param name="excludeIps">Phase 1 已探测的 IP（跳过）/ Phase 1 already-probed IPs to skip</param>
    public async Task<List<CfIpSource>> FetchGeneralIpsAsync(HashSet<string> excludeIps, Action<string>? onProgress = null,
        CancellationToken cancellationToken = default)
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
                var ips = await FetchIpsFromUrlAsync(url, cancellationToken);
                return (url, ips);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
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
            var domains = await FetchDomainsAsync(_config.DomainsSetUrl, cancellationToken);
            var resolved = await ResolveDomainsConcurrentWithSourceAsync(domains, cancellationToken);
            foreach (var (ip, domain) in resolved)
            {
                if (seen.Add(ip))
                    ipToSource[ip] = domain;
            }
        }

        // ── Cloudflare CIDR 过滤 ──
        var allIpList = ipToSource.Keys.ToList();
        onProgress?.Invoke($"Filtering {allIpList.Count} IPs against Cloudflare CIDRs...");
        var cidrs = await GetCfCidrsAsync(cancellationToken);
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
    private async Task<List<string>> FetchIpsFromUrlAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            var content = await _httpClient.GetStringAsync(url, cancellationToken);
            return _ipRegex.Matches(content)
                .Select(m => m.Value)
                .Distinct()
                .ToList();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch
        {
            return [];
        }
    }

    /// <summary>
    /// 从 API 获取域名列表（支持 JSON 数组 或 {"domains": [...]} 对象格式）
    /// Fetch domain list from API (supports JSON array or {"domains": [...]} object format)
    /// </summary>
    private async Task<List<string>> FetchDomainsAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            var content = await _httpClient.GetStringAsync(url, cancellationToken);
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
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch
        {
            return [];
        }
    }

    /// <summary>
    /// 并发 DNS 解析域名列表（20 并发，仅取 IPv4），返回 (ip, domain) 以追踪来源
    /// </summary>
    private async Task<List<(string ip, string domain)>> ResolveDomainsConcurrentWithSourceAsync(List<string> domains,
        CancellationToken cancellationToken)
    {
        var results = new ConcurrentBag<(string ip, string domain)>();
        var semaphore = new SemaphoreSlim(20);

        var tasks = domains.Select(async domain =>
        {
            await semaphore.WaitAsync(cancellationToken);
            try
            {
                var entries = await Dns.GetHostEntryAsync(domain, cancellationToken);
                foreach (var addr in entries.AddressList)
                {
                    if (addr.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                    {
                        results.Add((addr.ToString(), domain));
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
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
    private Task<List<string>> GetCfCidrsAsync(CancellationToken cancellationToken)
    {
        lock (_cidrLock)
            return _cidrsTask ??= FetchCfCidrsAsync(cancellationToken);
    }

    private async Task<List<string>> FetchCfCidrsAsync(CancellationToken cancellationToken)
    {
        try
        {
            var content = await _httpClient.GetStringAsync("https://api.cloudflare.com/client/v4/ips", cancellationToken);
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

            return cidrs.Count > 0 ? cidrs : (_config.CfDefaultIpv4Cidrs is { Count: > 0 } ? _config.CfDefaultIpv4Cidrs : Global.CfDefaultIpv4Cidrs);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch
        {
            return _config.CfDefaultIpv4Cidrs is { Count: > 0 } ? _config.CfDefaultIpv4Cidrs : Global.CfDefaultIpv4Cidrs;
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
    public double HistoricalSpeedKBs { get; set; }
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
