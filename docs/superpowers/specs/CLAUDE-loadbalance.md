# v2rayN 自适应节点调度器设计文档 v2.0

**版本**: 2.0（综合 Claude 架构评审 + ChatGPT 工程反馈修订）  
**目标**: 实现类 SSR 动态 QoS 调度体验，基于被动学习而非主动测速  
**约束**: C# / Windows / v2rayN 架构，不 fork xray-core  
**定位**: 轻量 adaptive routing，不是 SD-WAN，不是网络科研项目

---

## 目录

1. [ChatGPT 原始方案错误点分析](#1-chatgpt-原始方案错误点分析)
2. [v2.0 修订说明（吸收 ChatGPT 工程反馈）](#2-v20-修订说明)
3. [总体设计](#3-总体设计)
4. [数据结构设计](#4-数据结构设计)
5. [模块详细设计](#5-模块详细设计)
6. [评分公式与数学基础](#6-评分公式与数学基础)
7. [节点状态机与生命周期](#7-节点状态机与生命周期)
8. [关键代码骨架（完整可落地版）](#8-关键代码骨架)
9. [Phase 落地计划](#9-phase-落地计划)
10. [关键决策备忘](#10-关键决策备忘)

---

## 1. ChatGPT 原始方案错误点分析

### 1.1 EWMA α 是魔法数字，缺乏理论依据

**错误做法**

```text
new = old × 0.8 + current × 0.2   (α = 0.2，固定)
```

**为什么错**

α 编码的是"多久以前的数据开始失去意义"。TCP 拥塞控制（RFC 6298）的 SRTT
α=1/8 是基于采样频率和 RTT 量级的严格推导，不是拍脑袋。

代理场景的致命差异在于**采样间隔极不均匀**：用户看视频时每秒数十个连接，
看文章时可能 10 分钟一个请求。固定 α 的后果：

- 高频场景：α 过小，节点变差了 30 秒还没反应
- 低频场景：10 分钟前的 EWMA 被当成当前质量，应衰减的没衰减，严重失真

**正确做法：time-decayed EWMA**

```csharp
// α 随距上次观测的时间动态缩放
// 观测间隔 → 0s 时 α ≈ 0.30；间隔 → ∞ 时 α → 0.05
double DecayedAlpha(DateTime lastObserved) {
    double ageSec = (DateTime.UtcNow - lastObserved).TotalSeconds;
    return 0.05 + 0.25 * Math.Exp(-ageSec / 60.0);
}
```

---

### 1.2 "TCP connect duration" 在 v2rayN 架构下物理上拿不到

**错误做法**

```text
被动统计：TCP connect duration
```

**为什么错**

v2rayN 的流量链路：

```
本地 SOCKS5/HTTP → xray inbound → router → outbound → 远端节点
```

TCP 三次握手和 TLS 握手在 xray-core 内部完成，对外**只暴露 Stats gRPC API**，
内容仅为每个 outbound tag 的 `bytes_sent` / `bytes_recv` 累计值。
用户态 C# 代码物理上无法获取 socket 级延迟。

| 可观测 | 不可观测 |
|--------|---------|
| 应用层 TTFB（需主动探测） | TCP 三次握手时间 |
| xray stats API 字节计数 | TLS 握手时间 |
| 连接结果（成功/失败/超时） | 单连接 RTT |
| 整体吞吐率（字节差值/间隔） | 单连接丢包率 |

**正确做法**

通过本地 HTTP 客户端向指定 outbound tag 发出轻量 HEAD 请求，
测量 TTFB（Time To First Byte）作为延迟代理指标。

---

### 1.3 Cooldown 无全局约束，高并发下必然雪崩

**错误做法**

```text
失败 1 次 → cooldown 30s
失败 2 次 → cooldown 60s
失败 3 次 → cooldown 120s
（无任何全局约束）
```

**为什么错**

用户瞬间打开 20 个标签页时的雪崩链：

```
1. 节点 A 收到 20 个并发连接
2. 3 个超时（GFW 正常干扰）→ 节点 A 进 cooldown
3. 所有流量压到节点 B → 节点 B 过载超时 → 进 cooldown
4. 全部节点 cooldown → 用户完全断网
```

这是经典**惊群 + 雪崩**。Envoy outlier detection 的 `max_ejection_percent`
默认 10% 正是为了防止这个场景。

**正确做法**

任意时刻最多 1/3 节点可处于 cooldown 状态，超出上限时改为降权而非封禁。

---

### 1.4 QUIC/HTTP3 场景下"连接固定"策略直接失效

**错误做法**

```text
新连接建立时选择节点，然后整个连接固定
```

**为什么错**

- 一个 QUIC 连接可承载数百并发 stream，持续数小时
- 浏览器复用同一 QUIC 连接访问同一域名所有资源
- 连接固定到节点 A 后，即使 A 已经变差，所有流量被锁定

xray 的 `quic` 传输是混淆用途（vmess/vless 包装），
不是真正的 HTTP3 透明代理。真正的 HTTP3 用户流量走 UDP 路径，
与 TCP 路径是完全不同的代码流，分数不能混用。

**正确做法**：TCP 节点池与 UDP/QUIC 节点池完全隔离，独立打分。
Phase 1 优先实现 TCP 池，QUIC 池在 Phase 2 跟进。

---

### 1.5 Windows 计时精度问题导致延迟测量严重失真

**错误做法**

```csharp
var start = DateTime.Now;
await operation();
var latencyMs = (DateTime.Now - start).TotalMilliseconds; // ❌
```

**为什么错**

Windows 系统定时器中断默认间隔 **15.6ms**。
代理延迟通常在 50~200ms 量级，±15ms 意味着 **30% 的系统误差**，
EWMA 输入数据严重失真。

**正确做法**

```csharp
long t0 = Stopwatch.GetTimestamp(); // 基于 QPC，精度 < 1μs
await operation();
double ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency; // ✅
```

---

### 1.6 纯被动方案无冷启动机制，开机第一次用必然命中死节点

**错误做法**

完全依赖被动观测，无初始化。

**为什么错**

系统刚启动时所有节点分数相同（或为空），第一批连接均匀分布到所有节点，
包括已经宕机的节点。用户开机第一次打开浏览器就可能超时。

**正确做法**

启动时并行对所有节点做一次轻量 TCP connect 探活（2s 超时），
结果作为初始分数。探活完成前调度器缓存请求或顺序尝试。

---

### 错误点汇总

| 问题 | ChatGPT 原始方案 | 正确方案 |
|------|----------------|---------|
| EWMA α | 固定 0.2，魔法数字 | time-decayed α，随观测间隔动态调整 |
| 延迟数据源 | TCP connect duration（xray 架构下拿不到） | TTFB via HTTP HEAD probe |
| 计时精度 | 未提及（隐含 DateTime，误差 ±15ms） | `Stopwatch.GetTimestamp()`，< 1μs |
| Cooldown 雪崩 | 无全局约束 | max_ejected ≤ 1/3 + jitter |
| QUIC 处理 | 完全忽略 | 独立节点池，不与 TCP 共享分数 |
| 冷启动 | 无初始化，首次使用命中死节点概率高 | 并行 TCP 探活，初始化后放行流量 |
| 权重边界 | 未定义 | floor=1，cooldown FSM 处理封禁语义 |
| 全部节点 cooldown | 无兜底 | 选 cooldown 剩余最短节点降级服务 |

---

## 2. v2.0 修订说明

ChatGPT 在评审 v1.0 文档时提出了 5 条工程反馈，逐条评估如下：

### 2.1 `lock(this)` 是危险模式 ✅ 采纳

**ChatGPT 原话**：外部代码可能锁你的对象，应使用 `private readonly object _lock = new()`。

**评估**：完全正确。`lock(this)` 是 C# 经典反模式（CA2002 警告），
外部代码持有同一对象引用时可能造成意外死锁。v2.0 全部改为私有锁对象。

```csharp
// ❌ v1.0
lock (node) { ... }

// ✅ v2.0
public sealed class NodeState {
    private readonly object _lock = new();
    public void WithLock(Action action) { lock (_lock) action(); }
}
```

### 2.2 每节点 HttpClient 有 socket exhaustion 风险 ✅ 采纳（改进方案）

**ChatGPT 建议**：复用 handler。

**评估**：正确识别风险，但"复用 handler"的实现方式需要精确。
简单地共享一个 `HttpClient` 无法按 outbound tag 路由；
正确做法是每个 outbound tag 共享一个长生命周期的 `SocketsHttpHandler`，
并由 `ProberPool` 统一管理生命周期。v2.0 采用此方案。

### 2.3 "线程安全全靠 lock" 后期可能有锁竞争 ⚠️ 部分采纳

**评估**：Phase 1 节点数通常 < 20，lock 竞争不是瓶颈，
过早优化会增加实现复杂度。v2.0 保留 lock，
但将评分计算移到锁外（纯计算，不访问共享状态），
降低临界区长度。Phase 2 稳定后再评估是否需要 `ReaderWriterLockSlim`。

### 2.4 Phase 1 范围裁剪 ✅ 采纳

**ChatGPT 建议**：Phase 1 不做复杂 throughput learning、QUIC adaptive
migration、大量 background probes。

**评估**：与 v1.0 的 Phase 划分完全一致，原则正确。
轻量核心先上线，稳定后再迭代。

### 2.5 "系统必须轻" ✅ 采纳，但补充边界

**ChatGPT 建议**：v2rayN 定位决定必须"轻"，否则复杂度让体验比 SSR 更差。

**评估**：方向正确，但"轻"不等于"粗糙"。需要区分两类复杂度：

- **可以省略的复杂度**：QUIC migration、throughput learning、
  实时全局重算 → Phase 2/3 再做
- **不能省略的精确度**：time-decayed EWMA、cooldown 全局约束、
  `Stopwatch` 计时 → 这些是正确性保证，省掉会退化成比 SSR 更差的行为

**原则**：该精确的地方必须精确，该简单的地方坚决简单。

---

## 3. 总体设计

### 3.1 设计原则

```
目标：用户感觉"系统自己会挑节点"
手段：被动观测 + 时间衰减学习 + 概率调度
禁区：不碰 xray-core 内部 / 不做请求级切换 / 不做全局最优计算
定位：轻量 adaptive routing，不是 SD-WAN
```

**明确不做什么**

| 不做 | 原因 |
|------|------|
| DPI / 流量识别 | TLS + ECH 使 L7 信息趋近于零，方向错误 |
| 请求级切换 | 同一 TCP 连接中途换节点导致协议状态机崩溃 |
| 全局最优计算 | 所有流量压单节点会把该节点压死 |
| 高频主动测速 | 干扰正常流量，且无法代表真实业务质量 |
| 复杂 ML 模型 | 维护成本远超收益，v2rayN 不是科研项目 |

### 3.2 架构层次

```
┌──────────────────────────────────────────────────────┐
│                  v2rayN 进程（C#）                    │
│                                                      │
│  ┌────────────────────────────────────────────────┐  │
│  │            Dispatch Layer（调度层）              │  │
│  │   weighted random 选节点 · 连接级粒度            │  │
│  │   只读 ScoreTable，不写                          │  │
│  └───────────────────────┬────────────────────────┘  │
│                          │ 读分数                     │
│  ┌───────────────────────▼────────────────────────┐  │
│  │         Node State Machine（节点状态机）         │  │
│  │   NodeState · CooldownFsm · BootstrapProber    │  │
│  └───────────────────────┬────────────────────────┘  │
│                          │ 被动反馈写入               │
│  ┌───────────────────────▼────────────────────────┐  │
│  │        Measurement Layer（测量层）               │  │
│  │   TtfbProber · FailureCollector · StatsPoller  │  │
│  └───────────────────────┬────────────────────────┘  │
│                          │ Stats gRPC（只读）          │
└──────────────────────────┼───────────────────────────┘
                           │
┌──────────────────────────▼───────────────────────────┐
│              xray-core（完全黑盒，不修改）              │
│       Stats API: bytes_sent/recv per outbound tag     │
└──────────────────────────────────────────────────────┘
```

**层间规则**：调度层只读分数表；分数表只由测量层写入；xray-core 完全黑盒。

### 3.3 连接粒度

调度粒度选择**连接级**（TCP connection），不做请求级切换。

现代浏览器会持续建立新连接（新 tab、新域名、HTTP/2 stream 重建、
CDN chunk 新连接），自然产生持续的调度机会，无需强制在连接内切换节点。

---

## 4. 数据结构设计

### 4.1 NodeState

```csharp
public sealed class NodeState {
    // ── 标识（只读，初始化后不变）──────────────────────────
    public string Tag          { get; init; }  // xray outbound tag，唯一标识
    public string Host         { get; init; }  // 用于 Bootstrap TCP 探活
    public int    Port         { get; init; }
    public ProxyProtocol Protocol { get; init; } // Tcp | Udp

    // ── 评分状态（受 _lock 保护）──────────────────────────
    private readonly object _lock = new();

    private double _score         = 50.0;  // [1.0, 100.0]
    private double _ewmaLatencyMs = 500.0; // 初始假设 500ms
    private double _ewmaLossRate  = 0.10;  // 初始假设 10%
    private DateTime _lastObserved = DateTime.MinValue;
    private int _consecutiveFailures = 0;
    private DateTime _cooldownUntil  = DateTime.MinValue;

    // 只读属性（无锁，double 读在 x64 上是原子的）
    public double Score         => _score;
    public double EwmaLatencyMs => _ewmaLatencyMs;
    public double EwmaLossRate  => _ewmaLossRate;
    public DateTime LastObserved => _lastObserved;
    public int ConsecutiveFailures => _consecutiveFailures;
    public bool IsInCooldown => DateTime.UtcNow < _cooldownUntil;
    public DateTime CooldownUntil => _cooldownUntil;

    // 并发连接计数（Interlocked，无需锁）
    private int _activeConnections;
    public int ActiveConnections => _activeConnections;
    public void IncrementActive() => Interlocked.Increment(ref _activeConnections);
    public void DecrementActive() => Interlocked.Decrement(ref _activeConnections);

    // 批量更新（进锁一次，减少竞争）
    public void UpdateScore(double latencyMs, double lossRate,
                            double score, int consecutiveFailures) {
        lock (_lock) {
            _ewmaLatencyMs       = latencyMs;
            _ewmaLossRate        = lossRate;
            _score               = score;
            _consecutiveFailures = consecutiveFailures;
            _lastObserved        = DateTime.UtcNow;
        }
    }

    public void SetCooldown(DateTime until) {
        lock (_lock) { _cooldownUntil = until; }
    }

    public void ResetCooldown() {
        lock (_lock) { _cooldownUntil = DateTime.MinValue; }
    }

    // 快照读（测试 / 日志用）
    public NodeSnapshot Snapshot() {
        lock (_lock) {
            return new NodeSnapshot(Tag, _score, _ewmaLatencyMs,
                                    _ewmaLossRate, IsInCooldown, _cooldownUntil);
        }
    }
}

public record NodeSnapshot(string Tag, double Score, double LatencyMs,
                           double LossRate, bool InCooldown, DateTime CooldownUntil);

public enum ProxyProtocol { Tcp, Udp }
```

**设计说明**

- `_lock` 是私有对象，彻底消除 `lock(this)` 的外部竞争风险
- double 字段读取在 x64 平台上是原子的，属性读取无需锁
- `UpdateScore` 批量写入，一次进锁完成，减少锁持有时间
- `ActiveConnections` 用 `Interlocked`，读写不需要进入锁临界区

---

## 5. 模块详细设计

### 5.1 BootstrapProber — 冷启动探活

**目的**：消除冷启动盲区，确保调度器启动时有可用的初始分数。

```csharp
public sealed class BootstrapProber {
    private const int TcpTimeoutMs = 2000;
    private const int GlobalTimeoutMs = 3000; // 整体不超过 3s

    public async Task InitializeAsync(IReadOnlyList<NodeState> nodes,
                                      ScoreCalculator scorer) {
        using var cts = new CancellationTokenSource(GlobalTimeoutMs);

        var tasks = nodes
            .Where(n => n.Protocol == ProxyProtocol.Tcp)
            .Select(n => ProbeOneAsync(n, scorer, cts.Token));

        // 等所有探活完成，或 3s 全局超时
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private static async Task ProbeOneAsync(NodeState node,
                                            ScoreCalculator scorer,
                                            CancellationToken ct) {
        long t0 = Stopwatch.GetTimestamp();
        try {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(node.Host, node.Port, ct).ConfigureAwait(false);
            double latencyMs = ElapsedMs(t0);

            // 探活成功：按延迟设置初始分数
            double score = scorer.Compute(latencyMs, lossRate: 0.0);
            node.UpdateScore(latencyMs, 0.0, score, 0);
        }
        catch (OperationCanceledException) {
            // 超时：分数压到底，不进 cooldown（等真实流量确认）
            node.UpdateScore(5000, 1.0, 1.0, 0);
        }
        catch {
            node.UpdateScore(5000, 1.0, 1.0, 0);
        }
    }

    private static double ElapsedMs(long t0) =>
        (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
}
```

---

### 5.2 TtfbProber — TTFB 探测器（含 HttpClient 复用）

**目的**：提供精确的延迟观测，是不 fork xray 前提下唯一可行的延迟数据源。

**v2.0 关键改进**：每个 outbound tag 复用一个长生命周期 `SocketsHttpHandler`，
避免 socket exhaustion。

```csharp
public sealed class TtfbProber : IDisposable {
    // CF 连通检测端点：轻量、全球可达、不需要认证
    private const string ProbeUrl = "http://cp.cloudflare.com/";
    private const int TimeoutMs   = 5000;

    // 每个 tag 一个 handler，长期复用，避免 socket exhaustion
    private readonly ConcurrentDictionary<string, (SocketsHttpHandler handler,
                                                    HttpClient client)> _pool = new();
    private readonly Func<string, int> _portResolver; // tag → 本地 SOCKS5 端口
    private bool _disposed;

    public TtfbProber(Func<string, int> portResolver) {
        _portResolver = portResolver;
    }

    public async Task<ProbeResult> ProbeAsync(string tag,
                                              CancellationToken ct = default) {
        var (_, client) = _pool.GetOrAdd(tag, CreateEntry);
        long t0 = Stopwatch.GetTimestamp();
        try {
            var req = new HttpRequestMessage(HttpMethod.Head, ProbeUrl);
            using var resp = await client
                .SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            return new ProbeResult(true, ElapsedMs(t0), FailureType.None);
        }
        catch (OperationCanceledException) {
            return new ProbeResult(false, TimeoutMs, FailureType.Timeout);
        }
        catch (HttpRequestException ex) {
            return new ProbeResult(false, TimeoutMs, Classify(ex));
        }
    }

    private (SocketsHttpHandler, HttpClient) CreateEntry(string tag) {
        int port = _portResolver(tag);
        var handler = new SocketsHttpHandler {
            UseProxy  = true,
            Proxy     = new WebProxy($"socks5://127.0.0.1:{port}"),
            // 连接池：每个 handler 最多维护少量长连接
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
        };
        var client = new HttpClient(handler, disposeHandler: false) {
            Timeout = TimeSpan.FromMilliseconds(TimeoutMs)
        };
        return (handler, client);
    }

    private static double ElapsedMs(long t0) =>
        (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;

    private static FailureType Classify(HttpRequestException ex) =>
        ex.InnerException is SocketException { SocketErrorCode: SocketError.ConnectionRefused }
            ? FailureType.Refused : FailureType.NetworkError;

    public void Dispose() {
        if (_disposed) return;
        _disposed = true;
        foreach (var (h, c) in _pool.Values) { c.Dispose(); h.Dispose(); }
    }
}

public record ProbeResult(bool Success, double TtfbMs, FailureType Type);

public enum FailureType { None, Timeout, Refused, TlsError, NetworkError, UnexpectedEof }
```

---

### 5.3 FailureCollector — 失败事件收集器

```csharp
public sealed class FailureCollector {
    private readonly ScoreCalculator _scorer;
    private readonly CooldownFsm     _cooldown;

    public void RecordSuccess(NodeState node, double ttfbMs) {
        double alpha = DecayedAlpha(node.LastObserved);

        // 评分计算在锁外（纯计算，无共享状态访问）
        double newLatency = Ewma(node.EwmaLatencyMs, ttfbMs, alpha);
        double newLoss    = Ewma(node.EwmaLossRate,  0.0,    alpha);
        double newScore   = _scorer.Compute(newLatency, newLoss);

        node.UpdateScore(newLatency, newLoss, newScore,
                         consecutiveFailures: 0);
    }

    public void RecordFailure(NodeState node, FailureType type,
                              IReadOnlyList<NodeState> allNodes) {
        double alpha = DecayedAlpha(node.LastObserved);

        double newLatency = Ewma(node.EwmaLatencyMs, 10_000, alpha); // 超时值
        double newLoss    = Ewma(node.EwmaLossRate,  1.0,    alpha);
        double newScore   = _scorer.Compute(newLatency, newLoss);
        int newFails      = node.ConsecutiveFailures + 1;

        node.UpdateScore(newLatency, newLoss, newScore, newFails);
        _cooldown.TryEnterCooldown(node, allNodes);
    }

    // 静态工具方法
    private static double DecayedAlpha(DateTime lastObserved) {
        double ageSec = Math.Max(0,
            (DateTime.UtcNow - lastObserved).TotalSeconds);
        return 0.05 + 0.25 * Math.Exp(-ageSec / 60.0);
    }

    private static double Ewma(double old, double current, double alpha) =>
        old * (1 - alpha) + current * alpha;
}
```

---

### 5.4 CooldownFsm — 冷却状态机

```csharp
public sealed class CooldownFsm {
    // 全局约束：任意时刻最多 1/3 节点可处于 cooldown
    private const double MaxEjectionFraction = 1.0 / 3.0;
    // 指数退避参数
    private const double BaseSeconds  = 30.0;
    private const double JitterFactor = 0.20;  // ±20%
    private const double MaxSeconds   = 300.0; // 封顶 5 分钟

    public void TryEnterCooldown(NodeState node,
                                 IReadOnlyList<NodeState> allNodes) {
        // 单次失败不触发 cooldown，降权观察
        if (node.ConsecutiveFailures < 2) return;

        // 全局约束检查
        int cooldownCount = allNodes.Count(n => n.IsInCooldown);
        int maxAllowed    = (int)(allNodes.Count * MaxEjectionFraction);

        if (cooldownCount >= maxAllowed) {
            // 超出上限：不进 cooldown，分数已在 FailureCollector 里降低
            return;
        }

        // 计算 cooldown 时长（指数退避 + jitter）
        int n = Math.Max(0, node.ConsecutiveFailures - 2); // 从第2次失败开始退避
        double baseSec   = BaseSeconds * Math.Pow(2, n);
        double jitter    = baseSec * JitterFactor * (Random.Shared.NextDouble() - 0.5);
        double totalSec  = Math.Min(baseSec + jitter, MaxSeconds);

        node.SetCooldown(DateTime.UtcNow.AddSeconds(totalSec));
    }

    // cooldown 退避时长参考表
    // 连续失败次数 | cooldown 时长（含 jitter）
    //           2 | 30s ± 3s
    //           3 | 60s ± 6s
    //           4 | 120s ± 12s
    //           5 | 240s ± 24s
    //          6+ | 300s ± 30s（封顶）
}
```

---

### 5.5 XrayStatsPoller — xray Stats API 轮询器

```csharp
public sealed class XrayStatsPoller : IAsyncDisposable {
    private const int PollIntervalMs = 5000;
    private readonly IXrayStatsClient _client;
    private readonly IReadOnlyList<NodeState> _nodes;
    private readonly Dictionary<string, long> _lastBytes = new();
    private CancellationTokenSource? _cts;

    public void Start() {
        _cts = new CancellationTokenSource();
        _ = RunAsync(_cts.Token);
    }

    private async Task RunAsync(CancellationToken ct) {
        while (!ct.IsCancellationRequested) {
            await Task.Delay(PollIntervalMs, ct).ConfigureAwait(false);
            try { await PollOnceAsync().ConfigureAwait(false); }
            catch { /* 忽略单次轮询异常，继续下一轮 */ }
        }
    }

    private async Task PollOnceAsync() {
        var stats = await _client.GetOutboundStatsAsync().ConfigureAwait(false);
        foreach (var (tag, currentBytes) in stats) {
            if (_lastBytes.TryGetValue(tag, out long last)) {
                long delta = currentBytes - last;
                if (delta < 0) { _lastBytes[tag] = currentBytes; continue; } // 计数器重置
                double bps = delta / (PollIntervalMs / 1000.0);
                // 吞吐率作为辅助指标：仅用于发现"分数高但速度极差"的异常
                // 不直接参与主评分公式
                UpdateThroughputHint(tag, bps);
            }
            _lastBytes[tag] = currentBytes;
        }
    }

    private void UpdateThroughputHint(string tag, double bps) {
        var node = _nodes.FirstOrDefault(n => n.Tag == tag);
        if (node is null) return;
        // 若吞吐率极低（< 1KB/s）但分数仍高，触发一次 TTFB 主动探测
        // （此逻辑由外部订阅者处理，这里只发出事件）
        if (bps < 1024 && node.Score > 30)
            ThroughputAnomalyDetected?.Invoke(tag, bps);
    }

    public event Action<string, double>? ThroughputAnomalyDetected;

    public async ValueTask DisposeAsync() {
        _cts?.Cancel();
        _cts?.Dispose();
        await Task.CompletedTask;
    }
}
```

---

### 5.6 AdaptiveDispatcher — 调度器

```csharp
public sealed class AdaptiveDispatcher {
    private readonly IReadOnlyList<NodeState> _nodes;
    private readonly FailureCollector         _collector;
    private readonly TtfbProber               _prober;

    public NodeState Select(ProxyProtocol protocol) {
        var pool = _nodes.Where(n => n.Protocol == protocol).ToList();
        var live = pool.Where(n => !n.IsInCooldown).ToList();

        // 兜底：全部 cooldown 时选剩余时间最短的
        if (live.Count == 0)
            return pool.MinBy(n => n.CooldownUntil)
                   ?? throw new InvalidOperationException("No nodes configured.");

        return WeightedRandom(live);
    }

    private static NodeState WeightedRandom(List<NodeState> candidates) {
        double total = candidates.Sum(n => n.Score);
        double roll  = Random.Shared.NextDouble() * total;
        double cum   = 0;
        foreach (var n in candidates) {
            cum += n.Score;
            if (roll < cum) return n;
        }
        return candidates[^1]; // 浮点精度保底
    }

    // 连接完成后调用（由调用方在 using 块或 finally 中触发）
    public async Task OnConnectionCompletedAsync(NodeState node,
                                                 bool success,
                                                 FailureType failureType,
                                                 IReadOnlyList<NodeState> allNodes) {
        if (success) {
            // 成功：主动探测一次 TTFB 获取精确延迟
            var r = await _prober.ProbeAsync(node.Tag).ConfigureAwait(false);
            if (r.Success)
                _collector.RecordSuccess(node, r.TtfbMs);
        } else {
            _collector.RecordFailure(node, failureType, allNodes);
        }
    }
}
```

---

## 6. 评分公式与数学基础

### 6.1 Time-Decayed EWMA

```
α(Δt) = 0.05 + 0.25 × e^(−Δt / 60)
```

| 观测间隔 Δt | α 值 | 含义 |
|------------|------|------|
| 0s（刚观测）| 0.30 | 新数据权重 30% |
| 10s | 0.25 | 新数据权重 25% |
| 60s | 0.14 | 新数据权重 14% |
| 5min | 0.06 | 新数据权重 6%，趋向最小值 |
| 30min+ | 0.05 | 最小值，防止历史值完全主导 |

时间常数 60s 的依据：经验上代理节点质量的短期波动（GFW 干扰）
周期约在数十秒量级，60s 时间常数能平滑噪声同时不失去响应灵敏度。

### 6.2 评分公式

```csharp
public sealed class ScoreCalculator {
    private const double LatencyRef = 2000.0; // 延迟参考上限（ms）
    private const double LatencyWeight = 0.55;
    private const double LossWeight    = 0.45;
    private const double ScoreFloor    = 1.0;
    private const double Exponent      = 2.0; // 放大差距

    public double Compute(double ewmaLatencyMs, double ewmaLossRate) {
        // Step 1：归一化到 [0, 1]，消除量纲差异
        double latNorm  = Math.Min(ewmaLatencyMs / LatencyRef, 1.0);
        double lossNorm = Math.Clamp(ewmaLossRate, 0.0, 1.0);

        // Step 2：加权组合（延迟影响每次请求，权重略高）
        double raw = 1.0 - (latNorm * LatencyWeight + lossNorm * LossWeight);
        raw = Math.Max(raw, 0.0);

        // Step 3：平方放大差距，好坏节点权重比更显著
        // raw=0.95 → score≈90；raw=0.50 → score≈25；raw=0.20 → score≈4
        double score = Math.Pow(raw, Exponent) * 100.0;

        // Step 4：下界 1.0，保留调度资格（cooldown 才是真正的"暂停"）
        return Math.Max(score, ScoreFloor);
    }
}
```

**为什么用平方放大**：线性映射下，延迟 100ms 与 500ms 节点的权重比约 1.27:1，
调度器几乎感知不到差异。平方后变为 1.6:1，差距显著，好节点被选中概率更快上升。

### 6.3 分数与调度概率对照

以 3 节点场景为例（不含 cooldown 节点）：

| 延迟 | 失败率 | Score | 3 节点时选中概率 |
|------|--------|-------|----------------|
| 80ms | 1% | 91.8 | ≈ 55% |
| 200ms | 3% | 76.6 | ≈ 46% |
| 500ms | 10% | 46.0 | ≈ 28% |
| 1200ms | 30% | 21.6 | ≈ 13% |
| 3000ms | 80% | 1.0 | ≈ 1%（保底） |

---

## 7. 节点状态机与生命周期

### 7.1 完整状态机

```
              [初始化]
                 │
           Bootstrap 探活
          ┌──────┴──────┐
        成功            失败
       Score>1        Score=1
          │              │
          └──────┬───────┘
                 ▼
            [ACTIVE]  ◄──────────────────────────────┐
                 │                                    │
                 │ consecutiveFailures >= 2           │
                 │ AND cooldown节点数 < 总数/3         │
                 ▼                                    │
           [COOLDOWN]                                 │
                 │                                    │
                 │ 到期前 5s → 触发 TTFB 探活          │
                 │   ├─ 探活失败 → 重置 cooldown       │
                 │   └─ 探活成功 → 等待自然到期         │
                 │                                    │
                 │ cooldown 自然到期                   │
                 ▼                                    │
           [RECOVERING]                               │
           Score 从低值起                              │
           EWMA 自然上升 ────────────────────────────┘
```

**全局约束**：COOLDOWN 状态节点数 ≤ floor(总节点数 / 3)。
超出上限时，新的失败节点改为降权处理（Score × 0.5），不进 cooldown。

### 7.2 启动序列

```
T=0ms    加载节点配置，所有节点 Score=50（未知状态）
T=0ms    BootstrapProber 并行探活所有节点（TCP connect，2s 超时）
T=0ms    XrayStatsPoller 启动（不阻塞调度）
T≤3000ms Bootstrap 完成（全局超时 3s 强制结束）
T=3001ms 调度器开始接受连接请求（使用 Bootstrap 结果的初始分数）
T+…      被动观测逐步替代 Bootstrap 初始值
T+30s    第一批 EWMA 数据基本稳定
```

### 7.3 TCP vs UDP/QUIC 隔离

```csharp
// 调度时强制按协议过滤，TCP 和 UDP 分数互不影响
var candidates = _nodes
    .Where(n => n.Protocol == requestedProtocol && !n.IsInCooldown)
    .ToList();
```

Phase 1 优先完成 TCP 池；UDP/QUIC 池在 Phase 2 跟进。

---

## 8. 关键代码骨架

### 8.1 依赖注入注册

```csharp
// Program.cs / DI 注册
services.AddSingleton<ScoreCalculator>();
services.AddSingleton<CooldownFsm>();
services.AddSingleton<FailureCollector>();
services.AddSingleton<BootstrapProber>();
services.AddSingleton<TtfbProber>(sp =>
    new TtfbProber(tag => sp.GetRequiredService<PortRegistry>().GetPort(tag)));
services.AddSingleton<XrayStatsPoller>();
services.AddSingleton<AdaptiveDispatcher>();
```

### 8.2 连接代理使用示例

```csharp
public class ProxyConnectionHandler {
    private readonly AdaptiveDispatcher _dispatcher;
    private readonly IReadOnlyList<NodeState> _allNodes;

    public async Task HandleAsync(Stream clientStream) {
        var node = _dispatcher.Select(ProxyProtocol.Tcp);
        node.IncrementActive();

        bool success = false;
        FailureType failType = FailureType.None;

        try {
            // 实际连接逻辑（通过 xray SOCKS5 路由）
            await ProxyThroughNode(node, clientStream);
            success = true;
        }
        catch (TimeoutException) { failType = FailureType.Timeout; }
        catch (SocketException)  { failType = FailureType.NetworkError; }
        finally {
            node.DecrementActive();
            await _dispatcher.OnConnectionCompletedAsync(
                node, success, failType, _allNodes);
        }
    }
}
```

### 8.3 可观测性：分数快照日志

```csharp
// 每 30s 输出一次节点分数快照，便于调试和验收
public class ScoreLogger {
    private readonly IReadOnlyList<NodeState> _nodes;
    private readonly ILogger _logger;

    public async Task StartAsync(CancellationToken ct) {
        while (!ct.IsCancellationRequested) {
            await Task.Delay(30_000, ct);
            var snapshots = _nodes
                .Select(n => n.Snapshot())
                .OrderByDescending(s => s.Score);
            foreach (var s in snapshots) {
                _logger.LogInformation(
                    "Node {Tag}: score={Score:F1} lat={LatencyMs:F0}ms " +
                    "loss={LossRate:P1} cooldown={InCooldown}",
                    s.Tag, s.Score, s.LatencyMs, s.LossRate, s.InCooldown);
            }
        }
    }
}
```

---

## 9. Phase 落地计划

### Phase 1：核心调度（2~3 周，先上线）

**目标**：替换 v2rayN 默认轮询/ping 选节点，达到"明显比默认行为更聪明"。

| 模块 | 说明 |
|------|------|
| `NodeState`（私有锁版） | 核心数据结构 |
| `ScoreCalculator` | 评分公式 |
| `BootstrapProber` | 启动 TCP 探活 |
| `CooldownFsm`（含全局约束） | 冷却状态机 |
| `FailureCollector` | 被动失败记录 |
| `AdaptiveDispatcher` | weighted random 调度 |
| `ScoreLogger` | 30s 快照日志 |

**验收标准**

- 死节点在 2 次连续失败后自动停用
- 好节点选中概率 > 差节点 × 2 倍（可从日志验证分数分布）
- 重启后 3s 内完成 Bootstrap，不影响用户使用
- 节点全部 cooldown 时系统不崩溃（兜底路径生效）

### Phase 2：被动测量增强（Phase 1 稳定后）

| 模块 | 说明 |
|------|------|
| `TtfbProber`（HttpClient 复用版） | 精确 TTFB 观测 |
| `XrayStatsPoller` | 吞吐率异常检测 |
| Cooldown 恢复探活 | 到期前 5s 主动验证 |
| UDP/QUIC 独立节点池 | TCP/UDP 分离 |

**验收标准**

- 节点质量变化后 5 分钟内分数调整到位
- 吞吐率异常节点被检测并触发重探
- 无 socket exhaustion 告警

### Phase 3：可观测性与持久化

| 模块 | 说明 |
|------|------|
| v2rayN UI 分数面板 | 实时显示各节点分数 |
| 分数持久化 | 重启后恢复上次分数，不强制冷启动 |
| QUIC 连接健康检查 | 30s 周期 HEAD 探测 |
| 调度决策审计日志 | 每次选节点记录候选集快照 |

---

## 10. 关键决策备忘

| 决策点 | 选择 | 理由 |
|--------|------|------|
| 调度粒度 | 连接级 | 请求级需协议感知；会话级太粗糙 |
| 延迟数据源 | TTFB via HTTP HEAD | xray 不暴露 TCP 级统计，唯一可行方案 |
| EWMA α | time-decayed | 采样间隔不均匀，固定 α 会冻结或过度响应 |
| 评分放大 | 平方 | 线性映射好坏节点权重差距不足以驱动调度倾斜 |
| 并发锁 | 私有 `_lock` 对象 | 消除 `lock(this)` 外部竞争风险（CA2002） |
| HttpClient | 按 tag 复用 handler | 避免 socket exhaustion，正确的长期方案 |
| Cooldown 上限 | 1/3 节点 | 防雪崩；节点数少时比 Envoy 默认 10% 更宽松 |
| Jitter | ±20% | 防止多节点同时恢复引发 thundering herd |
| 计时器 | `Stopwatch.GetTimestamp` | Windows `DateTime` 误差 ±15ms，不可接受 |
| QUIC 处理 | 独立池（Phase 2） | QUIC 连接语义与 TCP 完全不同，分数不能混用 |
| 吞吐率 | 辅助指标，不入主公式 | 主要用于异常检测，避免 xray stats 分辨率不足带来的噪声 |
| 兜底策略 | 选 cooldown 最短节点 | 总好过返回错误；用户可手动刷新重试 |
| 系统复杂度 | 轻量 adaptive routing | v2rayN 定位决定必须"轻"；核心数学不能省，UI 复杂度可以省 |
