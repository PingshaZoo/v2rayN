# v2rayN 自适应节点调度器设计文档

**版本**: 4.0（2026-05-21 P0 验证：tag duplication 确认无效，系统重定位为 Active-Set Scheduler）  
**目标**: 动态剔除坏节点，保持 active-set 内节点可用。类 SSR 体验：坏节点自动消失，好节点稳定在线  
**约束**: C# / Windows / v2rayN 架构，不 fork xray-core  
**定位**: Adaptive Active-Set Scheduler — **核心目标是"动态剔除坏节点"，不是"精确概率分流"**

---

## 目录

1. [ChatGPT 原始方案错误点分析](#1-chatgpt-原始方案错误点分析)
2. [v2.0 修订说明（吸收 ChatGPT 工程反馈）](#2-v20-修订说明)
3. [总体设计](#3-总体设计)
4. [数据结构设计](#4-数据结构设计)
5. [模块详细设计](#5-模块详细设计)
6. [评分公式与数学基础](#6-评分公式与数学基础)
7. [节点状态机与生命周期](#7-节点状态机与生命周期)
8. [关键代码骨架](#8-关键代码骨架)
9. [行动计划（v3.0）](#9-行动计划v30基于-2026-05-21-综合评审)
10. [验收标准](#10-验收标准)
11. [关键决策备忘](#11-关键决策备忘)
12. [实时节点速度显示](#12-实时节点速度显示real-time-node-speed-display)

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

### 3.2 架构层次（实际实现）

```
┌──────────────────────────────────────────────────────┐
│                  v2rayN 进程（C#）                    │
│                                                      │
│  ┌────────────────────────────────────────────────┐  │
│  │        Control Plane（控制面，不进入数据路径）        │  │
│  │                                                │  │
│  │  ScoreCalculator → FailureCollector             │  │
│  │         ↓              ↓                       │  │
│  │    NodeState[] ← CooldownFsm                    │  │
│  │         ↓                                      │  │
│  │    ActiveSetManager（top-K + hysteresis）        │  │
│  │         ↓                                      │  │
│  │    GenAdaptiveConfig（active-set selector + 探活入站）│  │
│  └───────────────────────┬────────────────────────┘  │
│                          │ 生成 xray config.json      │
│                          │ （active-set unique tags + probe inbounds）│
└──────────────────────────┼───────────────────────────┘
                           │
┌──────────────────────────▼───────────────────────────┐
│              xray-core（完全黑盒，不修改）              │
│                                                      │
│  random balancer: 按 selector 列表等概率随机选 tag     │
│  重复 tag ×N → 该节点被选中概率放大 N 倍               │
│                                                      │
│  Stats API: bytes_sent/recv per outbound tag         │
└──────────────────────────────────────────────────────┘
```

**核心机制**：xray `random` balancer 对 selector 不做去重。`[A, A, A, B]` → A 命中率 75%。
C# 控制面根据分数生成重复次数（高分多重复），xray 侧自然实现概率加权调度。

**⚠️ P0 待验证**：此行为依赖 xray 内部实现细节，未被文档化为正式 API。必须在集成测试中验证通过才能算稳定。详见 §9 行动计划。

**层间规则**：C# 不进 Data Plane；调度由 xray 完成；C# 只维护分数 + 生成配置。

### 3.2a 系统能力边界

当前架构有能力做到的和做不到的，必须明确区分。这既是工程诚实，也是防止后续开发在不成立的假设上叠 hack。

**能做到**：

| 能力 | 实现方式 |
|------|---------|
| 自动淘汰坏节点 | cooldown FSM + active set 驱逐 |
| 自动恢复好节点 | cooldown 到期 + recovery probing + hysteresis 重新进入 |
| 动态 active set | score 驱动的 top-K + explorer + hysteresis 进出管理 |
| 自适应学习 | time-decayed EWMA（观测越久远影响越小） |
| 冷启动保护 | Bootstrap 并行 TCP connect 探活，覆盖过期历史分数 |
| 防止震荡 | hysteresis 缓冲带（Entry=60/Exit=35）+ debounce 防抖 |
| 可回放 telemetry | JSONL 独立日志，每事件一行，jq 可解析 |
| 一键紧急旁路 | EmergencyDisableAdaptive()，恢复默认配置，不重启 |

**做不到（当前架构约束）**：

| 限制 | 原因 |
|------|------|
| 真正 weighted routing | xray selector 对 candidates 做 prefix-match + dedup，tag 重复无效 |
| per-request balancing | 调度粒度为连接级（TCP connection），非请求级 |
| runtime probability shaping | xray 无动态 balancer API（`RandomStrategy.PickOutbound` 不可远程控制） |
| transparent QUIC migration | QUIC 连接语义与 TCP 完全不同，需独立节点池（Phase 3） |
| 全局最优计算 | 所有流量压单节点会压死该节点，必须维持 active set 分散 |

**核心原则**：系统定位为 Adaptive Health-Control Scheduler。核心价值是"坏节点自动消失"，不是"精确概率分流"。用户真正感知到的是坏节点自动消失，而不是 75% vs 25% 的 routing precision。

> **架构规则**：禁止继续在 weighted routing 上叠 hack。tag duplication 已被证实无效。任何新的加权方案必须在 xray 源码级验证 selector 行为后，才能进入设计阶段。Phase 1/2 坚持 active-set uniform random。

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

**探活策略（ProbeService / TtfbProber 触发规则）**

探活是在不 fork xray 前提下唯一可行的延迟观测手段，但会产生额外 HTTP 流量。触发条件必须明确分层：

```
触发条件（推荐策略）：
  1. Bootstrap 阶段   — 并行探活所有节点，一次性（TCP connect，3s 全局超时）
  2. Cooldown 恢复     — 到期前 5s 触发一次 TTFB 探活（3s 超时）
                        成功 → 立即清除 cooldown；失败 → 延长 cooldown
  3. 周期性补充（可选） — 低分节点（score < 20）每 2 分钟探一次
  4. 正常节点          — 不主动探活，依赖被动观测

探活并发上限：max(3, ceil(N / 5))，N 为节点总数
探活 URL：http://cp.cloudflare.com/（可配置，建议暴露到 Settings UI）
多目标探测：建议支持配置多个 ProbeUrl，取平均值减少单一目标偶发抖动
```

**探活偏差（Probe Bias）说明**

TTFB via HTTP HEAD 到 `cp.cloudflare.com` 是业务延迟的"代理指标"，不是真实业务 RTT。探活结果与 YouTube chunk、HTTP/2 多路复用、WebSocket 等真实流量的延迟存在系统偏差。这是已知约束：在 xray 不暴露 socket 级指标的架构下，TTFB 是唯一可行的延迟观测手段。

建议：如果用户业务主要是视频流，可选配置 `connectivitycheck.gstatic.com/generate_204` 等更接近真实业务路径的目标。

---

### 5.3 FailureCollector — 失败事件收集器（差异化惩罚）

> **P0 修复** (2026-05-21): 所有 FailureType 不应使用统一的 `lossRate = 1.0` 更新 EWMA。
> 不同类型代表完全不同的根因，应差异化惩罚。

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
        (double loss, double lat) = type switch {
            FailureType.Refused       => (1.0,   10_000),        // 端口不通，强惩罚
            FailureType.Timeout       => (0.8,   10_000),        // 可能 GFW，中强惩罚
            FailureType.NetworkError  => (0.7,   10_000),        // 通用网络错误
            FailureType.UnexpectedEof => (0.4,   node.EwmaLatencyMs * 1.5),  // 连接断开，弱惩罚
            FailureType.TlsError      => (0.0,   node.EwmaLatencyMs),         // 配置错误，不惩罚
            _                         => (0.5,   10_000),
        };

        // TlsError 不惩罚 EWMA，触发独立告警路径
        if (type == FailureType.TlsError) {
            // _alertService.RaiseTlsConfigError(node.Tag);
            return; // 不更新 score，不进入 cooldown
        }

        double alpha = DecayedAlpha(node.LastObserved);
        double newLatency = Ewma(node.EwmaLatencyMs, lat, alpha);
        double newLoss    = Ewma(node.EwmaLossRate,  loss, alpha);
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

### 5.5 ActiveSetManager — 活性集管理

> **本节为实际实现补充。** 设计文档 v1.0 未包含此模块，它是 active-set 调度方案的核心基础设施。

**职责**：管理哪些节点应该出现在 xray balancer selector 中。不直接调度连接（xray 负责），但决定调度器的输入列表。active-set 内流量均匀分配。

**Top-K 公式**（已文档化）：

```
K = max(3, ceil(total_nodes × 0.5))      # 至少 3 个，最多占非 cooldown 节点一半
explorer_count = max(1, floor(K × 0.15))  # 约 15%，至少 1 个
explorer 来源 = 分数最低的非 cooldown 节点（给低分节点保持曝光机会）
```

explorer 节点每个周期变化（随机选取），但 explorer 的变化**不触发** active set change → 不引起 reload。只有 top-K 集合变化才触发。

**迟滞检查（HasActiveSetChanged）**：只比较 top-K 集合是否变化。单节点进入/退出 cooldown 虽然会改变 active 集合，但由 score 驱动的 top-K 变化才触发 config 重生成。每次 Explorer 轮换不触发。

---

### 5.6 XrayStatsPoller — xray Stats API 轮询器

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

### 5.7 Balancer — Active-Set Uniform Random（v4.0 修订）

> **v4.0 修订**: 2026-05-21 集成测试证实 xray v26.3.27 对 selector 做 prefix-match + 去重。
> `selector: ["A", "A", "A", "B"]` → 去重后 candidates = `["A", "B"]` → 50/50 均匀随机。
> **tag duplication 从未生效。** 系统重定位为 Adaptive Active-Set Scheduler。

**当前行为**：
- active-set 内的节点通过 xray `random` balancer 均匀分配流量
- 坏节点被 cooldown / hysteresis 逐出 active set，不参与调度
- 核心价值是"动态剔除坏节点"，不是"精确概率分流"

**v3.0 弃用方案（tag duplication）**：
- 设计意图：selector 中重复 tag N 次 → N× 选中概率
- 实测结果：xray `SelectOutbounds()` 对 candidate outbound tags 做 prefix-match + 去重
- 结论：依赖 xray selector 内部实现细节做加权是不可行的

**xray selector 语义（已验证）**：
```go
// app/router/balancing.go SelectOutbounds()
// 1. 每个 selector 字符串做 strings.HasPrefix 匹配所有 outbound tag
// 2. 收集匹配的 outbound tag
// 3. 去重 → 传给 RandomStrategy.PickOutbound(candidates)
```
> **P0 行为契约**: 见 `XrayTagDuplicationIntegrationTests`。若未来 xray 修改 selector 语义（不去重），此测试会报警。

**核心原理**：
```
xray random balancer: rand.Intn(len(tags)) → 每个 tag 等概率
selector = [A, A, A, B] → P(A) = 75%, P(B) = 25%
selector = [A, A, B, B] → P(A) = 50%, P(B) = 50%
```

**tag 重复次数规则**：
```
tag_count = max(1, round(score / 25))   // score 100 → 4 次; score 25 → 1 次
floor = 1（所有节点至少 1 次，保留调度资格）；cooldown 节点不出现
```

**⚠️ P0 警告**: 此方案依赖 xray `random` balancer 不对 selector **做去重** 这一**隐式行为**。
xray 从未将此行为文档化为正式 API。任何版本更新都可能静默破坏此行为，且不会被视为 breaking change。
因此这是一个需要集成测试验证并加版本检查的 P0 级风险项。详见 §9 行动计划。

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
           恢复历史分数 + Bootstrap 探活
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
                 │   ├─ 探活成功 → 立即清除 cooldown   │
                 │   └─ 探活失败 → 延长 cooldown       │
                 │                                    │
                 │ cooldown 自然到期                   │
                 ▼                                    │
           [RECOVERING]                               │
           Score 从低值起                              │
           EWMA 自然上升 ────────────────────────────┘
```

**全局约束**：COOLDOWN 状态节点数 ≤ floor(总节点数 / 3)。
超出上限时，新的失败节点改为降权处理（Score × 0.5），不进 cooldown。

### 7.1a Active Set Hysteresis（迟滞机制）

> **P0 新增** (2026-05-21): 防止 score 在阈值附近震荡导致频繁 reload。

如果 active set 的进出门槛相同，节点 score 在 45~55 之间波动时会产生频繁的进出 active set，
每次变化都触发 xray reload（连接中断）。迟滞形成缓冲区：

```
进入门槛（Entry threshold）: score > 60     // 新节点需要较高分数才能进入
退出门槛（Exit  threshold）: score < 35     // 已在集合中的节点分数大幅下降才退出
缓冲带: 25 分                                // 防止震荡
```

```csharp
bool ShouldBeActive(NodeState node, bool currentlyActive) =>
    currentlyActive
        ? node.Score >= 35   // 节点已在 active set，维持到分数降至 35 以下
        : node.Score >= 60;  // 节点不在 active set，需要 60 以上才进入
```

### 7.2 启动序列（含实际时间线）

```
T=0ms      加载节点配置
           → 有历史分数: 恢复到 NodeState（作为初始值）
           → 无历史分数: 所有节点 Score=50（初始值）
T=0ms      BootstrapProber 并行 TCP connect 探活所有节点（2s 超时，全局 3s 超时）
           → Bootstrap 结果**始终覆盖**历史分数，防止过期高分掩盖已死亡节点
           → 历史分数过期机制：超过 4 小时的持久化分数强制回退到 50
T≤3000ms   Bootstrap 完成
T=3001ms   GenAdaptiveConfig：生成含 active-set selector 的 xray balancer 配置
T=3001ms+  xray 重启（实测耗时 ~1.1s，Windows 10, xray v26.3.27）
T≈4~5s     调度生效，流量开始在 active-set 内均匀分布
T=3001ms+  ProbeService + ScoreLogger + MonitorActiveSet 启动
T+15~30s   第一批被动观测 EWMA 数据逐步替代 Bootstrap 初始值
```

**调度响应延迟上界（active set 变化 → 流量切换完成）**：

```
节点质量恶化
  → EWMA 反映（取决于探活/被动观测频率）：T₁ (5~10s)
  → active set 更新（MonitorActiveSet 检查间隔）：5s
  → debounce 等待（ReloadPolicyApplier trailing）：15s
  → xray 重启（实测）：~1.1s
总计上限：15s + T₁ + 5s + 1.1s ≈ 22~27s
```

对比修改前（debounce 30s）：上限约 37~42s。debounce 降至 15s 后延迟缩短约 40%。

### 7.3 节点规模上限建议

| 节点数 | 建议 |
|--------|------|
| < 3 | 禁用 weighted balancing，保留 adaptive health tracking |
| 3~20 | 核心场景，完整功能 |
| 20~50 | 探活并发需要上限控制，建议 max(3, ceil(N/5)) |
| > 50 | 给出警告，建议按地区/用途拆分为多个 group |

### 7.4 TCP vs UDP/QUIC 隔离

```csharp
// 调度时强制按协议过滤，TCP 和 UDP 分数互不影响
var candidates = _nodes
    .Where(n => n.Protocol == requestedProtocol && !n.IsInCooldown)
    .ToList();
```

Phase 1 优先完成 TCP 池；UDP/QUIC 池在 Phase 2 跟进。

---

## 8. 关键代码骨架

### 8.1 编排器注册

实际实现中不使用 DI，而用 `AdaptiveSchedulerManager` 单例编排所有子模块：

```csharp
// AdaptiveSchedulerManager.Instance 持有所有子模块
// → ScoreCalculator (new)
// → CooldownFsm (new)
// → FailureCollector (new, 含 scorer + cooldown)
// → BootstrapProber (new)
// → ProbeService (new, 含 port resolver + collector)
// → ScoreLogger (new, 含 node list + log action)
// → ActiveSetManager (new, 含 node list)
// → IAdaptivePolicyApplier (外部传入, ReloadPolicyApplier)
```

### 8.2 调度逻辑（active-set uniform random，由 xray balancer 执行）

C# 侧不执行调度。调度由 xray `random` balancer 完成：
- active-set 内的节点在 selector 中各出现一次
- xray random strategy 均匀选择（去重后的 candidates）
- C# 控制面的职责是管理 active set（进出、cooldown、hysteresis），不是管理权重

```
C# GenAdaptiveConfig:
  tag_repeat = max(1, round(score / 25))  // score 100 → 4 副本

xray config:
  "balancer": { "strategy": "random", "selector": [A, A, A, B] }
  → xray random 等概率选出 → P(A) = 75%, P(B) = 25%
```

ActiveConnections 死代码已清理（该设计被替换后无调用者）。

### 8.3 可观测性：分数快照日志（JSONL）

```csharp
// 每 30s 输出一次节点分数快照，存入独立 adaptive.log，JSONL 格式
// 事件类型: score_snapshot, cooldown_enter, active_set_change, xray_reload
// 格式要求: 每行一个 JSON 对象，支持 jq 解析和事件回放

{"time":"2026-05-21T14:30:00Z","type":"score_snapshot","node":"HK-A","score":87.3,"latencyMs":95,"lossRate":0.01,"cooldown":false}
{"time":"2026-05-21T14:30:05Z","type":"cooldown_enter","node":"US-B","score":12.4,"latencyMs":1820,"lossRate":0.42,"consecutiveFails":3}
{"time":"2026-05-21T14:35:00Z","type":"active_set_change","active":["HK-A","JP-C"],"explorer":["SG-D"],"cooldown":["US-B"]}
{"time":"2026-05-21T14:35:02Z","type":"xray_reload","trigger":"active_set_change","debounceMs":12000,"durationMs":1840}
```

日志文件独立于 xray 日志，推荐路径 `{v2rayN}/adaptive.log`。
主界面需提供"查看 Adaptive 日志"入口（至少打开文件）。

### 8.4 Emergency Feature Flag（紧急旁路）

任何足够复杂的功能都需要一键回退路径。出问题时用户不应需要进入多层设置：

```csharp
// 全局紧急旁路，不需要重启软件
public void EmergencyDisableAdaptive()
{
    _config.AdaptiveSchedulerItem.Enabled = false;
    _probeCts?.Cancel();       // 立即停止所有探活任务
    _scoreLogger?.Stop();      // 停止日志
    _ = _policyApplier.RestoreDefaultConfigAsync();  // 恢复 xray 默认配置
    _isRunning = false;
    // Log: "Adaptive scheduling emergency-disabled by user."
}
```

UI 要求：主界面或系统托盘菜单提供"关闭 Adaptive（紧急）"选项，一键执行。

---

## 9. 行动计划（v3.0，基于 2026-05-21 综合评审）

> 上一版 Phase 计划已过时。以下是根据实现审计 + OpenAI 同行评审修订后的完整行动计划。

### P0 — 立即执行（1~3 天，阻断上线的问题）

| # | 任务 | 具体内容 | 验收条件 |
|---|------|---------|---------|
| ~~0.1~~ | **验证 xray tag duplication 行为** ✅ 已完成 | 集成测试：`[A×3, B×1]` selector，N=1000 请求，xray v26.3.27。**结论：selector 去重，duplication 无效，A=51.1%≈50%**。系统重定位为 Active-Set Scheduler | 测试保留为 xray selector 行为契约检测器 |
| 0.2 | **FailureType 差异化惩罚** | TlsError 不惩罚 EWMA；Refused 强惩罚(loss=1.0)；Timeout(loss=0.8)；NetworkError(loss=0.7)；UnexpectedEof(loss=0.4) | unit test 覆盖每种 FailureType 的 EWMA 更新行为 |
| 0.3 | **Bootstrap 覆盖历史分数验证** | 代码审查 + unit test：历史高分 90 → Bootstrap TCP connect 失败 → 分数降至 1.0 | unit test 通过；文档明确"Bootstrap 始终覆盖历史" |
| 0.4 | **Active Set Hysteresis** | Entry=60, Exit=35；在 ActiveSetManager 中实现迟滞逻辑 | unit test：score 在 45~55 抖动时，active set 不频繁变化 |
| 0.5 | **Adaptive Feature Flag 紧急旁路** | `EmergencyDisableAdaptive()`；UI 一键可达 | 功能可用；关闭后 xray 恢复默认配置 |

### P1 — 近期执行（3~7 天，功能完整性）

| # | 任务 | 具体内容 | 验收条件 |
|---|------|---------|---------|
| 1.1 | **debounce 从 30s 降至 10~15s** | 修改 `ReloadPolicyApplier`；实测 xray 重启耗时并写入文档 | 调度响应延迟上界降至 ~20s |
| 1.2 | **ScoreLogger → adaptive.log（JSONL）** | 独立日志文件；JSONL 格式含 score_snapshot、cooldown、active_set_change、xray_reload | 日志可直接用 jq 解析；主界面有打开入口 |
| 1.3 | **ActiveSetManager top-K 逻辑文档化** | K 计算公式、explorer 比例、explorer 选取策略写入设计文档 | 代码注释与文档一致 |
| 1.4 | **AdaptiveSchedulerManager 生命周期** | 移除/封装静态单例；文档化 profile 切换处理流程；确认 `IAsyncDisposable` | 切换 group 时探活任务正确重启，无资源泄漏 |
| 1.5 | **xray 版本兼容性检查** | 启动时验证版本，不满足最低验证版本时禁用 adaptive 并告警 | 低版本 xray 时功能自动降级 |
| 1.6 | **ProbeUrl 暴露到 Settings UI** | 输入框读写 `AdaptiveSchedulerItem.ProbeUrl`，修改后重启 ProbeService | 用户可配置 ProbeUrl |
| 1.7 | **分数过期机制** | 历史分数超过 4h 强制回退到 50 | unit test：加载 5h 前历史分数，验证被重置 |

### P2 — 中期执行（1~2 周，稳固性）

| # | 任务 | 具体内容 | 验收条件 |
|---|------|---------|---------|
| 2.1 | **XrayStatsPoller** | 5s 轮询 `/debug/vars`；高分低吞吐（< 1KB/s）触发补探活 | 高分节点吞吐归零后 10s 内触发探活 |
| 2.2 | **边界情况：1/2 节点处理** | 1 节点禁用 weighted balancing；2 节点允许最多 1 个 cooldown | unit test 覆盖 1、2、3 节点场景 |
| 2.3 | **PerTagProxyTraffic 线程安全** | 改为 `ConcurrentDictionary<string, NodeTrafficSnapshot>`（record） | 无数据竞争；类型可序列化 |
| 2.4 | **ProbeService 并发上限 + 压力测试** | 探活并发上限 max(3, ceil(N/5))；50 节点场景压力测试 | 资源占用有文档上界 |
| 2.5 | **Replayable Telemetry 完整事件** | JSONL 事件包含 probe_result、ewma_update、xray_reload 等完整链路 | 能从日志重现任意时间段的调度决策链 |
| 2.6 | **探活多目标支持** | 支持配置多个 ProbeUrl；结果取平均 | 配置 2 个探活目标时，两者都超时才判定失败 |

### P3 — 长期执行（仅在真实用户场景证明必要时启动）

| # | 任务 | 说明 |
|---|------|------|
| 3.1 | RuntimePolicyApplier | 通过 xray runtime API 实现零中断切换，替代 ReloadPolicyApplier（依赖 xray API 支持） |
| 3.2 | 调度质量指标（熵、P95 延迟） | 每 5 分钟计算并写入日志，作为观测指标（不作为验收标准） |
| 3.3 | UDP/QUIC 独立节点池 | 依赖 RuntimePolicyApplier 完成后实现 |
| 3.4 | 调度决策审计日志 UI | Telemetry 查看器，内嵌到 v2rayN 设置页 |
| 3.5 | 节点规模上限警告 | > 50 节点时提示拆组 |
| 3.6 | 外部 balancer / true weighted routing | **当前禁止在 P1/P2 阶段实施。** 仅在真实用户反馈证明 active-set uniform random 不足以满足需求时，重新评估此需求。启动前必须在 xray 源码级验证 selector 行为 |

---

## 10. 验收标准

### 10.1 核心调度行为（必须全部通过才能上线）

| 测试场景 | 预期行为 | 验证方法 |
|---------|---------|---------|
| 全部节点 cooldown | 选 cooldown 剩余最短节点，不崩溃 | unit test |
| 节点连续 2 次失败 | 进入 cooldown，其他节点接管 | unit test |
| cooldown 节点数达到 1/3 | 第 1/3+1 个失败节点降权而非 cooldown | unit test |
| Bootstrap 发现死节点 | Score=1.0，不自动进 cooldown | unit test |
| 历史分数 90 + Bootstrap 失败 | 分数覆盖为 1.0（Bootstrap 始终覆盖历史） | unit test |
| TlsError 失败 | EWMA 不更新，触发独立告警 | unit test |
| xray selector 去重行为 | `[A×3, B×1]` selector，1000 请求 A≈50%（证实去重） | integration test（CI 可重复，作为行为契约检测器） |
| active-set 内均匀分配 | active-set 内各节点流量接近均匀 | integration test |
| score 在 45~55 抖动 | active set 不频繁变化（hysteresis 生效） | unit test |
| 紧急旁路触发 | xray 恢复默认配置，adaptive 停止，不崩溃 | integration test |

### 10.2 用户体验指标（可量化，观测用，不作为阻断条件）

| 指标 | 目标值 | 测量方法 |
|------|--------|---------|
| 节点质量变化响应时间 | ≤ 25s（含 debounce 15s + xray 重启 ~1.1s，实测） | 实测：人为降低节点质量，观察切换时间 |
| 好节点 vs 差节点选中概率比 | ≥ 3:1（score 差 50 分时） | 从 adaptive.log 计算 |
| 冷启动后首次请求成功率 | ≥ 95%（Bootstrap 完成后） | 实测：重启 10 次，记录首次请求结果 |
| active set reload 频率 | < 4 次/小时（正常网络环境） | 从 adaptive.log 统计 xray_reload 事件 |

---

## 11. 关键决策备忘

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
| FailureType 惩罚 | 按类型差异化 (TlsError=0,Refused=1.0,Timeout=0.8) | 不同错误根因不同，统一惩罚导致 EWMA 失真 (P0 fix) |
| Active Set 迟滞 | Entry=60, Exit=35, 缓冲带 25 分 | 防 score 震荡导致频繁 reload (P0 fix, OpenAI 新增) |
| Bootstrap 覆盖策略 | 始终覆盖历史分数（包括高分） | 历史高分可能来自已死亡节点，Bootstrap 是当前真实状态 |
| 分数过期 | 历史分数超过 4h 回退到 50 | 长时间关机后历史分数无效，不信任过期数据 |
| 探活多目标 | 建议支持多个 ProbeUrl，取平均值 | 减少单一目标（如 `cp.cloudflare.com`）的偶发抖动影响 EWMA |
| Emergency Feature Flag | 一键关闭 adaptive + 恢复默认配置 | 任何复杂功能都需要快速回退路径 |
| xray 版本依赖 | tag duplication 行为需最低版本号 | 上游去重会静默破坏调度，必须版本检查 + CI 集成测试 |
| ScoreLogger 格式 | JSONL，独立文件，可回放 | 出问题时能重现调度决策链 (OpenAI 建议) |
| 节点规模上限 | <3 禁用 adaptive active-set (uniform random 无意义); >50 警告拆组 | 小节点数不值得调度，大节点数探活压力不可控 |
| xray tag duplication 弃用 | 2026-05-21 集成测试证实 xray v26.3.27 对 selector 去重，duplication 无效 | 整个 weighted scheduling 假设不成立；系统重定位为 Active-Set Scheduler |
| Active-Set vs Weighted | 当前版本 = active-set + cooldown + hysteresis + uniform random；不是 weighted LB | 核心目标"动态剔除坏节点"而非"精确概率分流" |
| **禁止 weighted routing hack** | 任何新的加权方案必须在 xray 源码级验证 selector 行为后才能进入设计 | tag duplication 已被证伪；Phase 1/2 坚持 active-set uniform random |

---

## 12. 实时节点速度显示（Real-time Node Speed Display）

**方案设计：Claude** | 日期：2026-05-21

### 12.1 问题

主界面表格的 Speed 列只在手动测速时才更新。Adaptive 负载均衡运行时，右下角状态栏有总速度，但无法知道每个节点当前的实际吞吐量。

### 12.2 实现方案

不改表结构、不新增列、不新增事件。利用已有的统计管线数据直接写到现有 Speed 列的显示属性。

### 12.3 数据流

```
xray /debug/vars (1s 轮询)
  → StatisticsXrayService.ParseOutput()
    → PerTagProxyTraffic: { tag → (up KB, down KB) }     ← 已有
  → StatisticsManager.UpdateServerStat()
    → 对每个 tag 计算 delta，生成 child ServerSpeedItem   ← 已有
      { IndexId = childId, ProxyUp, ProxyDown = 每秒 KB }
    → 通过 _updateFunc 发布                              ← 已有
  → ProfilesViewModel.UpdateStatistics()
    → 设置 item.SpeedVal = format(ProxyUp + ProxyDown)   ← 新增
```

### 12.4 改动的文件

| 文件 | 改动量 |
|------|--------|
| `ProfilesViewModel.cs` | +7 行 |

`UpdateStatistics()` 方法中，在更新 TodayUp/TodayDown 之后追加：

```csharp
// When adaptive scheduling is active, show per-node real-time throughput in Speed column
if (AdaptiveSchedulerManager.Instance.IsRunning)
{
    long totalKbps = update.ProxyUp + update.ProxyDown;
    if (totalKbps >= 1024)
        item.SpeedVal = $"{totalKbps / 1024.0:F1} MB/s";
    else if (totalKbps > 0)
        item.SpeedVal = $"{totalKbps} KB/s";
}
```

### 12.5 设计决策

| 决策 | 选择 | 理由 |
|------|------|------|
| 新增列？ | 否，复⽤现有 Speed 列 | 零 UI 改动，Speed 语义天然匹配 |
| 改 `Speed` 字段？ | 否，只写 `SpeedVal` | `Speed` 是持久化测速值，不应被实时数据覆盖 |
| 无流量时显示什么？ | 保持上次值或空 | 避免列频繁闪烁，流量恢复后自然更新 |
| 单位 | KB/s 自动升档 MB/s | 与现有速度显示风格一致 |
| Adaptive 关闭后？ | SpeedVal 自动恢复为测速值 | 下次表格刷新（`GetProfileItemsEx`）从 DB 读取原始值 |
