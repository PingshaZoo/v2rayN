# v2rayN Adaptive Node Scheduler — 完整设计与实现文档

**版本**: 7.3（2026-05-22 v7.3 P1 DNS + Probe 代码实现版）
**来源**: 合并自 v6.0 最终合并版 + Claude/DeepSeek/OpenAI 三方评审综合修订 + v7.1 10 项语义精炼 + v7.2 P0 C# 代码实现 + v7.3 P1 DNS 归因分离 + 随机载荷探活
**系统正式名称**: **Conservative Failure Isolation System**
**定位**: 三层描述 — 系统层 "Conservative Failure Isolation System" / 机制层 "Adaptive Active-Set Health Controller" / 目标层 "Mixed-Flow Proxy Stability Controller"
**核心收益来源**: 真正的系统收益来自**减少错误调度**，而不是**提高调度精度**
**约束**: C# / Windows / v2rayN 架构，不 fork xray-core

---

> **⚠️ 核心定性声明（必读）**
>
> **本系统不是智能负载均衡器。**
> **本系统是 Conservative Failure Isolation System（保守故障隔离系统）。**
>
> 系统的核心价值**不在于精确选出最优节点**，而在于**减少错误调度**：
> — 不把流量发给坏节点
> — 不因 score 震荡频繁切换 active-set
> — 不让探测流量混入生产流量
>
> 系统**不负责**寻找"最快节点"或"最优节点"。
> 系统**只负责**：
> - 淘汰明显异常节点
> - 防止控制面震荡
> - 最大化 mixed-flow stability（混合流稳定性）
>
> **成功标准不是 routing precision，而是"用户极少需要手动干预"。**

---

## 目录

1. [系统定位与设计哲学](#1-系统定位与设计哲学)
2. [ChatGPT 原始方案错误点分析](#2-chatgpt-原始方案错误点分析)
3. [v2.0 修订说明](#3-v20-修订说明)
4. [最终架构](#4-最终架构)
5. [系统能力边界与非目标](#5-系统能力边界与非目标)
6. [已知局限性与可观测性边界](#6-已知局限性与可观测性边界)
7. [数据结构设计](#7-数据结构设计)
8. [模块详细设计与实现](#8-模块详细设计与实现)
9. [评分公式与数学基础](#9-评分公式与数学基础)
10. [节点状态机与生命周期](#10-节点状态机与生命周期)
11. [全局不稳定性冻结机制](#11-全局不稳定性冻结机制)
12. [用户手动操作优先级](#12-用户手动操作优先级)
13. [DNS 故障域隔离](#13-dns-故障域隔离)
14. [设计演进：关键变更、冲突与解决](#14-设计演进关键变更冲突与解决)
15. [P1 稳定性增强实现](#15-p1-稳定性增强实现)
16. [实施行动计划与完成记录](#16-实施行动计划与完成记录)
17. [验收标准与测试覆盖](#17-验收标准与测试覆盖)
18. [关键决策备忘](#18-关键决策备忘)
19. [实时节点速度显示](#19-实时节点速度显示)
20. [未来方向：被动混合流观测器](#20-未来方向被动混合流观测器)
21. [真正成功标准](#21-真正成功标准)

**附录**

- [附录 A：优先级总表](#附录-a优先级总表)
- [附录 B：文档来源与版本历史](#附录-b文档来源与版本历史)

---

## 1. 系统定位与设计哲学

### 1.1 两层描述模型（v7.0 修订）

本系统使用两层描述来完整定义其职责范围：

| 层次 | 名称 | 关注点 |
|------|------|--------|
| **机制层** | Adaptive Active-Set **Health** Controller | 怎么工作：failure detection + cooldown FSM + hysteresis + active-set management |
| **目标层** | Mixed-Flow Proxy **Stability** Controller | 为什么工作：混合流长期稳定，用户极少手动干预 |

> **v7.0 重要更名**：目标层从 "Adaptive Media QoS Scheduler" 正式改为 "Mixed-Flow Proxy Stability Controller"。原名称存在根本性误导：系统无法测量真实 QoS，也无法测量媒体体验。真实目标是混合流稳定性，而非媒体优化。

### 1.2 系统演进路径

```
"理论 weighted scheduler"
        ↓ 收敛（P0.1 tag duplication 被证伪）
"Adaptive Active-Set Scheduler"
        ↓ 哲学收敛（v7.0 三方评审）
"Adaptive Active-Set Health Controller"
  + "Mixed-Flow Proxy Stability Controller"
```

关键哲学转变：从"寻找最优节点"收敛为"淘汰明显坏节点"。这不是降级，而是对系统真实能力边界的诚实认知。

### 1.3 真实用户流量模型

> **v7.0 修正**：原文档过度聚焦于"媒体场景"，但真实用户流量是**混合业务流**，必须以此为基础建模。

#### 真实混合流量构成

| 流量类型 | 特征 | 占比估计 |
|---------|------|---------|
| 网页小请求 | latency-sensitive，短连接，大量并发 | 高 |
| Twitter/图片 CDN | 高频小文件，中等延迟敏感 | 中 |
| Telegram / Discord WebSocket | jitter-sensitive，长连接，低带宽持续 | 中 |
| YouTube / 视频下载 | throughput-sensitive，长流 chunk | 中 |
| GitHub 克隆 / 大文件下载 | 纯吞吐，延迟不敏感 | 低 |
| ChatGPT SSE / 流式 API | latency-sensitive，持续小包 | 低 |
| 后台探测 / DoH | background noise | 低 |

**结论**：真正应该优化的不是"视频体验"，而是**混合流整体稳定性**。单一媒体场景优化会牺牲其他类型流量的体验。

#### 用户真正的痛点

- 某节点突然 timeout，导致整批请求失败
- 晚高峰节点爆炸，所有流量类型同时恶化
- 自动调度震荡，长连接（Telegram、WebSocket）频繁断流
- reload 打断进行中的下载或视频流

**不是** `75% vs 25% routing precision`，**也不是** `YouTube 4K 是否流畅`（这根本无法测量）。

### 1.4 核心设计原则（Failure-Driven Philosophy）

#### 核心哲学（v7.0 正式确立）

```
系统默认假设：所有健康节点都"足够好"。
系统不负责寻找"最快节点"。
系统只负责：淘汰明显坏节点。
```

这是整个架构稳定的根基。接受这个前提，所有设计决策才能自洽。

#### Stability Objective（统一目标函数，v7.0 修订）

```
旧目标：最小化 active-set churn，同时最大化 healthy-node exposure
新目标：最小化不必要的连接中断，同时渐进排除持续降级节点
```

具体含义：
- **不追求最优**：不尝试找 latency 最低的节点
- **不追求最大 exposure**：不激进地把所有"看起来健康"的节点加进 active-set
- **只追求"不明显坏"**：淘汰那些持续、明显故障的节点

**优先级铁律（不变）**：
```
稳定性 > 响应性 > 最优性
```

#### Phase 1 目标（当前，已实现）

```
Conservative Health Scheduler — 保守的健康调度
```

- 坏节点自动淘汰，不雪崩
- active-set 稳定，不震荡
- reload 不频繁，长流不中断
- 全局故障时不自激（冻结机制）
- 用户手动操作优先级最高

**不是**：精确调度最优节点，也不是媒体 QoS 推断。

#### Phase 2 目标（未来，未开始）

```
Runtime Routing Mutation — 运行时路由变异
```

即：active-set 变化**不需要 reload** 就能生效。这比任何 heuristic 改进都更有 UX 价值。

#### Phase 3 目标（远期，研究性质）

```
Passive Mixed-Flow Degradation Observation — 被动混合流降级观测
```

注意：不是"媒体 QoS inference"，而是"passive degradation observation"。只观察，不直接调度。

---

## 2. ChatGPT 原始方案错误点分析

### 2.1 EWMA α 是魔法数字，缺乏理论依据

**错误做法**

```text
new = old × 0.8 + current × 0.2   (α = 0.2，固定)
```

**为什么错**

α 编码的是"多久以前的数据开始失去意义"。TCP 拥塞控制（RFC 6298）的 SRTT α=1/8 是基于采样频率和 RTT 量级的严格推导。代理场景的致命差异在于**采样间隔极不均匀**：用户看视频时每秒数十个连接，看文章时可能 10 分钟一个请求。固定 α 的后果：

- 高频场景：α 过小，节点变差了 30 秒还没反应
- 低频场景：10 分钟前的 EWMA 被当成当前质量，应衰减的没衰减，严重失真

**正确做法：time-decayed EWMA**

```csharp
double DecayedAlpha(DateTime lastObserved) {
    double ageSec = (DateTime.UtcNow - lastObserved).TotalSeconds;
    return 0.05 + 0.25 * Math.Exp(-ageSec / 60.0);
}
```

### 2.2 "TCP connect duration" 在 v2rayN 架构下物理上拿不到

v2rayN 的流量链路：

```
本地 SOCKS5/HTTP → xray inbound → router → outbound → 远端节点
```

TCP 三次握手和 TLS 握手在 xray-core 内部完成，对外只暴露 Stats gRPC API，内容仅为每个 outbound tag 的 `bytes_sent` / `bytes_recv` 累计值。用户态 C# 代码物理上无法获取 socket 级延迟。

| 可观测 | 不可观测 |
|--------|---------|
| 应用层 TTFB（需主动探测） | TCP 三次握手时间 |
| xray stats API 字节计数 | TLS 握手时间 |
| 连接结果（成功/失败/超时） | 单连接 RTT |
| 整体吞吐率（字节差值/间隔） | 单连接丢包率 |

**正确做法**：通过本地 HTTP 客户端向指定 outbound tag 发出轻量 HEAD 请求，测量 TTFB（Time To First Byte）作为延迟代理指标。

### 2.3 Cooldown 无全局约束，高并发下必然雪崩

用户瞬间打开 20 个标签页时的雪崩链：

```
1. 节点 A 收到 20 个并发连接
2. 3 个超时（GFW 正常干扰）→ 节点 A 进 cooldown
3. 所有流量压到节点 B → 节点 B 过载超时 → 进 cooldown
4. 全部节点 cooldown → 用户完全断网
```

这是经典**惊群 + 雪崩**。Envoy outlier detection 的 `max_ejection_percent` 默认 10% 正是为了防止这个场景。

**正确做法**：任意时刻最多 1/3 节点可处于 cooldown 状态，超出上限时改为降权而非封禁。

### 2.4 QUIC/HTTP3 场景下"连接固定"策略直接失效

- 一个 QUIC 连接可承载数百并发 stream，持续数小时
- 浏览器复用同一 QUIC 连接访问同一域名所有资源
- 连接固定到节点 A 后，即使 A 已经变差，所有流量被锁定

**正确做法**：TCP 节点池与 UDP/QUIC 节点池完全隔离，独立打分。优先实现 TCP 池。

### 2.5 Windows 计时精度问题导致延迟测量严重失真

Windows 系统定时器中断默认间隔 **15.6ms**。代理延迟通常在 50~200ms 量级，±15ms 意味着 **30% 的系统误差**。

**正确做法**：

```csharp
long t0 = Stopwatch.GetTimestamp(); // 基于 QPC，精度 < 1μs
double ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
```

### 2.6 纯被动方案无冷启动机制，开机第一次用必然命中死节点

系统刚启动时所有节点分数相同（或为空），第一批连接均匀分布到所有节点，包括已经宕机的节点。

**正确做法**：启动时并行对所有节点做一次轻量 TCP connect 探活（2s 超时），结果作为初始分数。

### 错误点汇总

| 问题 | ChatGPT 原始方案 | 正确方案 |
|------|----------------|---------|
| EWMA α | 固定 0.2 | time-decayed α，随观测间隔动态调整 |
| 延迟数据源 | TCP connect duration | TTFB via HTTP HEAD probe |
| 计时精度 | DateTime（误差 ±15ms） | `Stopwatch.GetTimestamp()`（< 1μs） |
| Cooldown 雪崩 | 无全局约束 | max_ejected ≤ 1/3 + jitter |
| QUIC 处理 | 完全忽略 | 独立节点池，不与 TCP 共享分数 |
| 冷启动 | 无初始化 | 并行 TCP 探活，初始化后放行流量 |
| 权重边界 | 未定义 | floor=1，cooldown FSM 处理封禁语义 |
| 全部节点 cooldown | 无兜底 | 选 cooldown 剩余最短节点降级服务 |

---

## 3. v2.0 修订说明

ChatGPT 在评审 v1.0 文档时提出了 5 条工程反馈，逐条评估：

### 3.1 `lock(this)` 是危险模式 ✅ 采纳

`lock(this)` 是 C# 经典反模式（CA2002 警告），外部代码持有同一对象引用时可能造成意外死锁。全部改为私有锁对象。

```csharp
// ❌
lock (node) { ... }

// ✅
public sealed class NodeState {
    private readonly object _lock = new();
}
```

### 3.2 每节点 HttpClient 有 socket exhaustion 风险 ✅ 采纳（改进方案）

简单地共享一个 `HttpClient` 无法按 outbound tag 路由；正确做法是每个 outbound tag 共享一个长生命周期的 `SocketsHttpHandler`，并由 `ProbeService` 统一管理生命周期（见 §7.3）。

### 3.3 "线程安全全靠 lock" 后期可能有锁竞争 ⚠️ 部分采纳

Phase 1 节点数通常 < 20，lock 竞争不是瓶颈。保留 lock，但将评分计算移到锁外（纯计算，不访问共享状态），降低临界区长度。

### 3.4 Phase 1 范围裁剪 ✅ 采纳

Phase 1 不做复杂 throughput learning、QUIC adaptive migration、大量 background probes。轻量核心先上线，稳定后再迭代。

### 3.5 "系统必须轻" ✅ 采纳，但补充边界

方向正确，但"轻"不等于"粗糙"。需要区分两类复杂度：

- **可以省略的复杂度**：QUIC migration、throughput learning、实时全局重算 → Phase 2/3 再做
- **不能省略的精确度**：time-decayed EWMA、cooldown 全局约束、`Stopwatch` 计时 → 这些是正确性保证

**原则**：该精确的地方必须精确，该简单的地方坚决简单。

---

## 4. 最终架构

### 4.1 设计原则

```
目标：用户感觉"系统自己会挑节点"
手段：被动观测 + 时间衰减学习 + active-set 管理
禁区：不碰 xray-core 内部 / 不做请求级切换 / 不做全局最优计算
定位：轻量 adaptive routing，不是 SD-WAN
```

**明确不做什么**

| 不做 | 原因 |
|------|------|
| DPI / 流量识别 | TLS + ECH 使 L7 信息趋近于零 |
| 请求级切换 | 同一 TCP 连接中途换节点导致协议状态机崩溃 |
| 全局最优计算 | 所有流量压单节点会把该节点压死 |
| 高频主动测速 | 干扰正常流量，且无法代表真实业务质量 |
| 复杂 ML 模型 | 维护成本远超收益，v2rayN 不是科研项目 |

### 4.2 架构层次（实际实现）

```
┌──────────────────────────────────────────────────────┐
│                  v2rayN 进程（C#）                    │
│                                                      │
│  ┌────────────────────────────────────────────────┐  │
│  │        Control Plane（控制面，不进入数据路径）     │  │
│  │                                                │  │
│  │  ScoreCalculator → FailureCollector             │  │
│  │         ↓              ↓                       │  │
│  │    NodeState[] ← CooldownFsm                    │  │
│  │         ↓                                      │  │
│  │    ActiveSetManager（top-K + hysteresis）        │  │
│  │         ↓                                      │  │
│  │    GenAdaptiveConfig（active-set unique tags    │  │
│  │                     + probe inbounds）          │  │
│  └───────────────────────┬────────────────────────┘  │
│                          │ 生成 xray config.json      │
└──────────────────────────┼───────────────────────────┘
                           │
┌──────────────────────────▼───────────────────────────┐
│              xray-core（完全黑盒，不修改）              │
│                                                      │
│  random balancer: active-set 内 uniform random 选择   │
│  selector 去重后每个 candidate 等概率                  │
│                                                      │
│  Stats API: bytes_sent/recv per outbound tag         │
└──────────────────────────────────────────────────────┘
```

**核心机制**：xray `random` balancer 对 selector candidates 做 prefix-match + **去重**（2026-05-21 集成测试证实，xray v26.3.27）。active-set 内的节点在 selector 中各出现一次，xray random strategy 均匀选择。C# 控制面的职责是管理 active set（进出、cooldown、hysteresis），不是管理权重。

**层间规则**：C# 不进 Data Plane；调度由 xray 完成；C# 只维护分数 + 生成配置。

**核心原则**：

```
v2rayN = control plane
xray-core = data plane
```

### 4.3 连接粒度

调度粒度选择**连接级**（TCP connection），不做请求级切换。现代浏览器会持续建立新连接（新 tab、新域名、HTTP/2 stream 重建、CDN chunk 新连接），自然产生持续的调度机会，无需强制在连接内切换节点。

### 4.4 Stability Objective（稳定性目标函数）

系统当前包含多个局部 heuristic：EWMA、cooldown、hysteresis、explorer、top-K、debounce、probe、throughput anomaly、reload budget。每个局部决策在自身逻辑内是正确的，但缺乏**统一的稳定性目标函数**来解释和约束所有决策。

**核心目标（v7.0 修订）**：

```
旧目标：最小化 active-set churn，同时最大化 healthy-node exposure
新目标：最小化不必要的连接中断，同时渐进排除持续降级节点
```

**具体含义**：
- **不追求最优**：不尝试找 latency 最低的节点，不尝试最大化 active-set 覆盖
- **不追求最快**：不根据瞬时 latency 波动调整 active-set
- **只追求"不明显坏"**：淘汰那些持续、明显故障的节点
- **observability 边界内决策**：承认 probe 系统性偏差、throughput 因果污染、reload 的 UX 成本

**优先级铁律（不变）**：

```
稳定性 > 响应性 > 最优性
```

系统宁可响应稍慢，也不能频繁 reload；宁可 active-set 略保守，也不能激进地加入未验证节点。当稳定性与最优性冲突时，**稳定性总是赢**。

**约束所有 heuristic 的统一框架**：

| Heuristic | 稳定性角色 | 违反稳定性时如何退让 |
|-----------|-----------|-------------------|
| Hysteresis（Entry=60/Exit=35） | 25 分缓冲带防止 score 震荡触发 reload | 已在稳定侧，无需退让 |
| Explorer 隔离 | explorer 仅通过 probe traffic 验证，不入 production selector | 彻底消除 explorer 触发 reload 的路径 |
| Debounce（15s） | 合并短时间内多次变更 | 超预算时自动延长至 60~120s |
| Reload Budget | 直接限制每小时 reload 次数 | Soft budget：超预算后 throttle 而非 deny |
| Cooldown Jitter（FNV-1a hash） | 防止群体同步恢复（recovery burst） | 确定性偏移，每个节点恢复时间点永不相同 |
| Throughput Anomaly | 检测高分低吞吐 | 当前仅 observer，不入 score，不入 active-set gate |

**关键原则**：

- 任何新 heuristic 在加入系统前，必须明确它对"稳定性 vs 响应性"权衡的立场
- 所有 magic number（60、35、15s、30s、6 次/小时）必须能回溯到稳定性目标
- 不可为了"更灵敏"或"更智能"而引入新的 reload 触发路径
- 当稳定性与最优性冲突时，**稳定性总是赢**

### 4.5 架构规则

```
v2rayN 禁止接管 socket
禁止本地 SOCKS 中间层
禁止 request-level dispatch
禁止 fake weighted hack
```

### 4.6 ControlPlaneLoop：单线程状态所有者（v7.1 新增）

当前系统使用多个 `lock` 对象保护各自模块的内部状态（`NodeState._lock`、`ActiveSetManager._lock`），这在模块内部是正确的。但跨模块的**复合状态变更**（probe 更新 score → FSM 触发 cooldown → active-set 重算 → reload 调度）目前没有统一的序列化点。长期会退化为 distributed shared mutable state——每个模块各自加锁，但整体执行顺序不可预测。

**ControlPlaneLoop 概念**：

```
所有 state mutation 最终串行提交到 ControlPlaneLoop

ProbeService (并发探活)
    │  结果通过 channel 提交
    ▼
ControlPlaneLoop (单线程消费)
    │  1. UpdateScore()          — NodeState 变更
    │  2. CooldownFsm.Evaluate() — FSM 状态迁移
    │  3. ActiveSetManager       — 重算 active set
    │  4. FreezeController       — 评估冻结条件
    │  5. ReloadPolicyApplier    — 调度 reload
    │
    ▼
xray config reload (异步，不阻塞 ControlPlaneLoop)
```

**关键约束**：
- **并发探活，串行提交**：probe 可以并发执行（I/O bound），但结果提交到 ControlPlaneLoop 是串行的
- **所有 FSM transition 在单一 owner 线程内完成**：避免两个并发 transition 看到不一致的 state snapshot
- **reload 指令本身是 fire-and-forget**：ControlPlaneLoop 发出 reload 后不等待，继续处理下一个事件
- **lock 仍然是必要的**（读路径）：UI 读取 `Score`、`IsInCooldown` 等属性时仍然通过 lock 保护，但写路径全部串行化

**与现有代码的关系**：当前 Phase 1 实现中，`MonitorActiveSet` 的 `while` 循环实际上充当了简化版的 ControlPlaneLoop——它串行执行"collect → evaluate → decide → apply"。Phase 2 应显式化这个设计，将 ControlPlaneLoop 抽取为独立组件，其他模块通过 channel 提交事件。这不会改变现有行为，但会明确 single owner 边界，防止未来新增事件源（如 DNS 事件、manual override 事件）直接跨模块调用导致竞态。

---

## 5. 系统能力边界

### 5.1 能做到

| 能力 | 实现方式 |
|------|---------|
| 自动淘汰坏节点 | cooldown FSM + active set 驱逐 |
| 自动恢复好节点 | cooldown 到期 + recovery probing + hysteresis 重新进入 |
| 动态 active set | score 驱动的 top-K + hysteresis 进出管理（explorer 仅 probe，不入 production） |
| 自适应学习 | time-decayed EWMA（观测越久远影响越小） |
| 冷启动保护 | Bootstrap 并行 TCP connect 探活，覆盖过期历史分数 |
| 防止震荡 | hysteresis 缓冲带（Entry=60/Exit=35）+ debounce 防抖（15s）+ stable hash jitter |
| 可回放 telemetry | JSONL 独立日志，每事件一行，决策可追溯 |
| 一键紧急旁路 | `EmergencyDisableAdaptiveAsync()`，恢复默认配置，不重启 |
| 多目标探活 | 支持配置多个 ProbeUrl，取平均 TTFB，全失败才判失败 |
| 运行时零中断切换 | RuntimePolicyApplier 双模策略：API 可用→零中断，不可用→fallback ReloadPolicyApplier |
| 调度质量观测 | Shannon 熵、P95 EWMA 延迟、均值/标准差，每 5 分钟写入日志 |
| Reload 节流 | 滑动 1 小时窗口自适应 debounce（15s/60s/120s），软节流不硬拒绝 |

### 5.2 做不到（当前架构约束）

| 限制 | 原因 |
|------|------|
| 真正 weighted routing | xray selector 对 candidates 做 prefix-match + dedup，tag 重复无效 |
| per-request balancing | 调度粒度为连接级（TCP connection），非请求级 |
| runtime probability shaping | xray 无动态 balancer API（`RandomStrategy.PickOutbound` 不可远程控制） |
| transparent QUIC migration | QUIC 连接语义与 TCP 完全不同，需独立节点池（Phase 3） |
| 全局最优计算 | 所有流量压单节点会压死该节点，必须维持 active set 分散 |
| 真实媒体流质量测量 | HEAD probe = small-object latency，与长流媒体的 sustained congestion quality 相关性弱（这是已知系统性约束） |
| true weighted warmup | xray active-set 内 uniform random，无法让恢复节点只承接 30% 流量（Warmup 节点必须仅接收 probe traffic） |
| low-weight recovery routing | 无法渐进增加恢复节点权重，score 无法映射 traffic ratio |
| runtime probability shaping | xray 无动态 balancer API，无法运行时调整 per-node 流量比例 |
| 精确故障归因（DNS vs Node） | 当前 FailureType 无 DNS 独立类型，DNS 故障与节点故障共用 Timeout（Phase 2 改进） |
| cross-flow fairness optimization | 当前系统根本不知道哪个连接是视频、WebSocket、SSH、ChatGPT SSE，所以无法真正优化 mixed-flow fairness，现在只能避免 catastrophic degradation |

### 5.3 当前禁止事项

```
禁止继续在 weighted routing 上叠 hack
```

包括：

- duplicated outbounds
- fake weighted selector
- synthetic replicas

原因：xray 不是 programmable weighted LB engine，它是 **transport runtime**。tag duplication 已被集成测试证伪（xray selector 去重）。任何新的加权方案必须在 xray 源码级验证 selector 行为后，才能进入设计阶段。

### 5.4 架构上界：为什么不是算法不够智能，而是 runtime capability 不存在（v7.1 新增）

任何 proxy control-plane 系统的能力上限由 data-plane runtime 的 API surface 决定。在当前 xray 架构下：

| xray 缺失的 runtime capability | 对 control-plane 的直接影响 |
|------|------|
| 无 per-outbound weighted routing | selector dedup 导致 tag duplication 加权无效，score 无法映射 traffic ratio |
| 无动态 balancer API（`RandomStrategy.PickOutbound` 不可远程控制） | active-set 变更只能通过 config reload，reload = 连接中断 |
| 无 per-connection RTT | 只能通过应用层 probe 间接测量 latency，且受 probe bias 污染 |
| 无 per-connection 状态暴露 | 无法知道哪个连接正在用哪个 outbound，无法做 per-connection 调度 |
| 无 QUIC connection migration | QUIC 节点需独立池处理，无法与 TCP 节点共用 active-set |
| config reload 必然中断已有连接 | reload 频率受 budget 严格控制，牺牲响应性换取稳定性 |
| Stats API 只暴露累计字节数 | 无法获取 per-connection throughput，只能推算粗粒度 aggregate |

**核心结论**：当前系统的天花板不是 heuristic 不够聪明，而是 **xray runtime 的 API surface 几乎不存在**。C# control-plane 能在这些约束下实现"坏节点自动消失 + 好节点自动恢复 + 无震荡"已经是当前架构的上界。未来任何能力提升（warmup routing、per-connection scheduling、true weighted distribution）的前提是 xray 提供新的 runtime API，而不是在 C# 侧继续优化启发式算法。

---

## 6. 已知局限性与可观测性边界

> **这是当前整个系统最重要的限制声明。后续所有设计决策都以此为边界。**

### 6.1 核心声明：HealthScore ≠ UX Score

```
HealthScore 本质上是 Reachability Score。
不是 UX Score。
不是 Media Quality Score。
不是 Throughput Score。
```

当前系统通过 HEAD probe + EWMA 计算出的分数反映的是**节点的可达性与基本传输健康度**，不是用户的真实体验质量。这个区分不是实现缺陷，而是**系统性约束** —— 在 xray 不暴露 socket 级指标、不解密 TLS 流量的架构下，任何试图将 HealthScore 等同于 UX Score 的设计都是错误的。

### 6.2 系统可以测量的信号

| 可测量 | 含义 | 可靠性 |
|--------|------|--------|
| reachability | 节点是否可达（TCP/TLS 握手成功） | 高 |
| probe latency (TTFB) | 通过节点发送 HTTP HEAD 请求的首字节延迟 | 中（受机场 probe 优化影响） |
| connection failure | 连接失败类型（Timeout / Refused / NetworkError / TlsError） | 高 |
| basic transport health | 连续失败次数、失败模式 | 高 |
| throughput (bytes/sec) | 通过 xray Stats API 获取 per-tag 字节数差值 | 低（受用户行为污染） |

### 6.3 系统无法测量的信号

| 无法测量 | 原因 |
|---------|------|
| sustained media QoS | 不解密 TLS 流量，无法知道视频 chunk 是否被缓冲 |
| congestion quality | cwnd / retransmission state 在 xray 内核中，C# 无法访问 |
| buffering frequency | 无法区分"用户暂停"和"节点卡顿" |
| HTTP/2 multiplexing stall | H2 流控信息在传输层以下 |
| QUIC pacing quality | QUIC 状态机在 xray 内核中 |
| 单连接 RTT | xray Stats API 只暴露累计字节数，不暴露 per-connection RTT |

### 6.3.1 复合路径的归因困境（v7.1 新增）

当前 probe latency 测量的是**复合路径指标**，不是纯节点质量：

```
probe latency = f(client → local ISP → proxy node → remote ISP → CDN edge)
```

系统**无法区分**以下干扰因素：

- **节点拥塞** vs **上游 ISP QoS throttling**：延迟上升可能是节点本身问题，也可能是 ISP 在特定时段（晚高峰）对跨境流量限速
- **CDN edge routing 变化**：probe 目标域名的 CDN edge 切换可能导致延迟变化，与节点质量无关
- **用户本地网络抖动**：WiFi 干扰、路由器 buffer bloat、本地 ISP 临时波动

因此 **低 latency ≠ 节点更好**。probe 返回 80ms vs 200ms 的差异可能完全来自 CDN edge 距离或 ISP 路由变化，而非节点本身的质量差异。HealthScore 的 latency 维度本质上是"这条复合路径的当前表现"，不是"这个节点的固有质量"。这个 distinction 是理解系统边界的核心。

### 6.4 机场环境 Probe 系统性偏差

机场运营方对 probe 域名的特殊优化是一个严重但文档讨论不足的问题：

- 大量机场对 `www.gstatic.com/generate_204` 做 CDN 缓存或 Anycast 加速，该域名本身就有 Google 全球 CDN 加速，经机场代理后延迟测量失去参考意义
- 部分机场对小包/空包连接有专用优化通道（small-packet acceleration），HEAD 请求的 payload 极小，可能走快通道，而真实流量的 TCP 窗口行为完全不同
- 机场可能对常用 probe 域名做 prioritize routing，probe-domain prioritization 使得 probe 流量得到特殊待遇
- 结果：**probe 延迟可能系统性低估真实业务 RTT 30%-60%，HealthScore 系统性虚高**

**缓解方向**（当前未实施，Phase 2 评估）：混合多个 probe 目标（不只 generate_204）、加入随机大小 payload 的 probe 模拟真实流量、允许用户自定义 probe URL。但需注意这些措施会增加 probe 开销，需要权衡频率。

### 6.5 Probe 的正确使用边界

```
Probe 只用于：
  - failure detection（节点是否彻底不可达）
  - reachability estimation（节点是否存活）
  - basic latency comparison（同类型节点间相对比较）

Probe 禁止用于：
  - QoS optimization（不能根据 probe 延迟优化媒体体验）
  - media quality inference（不能推断视频缓冲频率）
  - throughput prediction（不能预测大文件下载速度）
  - "best node" selection（不能声称找到了最优节点）
```

### 6.6 Throughput 信号的因果性错乱

吞吐量信号存在根本性的**因果倒置（causality inversion）**问题：

```
用户行为 → throughput（不是 node quality → throughput）

用户看 4K → throughput 高 → 系统以为节点好 ✓（实际是用户在用）
用户没流量 → throughput = 0 → 系统以为节点差 ✗（实际是没人用）
用户暂停视频 → throughput 下降 → 被误判为节点降级
CDN cache hit → throughput 瞬时高 → 被误判为节点恢复
```

吞吐量测的是"用户在做什么"，不是"节点有多好"。这不是信号质量问题，是**测量对象和目标变量根本不对齐**。

**正确使用方式**：
- **禁止**直接进入 HealthScore
- **禁止**直接影响 cooldown 决策
- **禁止**直接影响 active-set membership
- **仅允许**作为组合异常检测的辅助条件（high score + 长期极低 throughput + 持续失败三者同时成立才触发 suspicion）
- **仅允许**用于 telemetry 和 debugging hint

### 6.7 active-set 内部分配的现实约束

当前 active-set 内的流量分配是 xray random balancer 的 uniform random，**无法实现**以下能力：

- true weighted warmup（无法让恢复节点只承接 30% 流量）
- low-weight recovery routing（无法渐进增加恢复节点权重）
- runtime probability shaping（无法动态调整 per-node 流量比例）

这意味着 **Warmup 节点不能直接进入 production selector**——如果进入，它将与所有健康节点以相同概率承接流量，可能导致刚恢复的节点被大量流量压垮。Warmup 节点只能接收 probe traffic，必须在确认稳定后才能加入 production selector。这个约束必须在设计文档中明确，否则后续开发者会误以为 score 可以映射 traffic ratio——实际上 xray 做不到。

---

## 7. 数据结构设计

### 6.1 NodeState（实际实现）

```csharp
public enum ProxyProtocol { Tcp, Udp }

public sealed class NodeState
{
    // ── identity（只读，初始化后不变）──────────────────────────
    public string Tag          { get; init; }  // xray outbound tag，唯一标识
    public string Host         { get; init; }  // 用于 Bootstrap TCP 探活
    public int    Port         { get; init; }
    public ProxyProtocol Protocol { get; init; } // Tcp | Udp
    public string ChildIndexId { get; init; }  // 关联 ProfileItem 的 IndexId

    // ── scoring state（受 _lock 保护）──────────────────────────
    private readonly object _lock = new();

    private double _score         = 50.0;  // [1.0, 100.0]
    private double _ewmaLatencyMs = 500.0; // 初始假设 500ms
    private double _ewmaLossRate  = 0.10;  // 初始假设 10%
    private DateTime _lastObserved = DateTime.MinValue;
    private int _consecutiveFailures;
    private DateTime _cooldownUntil = DateTime.MinValue;

    // 只读属性（double 读在 x64 上是原子的，无需锁）
    public double Score         => _score;
    public double EwmaLatencyMs => _ewmaLatencyMs;
    public double EwmaLossRate  => _ewmaLossRate;
    public DateTime LastObserved => _lastObserved;
    public int ConsecutiveFailures => _consecutiveFailures;
    public bool IsInCooldown => DateTime.UtcNow < _cooldownUntil;
    public DateTime CooldownUntil => _cooldownUntil;

    // 批量更新（进锁一次，减少竞争）
    public void UpdateScore(double latencyMs, double lossRate,
                            double score, int consecutiveFailures) { ... }

    public void SetCooldown(DateTime until) { ... }
    public void ResetCooldown() { ... }

    // 快照读（测试 / 日志用）
    public NodeSnapshot Snapshot() { ... }
}

public record NodeSnapshot(string Tag, double Score, double LatencyMs,
                           double LossRate, bool InCooldown, DateTime CooldownUntil);
```

**设计说明**：
- `_lock` 是私有对象，消除 `lock(this)` 的外部竞争风险
- double 字段读取在 x64 平台上是原子的，属性读取无需锁
- `UpdateScore` 批量写入，一次进锁完成，减少锁持有时间
- `ChildIndexId` 用于关联 ProfileItem，支持分数持久化和统计归属

### 6.2 AdaptiveConfig

```csharp
public record AdaptiveConfig
{
    public required List<string> ActiveTags { get; init; }
    public required List<string> CooldownTags { get; init; }
    public required IReadOnlyDictionary<string, int> ProbePorts { get; init; }
    public IReadOnlyDictionary<string, double> NodeScores { get; init; } = new Dictionary<string, double>();
    public IReadOnlyDictionary<string, string> TagToIndexId { get; init; } = new Dictionary<string, string>();
}
```

### 6.3 FailureType 枚举

```csharp
public enum FailureType { None, Timeout, Refused, TlsError, NetworkError, UnexpectedEof }
```

### 6.4 ConfigGeneration：单调版本号系统（v7.1 新增）

当前 `AdaptiveConfig`、active-set、reload 都是 mutable runtime state，但没有 generation id 来防止竞态。典型危险场景：

```
T1: MonitorActiveSet 计算出 active-set {A,B,C}
T2: ReloadPolicyApplier 创建 reload task（debounce 15s 后执行）
T3: 用户触发 Manual Override，active-set 变为 {D,E,F}
T4: T2 的 reload task 终于执行 → 将 Manual Override 覆盖为 {A,B,C} ✗
```

**解决方案**：引入单调递增的 `ConfigGeneration`。

```csharp
// 全局单调递增，线程安全（Interlocked.Increment）
public static class ConfigGeneration
{
    private static long _current;

    /// <summary>返回新 generation。每次 active-set 或 manual override 变更时调用。</summary>
    public static long Next() => Interlocked.Increment(ref _current);

    /// <summary>当前 generation（用于比较）。</summary>
    public static long Current => Interlocked.Read(ref _current);
}
```

**使用规则**：

```
1. 每次 active-set 变更 → ConfigGeneration.Next()
2. Manual Override → ConfigGeneration.Next()
3. ReloadPolicyApplier 创建 reload task 时捕获当前 generation:
   long capturedGen = ConfigGeneration.Current;
4. Reload 执行前检查:
   if (ConfigGeneration.Current != capturedGen) {
       // 已经有更新的 config，放弃本次 reload
       return;
   }
5. xray reload 完成后不递增 generation（reload 是 apply，不是 change）
```

**AdaptiveConfig 扩展**：

```csharp
public record AdaptiveConfig
{
    // ... 现有字段 ...
    public long Generation { get; init; }  // 此 config 对应的 generation
}
```

**为什么不用 GUID/DateTime**：GUID 无法比较先后（只知道不同，不知道谁新谁旧）。DateTime 在 Windows 上可以回拨（NTP 同步）。单调 `long` 是最简单、最确定、最不容易出错的方案。

---

## 8. 模块详细设计与实现

### 8.1 BootstrapProber — 冷启动探活

**目的**：消除冷启动盲区，确保调度器启动时有可用的初始分数。

**实现** (`BootstrapProber.cs`)：

```csharp
public sealed class BootstrapProber {
    private const int TcpTimeoutMs = 2000;
    private const int GlobalTimeoutMs = 3000; // 整体不超过 3s

    public async Task InitializeAsync(IReadOnlyList<NodeState> nodes, ScoreCalculator scorer) {
        using var cts = new CancellationTokenSource(GlobalTimeoutMs);
        var tasks = nodes
            .Where(n => n.Protocol == ProxyProtocol.Tcp)
            .Select(n => ProbeOneAsync(n, scorer, cts.Token));
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private static async Task ProbeOneAsync(NodeState node, ScoreCalculator scorer, CancellationToken ct) {
        long t0 = Stopwatch.GetTimestamp();
        try {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(node.Host, node.Port, ct).ConfigureAwait(false);
            double score = scorer.Compute(ElapsedMs(t0), 0.0);
            node.UpdateScore(ElapsedMs(t0), 0.0, score, 0);
        }
        catch (OperationCanceledException) {
            node.UpdateScore(5000, 1.0, 1.0, 0); // 超时：分数压到底，不进 cooldown
        }
        catch {
            node.UpdateScore(5000, 1.0, 1.0, 0);
        }
    }
}
```

**关键行为**：Bootstrap 的 ALL code paths 都调用 `node.UpdateScore()` — 无路径保留旧分数。**Bootstrap 始终覆盖历史分数**（包括历史高分）。

### 8.2 ScoreCalculator — 评分计算器

```csharp
public sealed class ScoreCalculator {
    private const double LatencyRef = 2000.0;
    private const double LatencyWeight = 0.55;
    private const double LossWeight = 0.45;
    private const double ScoreFloor = 1.0;
    private const double Exponent = 2.0; // 平方放大差距

    public double Compute(double ewmaLatencyMs, double ewmaLossRate) {
        double latNorm  = Math.Min(ewmaLatencyMs / LatencyRef, 1.0);
        double lossNorm = Math.Clamp(ewmaLossRate, 0.0, 1.0);
        double raw = 1.0 - (latNorm * LatencyWeight + lossNorm * LossWeight);
        raw = Math.Max(raw, 0.0);
        double score = Math.Pow(raw, Exponent) * 100.0;
        return Math.Max(score, ScoreFloor);
    }
}
```

### 8.3 ProbeService — TTFB 探测服务 / 探活系统

**职责**：通过 xray SOCKS5 入站发 HTTP HEAD 测量 TTFB，作为延迟代理指标。这是在不 fork xray 前提下唯一可行的延迟观测手段。

**实现** (`ProbeService.cs`)：

- **多目标探活（P2.6）**：支持配置多个 ProbeUrl（按换行分割），全部成功时取平均 TTFB，所有目标失败才记录 failure
- **并发上限（P2.4）**：`SemaphoreSlim` gate，`max(3, ceil(N/5))`
- **Cooldown 恢复探测**：到期前一个 interval window 内触发 TTFB 探活，成功→立即清除 cooldown，失败→延长 cooldown
- **HttpClient 复用**：每个 outbound tag 复用一个长生命周期 `SocketsHttpHandler`，避免 socket exhaustion

**探活策略分层**：

```
触发条件：
  1. Bootstrap 阶段   — 并行探活所有节点（TCP connect，3s 全局超时）
  2. Cooldown 恢复     — 到期前触发 TTFB 探活（3s 超时），成功→立即清除
  3. 周期性补充        — 所有节点按配置间隔探测（默认 30s）
  4. 低分节点          — 无特殊处理，统一周期探测
```

#### 探活系统的正确认知

当前 probe 不是**真实媒体体验测量**，而是**节点健康检测**。这是一个重要的认知修正。

**用于**：

- timeout 检测
- packet loss
- RTT
- TLS failure
- dead node detection

**不用于**：

- 视频吞吐评估
- 4K 稳定性判断
- sustained media quality

HEAD probe 测的是 small-object latency，与 YouTube chunk streaming 的 sustained congestion quality 相关性很弱。这是已知的系统性约束，必须在设计中承认。

#### 探活偏差（Probe Bias — v7.0 扩展）

TTFB via HTTP HEAD 是业务延迟的"代理指标"，不是真实业务 RTT。探活结果与长流 chunk、HTTP/2 多路复用、WebSocket 等真实流量的延迟存在系统偏差。在 xray 不暴露 socket 级指标的架构下，TTFB 是唯一可行的延迟观测手段。

**机场环境特殊优化加剧偏差（v7.0 新增）**：机场运营方可能对常用 probe 域名做特殊处理——对 `www.gstatic.com/generate_204` 做 CDN 缓存或 Anycast 加速、对小包/空包连接有专用优化通道（small-packet acceleration）、对 probe 域名做 prioritize routing。结果是 **probe 延迟可能系统性低估真实业务 RTT 30%-60%，HealthScore 系统性虚高**。缓解方向（Phase 2 评估）：混合多个 probe 目标（不只 generate_204）、加入随机大小 payload 的 probe 模拟真实流量、允许用户自定义 probe URL。但需注意这些措施会增加 probe 开销。

**Probe 的正确使用边界（v7.0 新增）**：Probe 只用于 failure detection（节点是否彻底不可达）和 reachability estimation（节点是否存活），**禁止用于** QoS optimization、media quality inference、throughput prediction、"best node" selection。

### 8.4 FailureCollector — 失败事件收集器（差异化惩罚）

**实现** (`FailureCollector.cs`)：

```csharp
public sealed class FailureCollector {
    public void RecordSuccess(NodeState node, double ttfbMs) {
        double alpha = DecayedAlpha(node.LastObserved);
        double newLatency = Ewma(node.EwmaLatencyMs, ttfbMs, alpha);
        double newLoss    = Ewma(node.EwmaLossRate, 0.0, alpha);
        double newScore   = _scorer.Compute(newLatency, newLoss);
        node.UpdateScore(newLatency, newLoss, newScore, consecutiveFailures: 0);
        // P2.5: emit probe_result + ewma_update JSONL events
    }

    public void RecordFailure(NodeState node, FailureType type, IReadOnlyList<NodeState> allNodes) {
        if (type == FailureType.TlsError) {
            // TLS config error — no penalty, no cooldown, emit probe_result only
            return;
        }

        (double loss, double lat) = GetPenalty(type, node);
        double alpha = DecayedAlpha(node.LastObserved);
        double newLatency = Ewma(node.EwmaLatencyMs, lat, alpha);
        double newLoss    = Ewma(node.EwmaLossRate, loss, alpha);
        double newScore   = _scorer.Compute(newLatency, newLoss);
        int newFails = node.ConsecutiveFailures + 1;

        node.UpdateScore(newLatency, newLoss, newScore, newFails);
        _cooldown.TryEnterCooldown(node, allNodes);
        // P2.5: emit probe_result + ewma_update JSONL events
    }

    public static (double penaltyLoss, double penaltyLatencyMs) GetPenalty(FailureType type, NodeState node) =>
        type switch {
            FailureType.Refused       => (1.0, 10_000),
            FailureType.Timeout       => (0.8, 10_000),
            FailureType.NetworkError  => (0.7, 10_000),
            FailureType.UnexpectedEof => (0.4, node.EwmaLatencyMs * 1.5),
            FailureType.TlsError      => (0.0, node.EwmaLatencyMs),
            _                         => (0.5, 10_000),
        };
}
```

**差异化惩罚原理**：

| FailureType | penaltyLoss | penaltyLatencyMs | 原理 |
|-------------|-------------|------------------|------|
| Refused | 1.0 | 10,000 | 端口不通，几乎肯定是节点问题 |
| Timeout | 0.8 | 10,000 | 可能 GFW 干扰，也可能是节点慢 |
| NetworkError | 0.7 | 10,000 | 通用网络错误 |
| UnexpectedEof | 0.4 | EwmaLatencyMs × 1.5 | 连接中途断开，弱惩罚 |
| TlsError | 0.0 | EwmaLatencyMs（不变） | TLS 配置错误，与网络质量无关 |

### 8.5 CooldownFsm — 冷却状态机

**实现** (`CooldownFsm.cs`)：

```csharp
public sealed class CooldownFsm {
    private const double MaxEjectionFraction = 1.0 / 3.0;
    private const double BaseSeconds  = 30.0;
    private const double MaxSeconds   = 300.0; // 封顶 5 分钟
    private const int JitterRangeSeconds = 15; // FNV-1a hash offset (P1)

    public void TryEnterCooldown(NodeState node, IReadOnlyList<NodeState> allNodes) {
        if (node.ConsecutiveFailures < 2) return;  // 单次失败不触发

        int cooldownCount = allNodes.Count(n => n.IsInCooldown);
        int maxAllowed = ComputeMaxCooldown(allNodes.Count);
        if (cooldownCount >= maxAllowed) return;  // 超出上限，降权不封禁

        int n = Math.Max(0, node.ConsecutiveFailures - 2);
        double baseSec = BaseSeconds * Math.Pow(2, n);
        int hashOffset = ComputeStableOffset(node.Tag);
        double totalSec = Math.Min(baseSec + hashOffset, MaxSeconds);
        node.SetCooldown(DateTime.UtcNow.AddSeconds(totalSec));
    }

    public static int ComputeMaxCooldown(int nodeCount) =>
        nodeCount <= 1 ? 0 :
        nodeCount == 2 ? 1 :
        Math.Max(1, (int)(nodeCount * MaxEjectionFraction));
}
```

#### Hash-based Cooldown Jitter（P1 稳定性增强）

**问题**：原设计使用 `Random.Shared.NextDouble()` 生成 jitter，群体节点可能同步恢复（recovery burst），导致多个节点同时退出 cooldown 进入 active set，触发不必要的 reload。

**方案**：FNV-1a hash-based stable jitter。

```
offset = fnv1a(tag) % 15s
```

```csharp
private static int ComputeStableOffset(string tag)
{
    uint hash = 2166136261;               // FNV-1a offset basis
    foreach (char c in tag)
    {
        hash ^= c;
        hash *= 16777619;                 // FNV-1a prime
    }
    return (int)(hash % JitterRangeSeconds);
}
```

**为什么不用 `string.GetHashCode()`**：.NET 不保证跨进程 hash 稳定性（较新 runtime 启用随机化 hash），重启后 jitter 会变。FNV-1a 是确定性算法，相同 tag 永得相同 offset。每个节点恢复时间永不相同，且跨重启稳定，telemetry 分析不会因重启而错乱。

**Cooldown 边界规则**：

| 节点数 | 最大 cooldown 数 | 公式 |
|--------|-----------------|------|
| 1 | 0 | 永不冷却（冷却唯一节点 = 无可路由） |
| 2 | 1 | 至少保留 1 个可用 |
| 3 | 1 | max(1, floor(3/3)) |
| 6 | 2 | max(1, floor(6/3)) |
| 9 | 3 | max(1, floor(9/3)) |
| 12 | 4 | max(1, floor(12/3)) |

**Cooldown 退避时长**：

| 连续失败次数 | cooldown 时长（含 hash jitter） |
|-------------|------------------------------|
| 2 | 30s + [0, 14]s |
| 3 | 60s + [0, 14]s |
| 4 | 120s + [0, 14]s |
| 5 | 240s + [0, 14]s |
| 6+ | 300s（封顶） |

### 8.6 ActiveSetManager — 活性集管理

**职责**：管理哪些节点应该出现在 xray balancer selector 中。不直接调度连接（xray 负责），但决定调度器的输入列表。

**实现** (`ActiveSetManager.cs`)：

#### Hysteresis 迟滞机制（核心稳定机制）

**问题**：如果 active set 的进出门槛相同（如 score > 50 进入，score < 50 退出），节点分数在阈值附近震荡时会产生频繁的进出 active set，每次变化触发 xray reload，用户感受到的是频繁的连接中断。

**实现**：

```csharp
public const double EntryThreshold = 60.0;  // 新节点需 score ≥ 60 才能进入
public const double ExitThreshold  = 35.0;  // 已在集合中的节点 score < 35 才退出
// 缓冲带: 25 分
```

**两阶段评估**：

1. **Sticky 保护**：已在 `_currentActiveSet` 中的节点，只要 score ≥ ExitThreshold(35) 就保留在 sticky set 中
2. **Entry 门槛**：不在当前 sticky set 中的节点，需 score ≥ EntryThreshold(60) 才能进入 candidates 列表
3. **Top-K 填充**：sticky 节点优先（按 score 降序），剩余空位用 candidates 填充
4. **安全底线**：若 sticky + candidates 均为空（所有节点分数在 [35, 60)），回退到 raw top-K by score（balancer 永不为空）

#### Explorer 隔离（P1 稳定性增强）

Explorer **不应进入 production selector**。原因：

```
exploration traffic ≠ production traffic
```

**变更前**：每轮额外选 1 个 ≥35 分的非 active 节点加入 selector 给予曝光机会，但不获得 sticky 状态。

**变更后**：Explorer 仅允许：

- probe（探活流量 — `ProbeService` 探测所有节点）
- telemetry（观测）
- passive evaluation（被动评估）

只有**稳定超过 EntryThreshold=60** 才允许进入 active-set (production selector)。这彻底消除了 explorer 旋转触发 xray reload 的路径。

**Top-K 公式**：

```
K = max(2, ceil(eligible_count × 2/3))   # 最少 2 个，最多占非 cooldown 节点的 2/3
```

score 的作用域：

- active-set membership
- cooldown decision
- recovery ordering

**不再映射** routing probability（因为 active-set 内 uniform random）。

#### 建议未来增加：Time Window Hysteresis

当前 hysteresis 仅检查瞬时 score。GPT 评审建议增加时间窗口：

```
进入 active-set: score > 60 持续 20s
退出 active-set: score < 35 持续 10s
```

目的：防止边界抖动、防止 reload storm、防止 active-set oscillation。当前未实施，作为未来考虑项。

### 8.7 XrayStatsPoller — xray Stats API 轮询器（P2.1）

**实现** (`XrayStatsPoller.cs`)：

- 通过 `IXrayStatsClient` 接口（支持 Fake 实现用于测试）轮询 `/debug/vars`
- 可配置轮询间隔（默认 5000ms，测试可注入 50ms）
- 暴露 `TriggerPollAsync()` 用于确定性测试（消除时序竞态）
- 计数器重置检测（delta < 0 → 重置基线）
- `ThroughputAnomalyDetected` 事件：仅作组合异常检测辅助信号（见下文）

**Throughput 信号使用约束（v7.0 修订）**：

> **吞吐量不进入主评分系统。吞吐量不是节点质量指标。**
>
> throughput signals suffer from causality inversion:
> `user behavior → throughput`，而不是 `node quality → throughput`
>
> 吞吐量测的是"用户在做什么"，不是"节点有多好"。

**仅允许用于**：
- 组合异常检测（high score + 长期极低 throughput + 持续失败三者同时成立才触发 suspicion）
- telemetry 记录（debugging hint，不作为自动决策输入）
- 调度质量观测（§7.11 `QualityMetricsReporter` 的 `quality_metrics` 事件）

**禁止**：
- 直接进入 HealthScore
- 直接影响 cooldown 决策
- 直接影响 active-set membership

### 8.8 ScoreLogger — Telemetry 日志（P1.2）

**实现** (`ScoreLogger.cs`)：

- JSONL 格式写入 `guiLogs/adaptive.log`（独立于 xray 日志）
- 每行一个 JSON 对象，`snake_case` 命名，含 `time` 和 `type` 字段
- 事件类型：
  - `score_snapshot` — 每 30s 输出所有节点分数快照（含 `in_cooldown` 字段）
  - `probe_result` — 探活成功/失败，含 ttfb_ms 或 failure_type
  - `ewma_update` — EWMA 更新，含 old/new latency/score 和 alpha
  - `active_set_change` — top-K sticky set 变化，含 active_tags/cooldown_tags/scores/**added/removed/change_reasons**
  - `xray_reload` — xray 配置重载触发
  - `quality_metrics` — P3.2 调度质量指标（熵、P95、均值、标准差）
- 构造函数支持显式 `logPath` 参数（测试写入临时文件）

#### Decision Traceability（决策可追溯性 — P1）

Telemetry 最重要目标不是 metrics dashboard，而是 **Decision Traceability**。

系统必须能回答：**为什么 active-set 变化？**

每个 active-set 变更都包含 causal trace：

```json
{
  "event": "active_set_change",
  "active_tags": ["A", "B"],
  "cooldown_tags": ["C"],
  "scores": {"A": 75.2, "B": 68.1, "C": 31.0},
  "added": [],
  "removed": ["C"],
  "change_reasons": {
    "C": "score_below_exit: score=31.0 < 35"
  }
}
```

**实现**：`AdaptiveSchedulerManager.OnActiveSetChangedAsync()` 调用 `BuildChangeReasons()` 构建 per-node reason map，写入 `active_set_change` JSONL 事件。每个 change reason 精确描述原因（score_crossed_entry / score_below_exit / entered_cooldown / cooldown_cleared / score_ranking），包含边界值和当前分数。

#### Telemetry Retention Policy（v7.1 新增）

Telemetry 完整性依赖日志持久化，但不加约束的日志会无限增长。定义以下保留策略：

| 参数 | 值 | 理由 |
|------|-----|------|
| **Max file size** | 50 MB | 正常运行时每 30s 一次 snapshot ≈ 2KB，加上 probe/ewma/reload 事件，日均约 5-10MB。50MB 约保留 5-10 天 |
| **Retention days** | 7 days | 超过 7 天的日志对调试帮助有限（问题通常在数小时内被发现） |
| **Rotation** | 单文件，达到 50MB 后 rename 为 `adaptive.{yyyyMMdd}.log`，新建 `adaptive.log` | 保持当前日志可快速访问，历史日志按日期归档 |
| **Compression** | 归档文件 gzip 压缩（`adaptive.{yyyyMMdd}.log.gz`） | 典型压缩比 ~8:1，50MB → ~6MB |
| **Max total storage** | 200 MB（约 30 个归档文件） | 超出后删除最旧归档 |
| **Startup cleanup** | 每次启动时执行 retention cleanup | 不引入后台 timer，降低复杂度 |

**实现注意**：retention cleanup 在 `ScoreLogger` 构造函数中同步执行，不阻塞 ControlPlaneLoop。如果 cleanup 失败（如磁盘满），记录 warning 但不阻止系统启动 — telemetry 的可用性不高于 proxy 的可用性。

### 8.9 ReloadPolicyApplier — 策略应用器（Phase 1 Fallback）

**实现** (`ReloadPolicyApplier.cs`)：

- **Trailing debounce**：变更到达时若在 reload 预算窗口内，保存最新配置，窗口到期后应用 — 不丢失更新
- 支持 `IAsyncDisposable`，取消 pending debounce timer

#### Reload Budget（P1 稳定性增强）

**问题**：xray reload = 用户连接中断，这是当前最大 UX 风险。但若硬拒绝所有 reload，系统会为了避免 reload 而持续使用坏节点。

**方案**：**Soft throttle（软节流）**，不是 **hard deny（硬拒绝）**。

```
滑动 1 小时窗口内统计 reload 次数：
  ≤ 6 次/小时  → 15s debounce（正常）
  7-10 次/小时 → 60s debounce（扩展）
  > 10 次/小时 → 120s debounce（节流）
```

- 非 critical change → 延后
- critical degradation → 可突破 budget（但仍有递增 debounce）

```csharp
private TimeSpan GetBudgetAdjustedInterval()
{
    PruneReloadTimestamps(); // 清除 1 小时前的记录
    int count = _reloadTimestamps.Count;
    if (count <= NormalReloadLimit)     // ≤6
        return NormalInterval;          // 15s
    if (count <= ExtendedReloadLimit)   // 7-10
        return ExtendedInterval;        // 60s
    return ThrottledInterval;           // >10 → 120s
}
```

**设计理由**：长连接场景（YouTube、Telegram 下载）频繁 reload 比用次优节点更差。debounce 上限 120s 远小于 cooldown 最小退避（30s），意味着即使节流状态下，真坏节点仍能通过 cooldown 机制被驱逐。

### 8.10 RuntimePolicyApplier — 运行时零中断切换（P3.1）

**实现** (`RuntimePolicyApplier.cs`)：

- **双模策略**：`IXrayHandlerClient.IsAvailableAsync()` 返回 false → fallback 到 `IAdaptivePolicyApplier`
- API 可用时：通过 `AddOutboundAsync`/`RemoveOutboundAsync` 增量更新 xray balancer，无需重启
- API 不可用时：自动 fallback 到 `ReloadPolicyApplier`（生成完整配置 + 重启 xray）
- 对调用方透明

**当前状态**：架构已就绪，但 `IXrayHandlerClient` 的 xray gRPC 实现尚未连线到 `AdaptiveSchedulerManager`。当前 `ReloadPolicyApplier` 仍是默认 applier。

#### Runtime Mutation Capability Matrix（v7.0 新增）

> **RuntimePolicyApplier 的真实能力完全依赖于 proxy core 的 runtime IPC 能力。** 以下是在 v2rayN 支持的 proxy core 中的实际可行性：

| Core | Runtime Outbound 变更能力 | 长流安全（无连接中断） | 说明 |
|------|--------------------------|----------------------|------|
| xray | **极弱** | **否** | 无动态 outbound API。`RandomStrategy.PickOutbound` 不可远程控制。任何 outbound 变更都需要 regenerate config + reload = 连接中断 |
| sing-box | 部分支持 | 部分 | 有 REST API 可动态修改部分配置。outbound 增删理论上可通过 API，但 transport 层变更仍可能触发重连 |
| clash / mihomo | REST API 较成熟 | 相对较好 | `/configs` API 支持 PATCH 更新。outbound/selector 变更可通过 API 完成，部分场景可避免连接中断 |

**结论**：
- xray 用户：RuntimePolicyApplier **不可用**。xray 不支持运行时 outbound 变更。当前 ReloadPolicyApplier 是唯一可行方案。Phase 2 目标"Runtime Routing Mutation"在 xray 环境下依赖 xray 上游提供动态 outbound API。
- sing-box / clash 用户：RuntimePolicyApplier 有一定可行性，但必须在具体 core 版本上做 feasibility testing，确认 API capability boundary 后再连线到生产路径。
- 不要给人以"API 可用 → 零中断"的错觉。这个前提条件（API 可用）在 xray 环境下不成立。

### 8.11 SchedulingQualityMetrics — 调度质量指标（P3.2）

**实现** (`SchedulingQualityMetrics.cs` + `QualityMetricsReporter.cs`)：

- 每 5 分钟计算并写入 `quality_metrics` JSONL 事件
- 指标：Shannon 熵（均匀度）、P95 EWMA 延迟、均值、标准差、active/cooldown 节点数
- `QualitySnapshot` 是 `readonly record struct`（值类型，零分配）
- 纯观测指标，不作为验收标准

### 8.12 AdaptiveSchedulerManager — 控制面编排器

**实现** (`AdaptiveSchedulerManager.cs`)：

- 静态 `Lazy<T>` 单例（v2rayN 代码库不使用 DI，单例是项目中最广泛使用的模式）
- 完整生命周期：`InitializeNodes` → `BootstrapAsync` → `StartProbesAsync` → `StopAsync`
- XmlDoc 覆盖 lifecycle / singleton / profile switching / emergency bypass
- `MonitorActiveSetAsync` 每 5s 检查 top-K 变化，变化时通过 `ReloadPolicyApplier` 应用（含自适应 debounce）
- 紧急旁路：`EmergencyDisableAdaptiveAsync()` 设置 Enabled=false + StopAsync（配置恢复由调用方负责）

**生命周期阶段**：

```
Phase 1 — Init: InitializeNodes — sync, builds node states + allocates probe ports
Phase 2a — Bootstrap: BootstrapAsync — async, restores persisted scores + TCP-connect probes
Phase 2b — Runtime: StartProbesAsync — async, starts ProbeService + ScoreLogger + monitor loop
Shutdown: StopAsync — cancels monitor, disposes services, clears state
```

### 8.13 模块创建顺序（P2.5 连线）

`AdaptiveSchedulerManager.StartProbesAsync()` 中：

```
1. 创建 _scoreLogger（JSONL telemetry）
2. 重建 _collector（含 _scoreLogger 引用，确保 probe_result/ewma_update 从第一轮开始记录）
3. 创建 _probeService（含更新后的 _collector）
4. Prime() active set change tracker
5. 启动 MonitorActiveSetAsync 循环
```

---

## 9. 评分公式与数学基础

### 8.1 Time-Decayed EWMA

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

时间常数 60s 的依据：经验上代理节点质量的短期波动（GFW 干扰）周期约在数十秒量级，60s 时间常数能平滑噪声同时不失去响应灵敏度。

### 8.2 评分公式

```csharp
latNorm  = min(ewmaLatencyMs / 2000, 1.0)     // 归一化延迟 [0,1]
lossNorm = clamp(ewmaLossRate, 0.0, 1.0)       // 归一化丢包 [0,1]
raw      = 1.0 - (latNorm × 0.55 + lossNorm × 0.45)  // 加权组合
raw      = max(raw, 0.0)                        // 保底 0
score    = raw² × 100                           // 平方放大差距
score    = max(score, 1.0)                      // 下界 1.0
```

**为什么用平方放大**：线性映射下，延迟 100ms 与 500ms 节点的权重比约 1.27:1，调度器几乎感知不到差异。平方后变为 1.6:1，差距显著。

### 8.3 分数与调度概率对照

以 3 节点场景为例（不含 cooldown 节点，active-set uniform random）：

| 延迟 | 失败率 | Score |
|------|--------|-------|
| 80ms | 1% | 91.8 |
| 200ms | 3% | 76.6 |
| 500ms | 10% | 46.0 |
| 1200ms | 30% | 21.6 |
| 3000ms | 80% | 1.0（保底） |

> **注意**：score 决定 active-set membership（通过 hysteresis 门槛），不控制 per-node 流量权重。Active-set 内所有节点流量均匀分配。

### 8.4 未来方向：HealthTier 替代伪精度分数（v7.1 新增）

当前 score 使用 0-100 连续值并显示为 91.8 / 76.6 / 46.0 等精度数字，但这是**虚假精度**。当前的 observability 水平（HEAD probe TTFB + EWMA + 复合路径噪声）根本不足以支持小数点级别的区分。

**建议未来考虑使用 HealthTier（离散健康等级）替代连续分数**：

| HealthTier | 含义 | 触发条件 | 行为 |
|------------|------|---------|------|
| GOOD | 节点健康，无明显问题 | score ≥ 60 | 正常参与 active-set |
| DEGRADED | 节点有降级信号，但在可接受范围 | 35 ≤ score < 60 | sticky 保护（已在 active-set 中保持），新节点不进入 |
| UNSTABLE | 节点不稳定，排入探活观察 | score < 35 且非 cooldown | 仅接收 probe traffic，不入 production selector |
| FAILED | 节点确认失败 | cooldown 状态 | 完全排除，等待 cooldown 到期恢复 |

**优势**：
- **诚实性**：不再假装能测量出 91.8 vs 91.2 的差异
- **可解释性**：用户看到 "GOOD" / "DEGRADED" 比看到 76.6 更容易理解
- **减少 micro-optimization**：离散等级消除对 0.1 分变化的过度反应
- **与 hysteresis 天然对齐**：Entry=60 / Exit=35 已经是二值门槛，HealthTier 只是显式化了这个事实

此项为未来方向，当前版本仍使用连续分数以保证与现有代码兼容。

---

## 10. 节点状态机与生命周期

### 10.1 完整状态机（v7.0 修订：Recovery Confirmation FSM）

> **v7.0 重要升级**：旧状态机的 `COOLDOWN → RECOVERING → ACTIVE` 过于简单。三方评审一致指出缺少 Recovery Confirmation 阶段。v7.0 新增完整的四阶段恢复状态机。

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
                     [ACTIVE]  ◄────────────────────────────────────┐
                          │                                          │
                          │ consecutiveFailures >= 2                 │
                          │ AND cooldown节点数 < 总数/3               │
                          ▼                                          │
                    [FAILED]                                         │
                   (cooldown)                                        │
                          │                                          │
                          │ cooldown 到期                             │
                          ▼                                          │
                [RECOVERY_PROBING]                                   │
                          │                                          │
                   ┌──────┴──────┐                                   │
            连续3次probe成功   任意一次probe失败                        │
                  │              │                                   │
                  ▼              ▼                                   │
     [STABILITY_VERIFICATION]  重回 [FAILED]                           │
          (不入production     延长cooldown                            │
           selector,          指数退避                                │
           仅接收probe        上限30min)                               │
           traffic)                                                  │
                  │                                                   │
                  │ 正常probe持续稳定 N分钟                             │
                  │ (建议N=5，可配置)                                   │
                  ▼                                                   │
             [ACTIVE] ──────────────────────────────────────────────┘
```

**关键设计细节（v7.0 / v7.1 修订）**：

1. **Recovery Probing 阶段**：cooldown 到期后不直接进入 ACTIVE，而是进入 RECOVERY_PROBING。连续 3 次 probe 成功 → 进入 STABILITY_VERIFICATION。任意一次 probe 失败 → 重回 FAILED，延长 cooldown（指数退避，上限 30 分钟，防止永久冷冻）。

2. **Stability Verification 阶段（v7.1 改名，原 WARMUP_CANDIDATE）**：节点已证明基本可达，但尚未证明长期稳定。**这不是 traffic warmup——当前 xray 架构不存在低权重真实流量预热**，active-set 内是 uniform random。如果此阶段的节点进入 selector，它将与所有健康节点等概率承接流量，可能导致刚恢复的节点被压垮。**STABILITY_VERIFICATION 节点仅接收 probe traffic**，这是 stability gate 而非 warmup stage。它在确认稳定（N 分钟持续正常，建议 5 分钟可配置）后才进入 production selector。

3. **指数退避上限**：cooldown 延长使用指数退避但 cap 在 30 分钟。避免节点在长期网络波动后被永久冷冻（无限延长 cooldown = 手动干预才能恢复 = 违反"用户极少手动干预"目标）。

4. **状态持久化**：Recovery Confirmation FSM 的状态（FAILED / RECOVERY_PROBING / WARMUP / ACTIVE）必须持久化到 `ProfileExItem`。进程重启后状态不能丢失——否则重启后 WARMUP 节点直接变成 ACTIVE，跳过验证阶段。

**全局约束**：COOLDOWN（FAILED）状态节点数 ≤ `ComputeMaxCooldown(N)`。超出上限时，新的失败节点改为降权处理（不进 cooldown）。

### 10.2 Active Set Hysteresis（迟滞机制）

详见 §7.6。这是 OpenAI 评审中最有价值的增量贡献，Claude v1.0 审计遗漏了这一点。

### 10.3 启动序列（含实际时间线）

```
T=0ms      加载节点配置
           → 有历史分数: 恢复到 NodeState
           → 无历史分数: 所有节点 Score=50（初始值）
           → 历史分数超过 4h 强制回退到 50
T=0ms      BootstrapProber 并行 TCP connect 探活所有节点（2s 超时，全局 3s 超时）
           → Bootstrap 结果始终覆盖历史分数
T≤3000ms   Bootstrap 完成
T=3001ms   InitializeNodes 返回初始 AdaptiveConfig（含 probe inbounds）
T=3001ms+  首次 LoadCore：xray 配置加载（含探活入站）
T≈4~5s     xray SOCKS5 就绪（重启实测 ~1.1s）
T=3001ms+  StartProbesAsync：ProbeService + ScoreLogger + MonitorActiveSet 启动
T+15~30s   第一批被动观测 EWMA 数据逐步替代 Bootstrap 初始值
```

**调度响应延迟上界**：

```
节点质量恶化
  → EWMA 反映：T₁ (5~10s)
  → active set 更新（MonitorActiveSet 检查间隔）：5s
  → debounce 等待（ReloadPolicyApplier 自适应）：15~120s（取决于 reload budget）
  → xray 重启（实测）：~1.1s
总计上限：~22~27s（正常）/ ~127s（节流状态下）
```

### 10.4 节点规模上限建议

| 节点数 | 建议 |
|--------|------|
| < 3 | active-set uniform random 无意义，保留 adaptive health tracking |
| 3~20 | 核心场景，完整功能 |
| 20~50 | 探活并发需要上限控制，`max(3, ceil(N/5))` |
| > 50 | 给出警告，建议按地区/用途拆分为多个 group |

### 10.5 TCP vs UDP/QUIC 隔离

```csharp
var candidates = _nodes
    .Where(n => n.Protocol == requestedProtocol && !n.IsInCooldown)
    .ToList();
```

当前仅实现 TCP 池。UDP/QUIC 池在 P3.3 跟进。

### 10.6 全部节点 Cooldown 兜底策略（§8.1 Criterion #1）

当所有节点都进入 cooldown 时，`ActiveSetManager.GetActiveTags()` 选择 cooldown 剩余时间最短的节点作为降级服务。这确保 balancer selector 永远不会为空 — xray 必须至少有一个可路由的 outbound。

### 10.7 State Transition Invariants（状态迁移不变量，v7.1 新增）

每个 FSM 状态定义**合法迁移（legal transitions）**和**非法迁移（illegal transitions）**。非法迁移在代码中必须通过 `Debug.Assert` 或显式 guard clause 阻止。

#### 节点健康状态机 Illegal Transitions

```
禁止: FAILED → ACTIVE (跳过 RECOVERY_PROBING + STABILITY_VERIFICATION)
禁止: RECOVERY_PROBING → ACTIVE (跳过 STABILITY_VERIFICATION)
禁止: STABILITY_VERIFICATION → FAILED (仅 probe 失败, 必须走 RECOVERY_PROBING)
禁止: ACTIVE → RECOVERY_PROBING (cooldown 到期后才是 RECOVERY_PROBING)
禁止: 任何状态 → ACTIVE (除非从 STABILITY_VERIFICATION 且 verification_timer 到期)
```

**合法迁移**：
```
ACTIVE → FAILED              : consecutiveFailures >= 2 AND cooldown 未达上限
FAILED → RECOVERY_PROBING    : cooldown 到期
RECOVERY_PROBING → STABILITY_VERIFICATION : 连续 3 次 probe 成功
RECOVERY_PROBING → FAILED    : 任意一次 probe 失败 (指数退避延长 cooldown)
STABILITY_VERIFICATION → ACTIVE : 持续 N 分钟 probe 正常 (默认 5min, 可配置)
STABILITY_VERIFICATION → FAILED : probe 失败 (退回 FAILED, 重置验证进度)
```

#### Global Freeze 状态机 Illegal Transitions

```
禁止: FREEZE_ACTIVE → FREEZE_ACTIVE (重复进入, freeze 期间不重新触发)
禁止: 任何操作在 FREEZE_ACTIVE 期间修改 active-set
禁止: FREEZE_ACTIVE 期间调度 reload
禁止: freeze_cooldown 期间触发新 freeze (除非 escalate to EmergencyDisable)
```

**合法迁移**：
```
NORMAL → FREEZE_ACTIVE       : >60% active 节点同时 FAILED
FREEZE_ACTIVE → NORMAL       : 60s freeze 到期, 正常解除
NORMAL → FREEZE_COOLDOWN     : freeze 刚解除, 进入 120s cooldown 观察期
FREEZE_COOLDOWN → NORMAL     : 120s 到期, 恢复 freeze 触发能力
FREEZE_COOLDOWN → EMERGENCY_DISABLE : cooldown 期间再次大规模异常, 升级
```

#### Manual Override 状态机 Illegal Transitions

```
禁止: ManualOverride 期间任何自动 reload
禁止: ManualOverride 期间任何 active-set mutation
禁止: 自动系统在 ManualOverride 期间修改 xray 配置
禁止: ManualOverride 解除后不经过 Prime() 就恢复自动控制
```

**实现原则**：每个状态迁移函数必须在入口处检查 `IsLegalTransition(from, to)`，非法迁移记录为 `illegal_transition_attempt` JSONL 事件（用于发现代码 bug），然后在 debug build 中 assert，release build 中 silently reject 并保持当前状态不变。

---

## 11. 全局不稳定性冻结机制（Global Instability Freeze）

> **v7.0 新增**：这是 production 环境必需的防护机制。三方评审一致认为缺失此机制是当前系统最大的运行风险。

### 11.1 问题描述

在 GFW 波动、ISP 抖动、DNS 污染、大规模短时 timeout 等外部冲击下，如果不冻结控制面，会发生**自激震荡（self-oscillation）**：

```
外部冲击 → 大量节点同时超时 → 全部进入 cooldown
  → active-set 急剧缩小 → 剩余节点被流量压垮
  → 更多节点超时 → 恶性循环 → 用户完全断网
```

Hysteresis 和 Reload Budget 能缓和正常波动，但在大规模外部冲击面前不够。

### 11.2 触发条件

```
> 60% active-set 内节点在 T 秒窗口内同时发生连续失败
（T 建议值 = 15s，可配置）
```

### 11.3 冻结行为

触发后自动执行以下冻结（持续 60 秒，可配置）：

```
1. Freeze active-set mutation    — 不再驱逐节点，不再添加节点
2. Freeze cooldown ejection      — 不触发新的 cooldown
3. Suspend reload scheduling     — 暂停所有 xray config reload
4. Keep last known stable selector — 维持冻结前最后一组 active-set
```

**冻结期间**：probe 继续运行（收集数据），ScoreLogger 继续记录（保留现场），telemetry 标记 `global_freeze` 事件。

### 11.4 解除条件

```
冻结持续时间到达（默认 60s）后自动解除。
```

解除后：
1. 以解除时的最新分数重新计算 active-set（不回溯冻结前状态）
2. 恢复正常 cooldown 评估（冻结期间积累的 failure count 不清零，但只计最近 2 次）
3. Reload Budget 从头计数（冻结期间的 reload 不计入预算）

解除时发出 `global_freeze_end` JSONL 事件，包含 freeze duration 和 freeze 前后 active-set 对比。

### 11.5 设计理由

冻结机制不追求"判断这次波动是不是真的"——它只做一件事：**给系统足够的静默时间来度过外部冲击**。60 秒在 GFW 波动场景下通常足够，但不长到影响真正需要切换的场景。

### 11.6 配置参数

| 参数 | 建议默认值 | 范围 |
|------|-----------|------|
| FreezeTriggerRatio | 0.60 (60%) | 0.30 – 0.80 |
| FreezeTriggerWindowSeconds | 15s | 5s – 30s |
| FreezeDurationSeconds | 60s | 30s – 120s |
| FreezeCooldownSeconds | 120s | 60s – 300s |

### 11.7 Freeze Hysteresis（v7.1 新增）

**问题**：若无防震荡机制，会出现 freeze → 恢复 → 5 秒后再次 freeze → 恢复 → 再 freeze 的 freeze oscillation。

**方案**：**Freeze Cooldown**——freeze 结束后 120 秒内禁止再次触发 freeze。

```
freeze 解除 → 进入 freeze_cooldown 状态（120s）
  → cooldown 期间: 正常监控，正常 probe，但不触发 freeze
  → cooldown 期间: 如果再次出现大规模异常 → escalate to EmergencyDisable（而非再次 freeze）
  → cooldown 到期: 恢复正常 freeze 触发能力
```

**设计理由**：如果 120s 内再次发生 60% 节点同时异常，说明问题不是短时外部波动，而是更严重的系统性故障。此时 escalate 到 EmergencyDisableAdaptive 比反复 freeze/unfreeze 更安全——反复震荡意味着控制面本身已成为不稳定源。

### 11.8 Control-Plane Mutation Authority 边界（v7.3 新增）

> **这是当前系统最重要的架构风险标注。**

#### 问题

当前 control plane 存在 **两个独立的 mutation authority** 同时作用于 `NodeState` 的生命周期：

| Authority | 所在层 | 触发路径 | 修改什么 |
|-----------|--------|---------|---------|
| `CooldownFsm.TryEnterCooldown` | Probe 层（数据采集） | `FailureCollector.RecordFailure` → `CooldownFsm` | `NodeState._cooldownUntil`、`_consecutiveFailures` |
| `GlobalFreezeController.Evaluate` | Monitor 层（调度决策） | `MonitorActiveSetAsync` → `Evaluate` | 返回 `BlockMutation`，阻止 active-set 变更 |

**这两个 authority 不在同一层级且不互知**：freeze 声称"冻结控制面"，但 cooldown 在 probe 层照常推进。Freeze 解除时，CooldownFsm 已经在后台积累了 60 秒的 failure count，可能触发 latent cooldown explosion — 这正是 freeze 设计要防止的"自激震荡"场景。

#### 当前实际的 Mutation Authority Map

```
NodeState 生命周期变更路径（v7.3 实际）:

  ProbeService → FailureCollector.RecordFailure
    ├── 更新 EWMA (always)           ← 数据采集，freeze 期间允许
    ├── 推进 CooldownFsm.TryEnter    ← ⚠️ 状态迁移，freeze 期间也在推进
    └── 通知 GlobalFreezeController  ← 数据采集，freeze 期间允许

  MonitorActiveSetAsync
    ├── GlobalFreezeController.Evaluate → BlockMutation?  ← 调度决策
    ├── RecoveryConfirmationFsm 状态推进                    ← 调度决策
    └── ActiveSetManager 更新                               ← 调度决策

  ManualOverride
    └── 禁止以上所有路径的 reload
```

**问题核心**：Freeze 阻止的是 Monitor 层的 decision consumption，但没有阻止 Probe 层的 state production。这产生 **split-brain control-plane**：Probe 层持续积累 cooldown state，Freeze 层声称冻结了，但冻结解除后立即面对一个已经爆炸的 cooldown backlog。

#### 当前缓解措施（v7.3）

1. Freeze 解除时不清除 failure count（`_consecutiveFailures` 保持），但 freeze 期间积累的 cooldown 在 Monitor 恢复后的第一个 cycle 被 `BlockMutation` 阻止消费
2. Freeze hysteresis（120s cooldown）防止 freeze oscillation
3. EmergencyDisable escalation 作为最后的熔断路径

#### P2 计划

```
短期（P2.1）：✅ 已完成（v7.3 freeze gate）
  → FailureCollector.RecordFailure 检查 _freezeController.IsFrozen
  → freeze 期间：更新 EWMA（观测继续），不递增 consecutiveFailures，不调用 CooldownFsm
  → telemetry 标记 freeze_gate: true
  → 实现文件：FailureCollector.cs (RecordFailure freeze gate)
  → 测试文件：FreezeGateTests.cs (7 tests)

中期（P2.2）：统一 Control-Plane Mutation Authority
  → 形式化规则：谁有权改变 NodeState lifecycle
  → 所有 state transition 通过单一 gate 检查
  → Freeze → ManualOverride → RecoveryFSM → CooldownFSM 优先级链
```

---

## 12. 用户手动操作优先级（Manual Override Lock）

> **v7.0 新增 / v7.1 修订**：Manual Override 的本质不是"暂停功能"，而是**control-plane authority transfer**——自动系统暂时 relinquish routing authority，用户显式接管 routing policy。

### 12.1 问题描述

用户手动切节点后，自动调度可能在几秒内又把节点切走，导致用户感觉"系统在和我抢控制权"。这不是功能缺陷，而是**控制面权限模型不清晰**——自动系统和用户之间没有明确的 authority boundary。

### 12.2 权限转移规则（v7.1 修订）

```
用户手动切换到指定节点
  → Control-plane authority transfer:
       自动系统 relinquish routing authority
       用户显式接管 routing policy
  → 持续时间：N 分钟（默认 5 分钟，可配置）
  → 锁定期间：
       - 禁止 active-set 变更触发 reload
       - 禁止自动切换节点
       - 保留所有 probe + telemetry（数据收集继续，但不决策）
  → UI 明确标注："Routing: Manual（自动调度已让权）"
  → 锁定到期: authority 自动归还 adaptive 系统
```

**语义升级**：这不是"暂停功能"，而是**用户显式行使 routing authority**。自动系统在此期间仍然运行（probe、telemetry、状态追踪），但不产生 routing 决策——类似 aircraft autopilot 的"manual override"概念。

### 12.3 手动解锁

用户可以通过以下方式提前解除锁定：

- 点击 UI 上的"恢复自动调度"按钮
- 切换 profile / 重新加载配置（锁定自动失效）

### 12.4 设计理由

用户手动操作意味着"我当前有明确的节点偏好"，这个偏好必须被尊重。5 分钟锁定既尊重了用户意图，又避免了永久退出 adaptive 模式（用户可能忘记恢复）。

### 12.5 与紧急旁路的关系

| 机制 | 触发方式 | 行为 | 恢复方式 |
|------|---------|------|---------|
| Manual Override Lock | 用户手动切节点 | 暂停调度 N 分钟 | 自动恢复 |
| EmergencyDisableAdaptive | 用户主动执行 | 完全停止 adaptive | 手动重新启用 |

两者可共存。EmergencyDisable 优先级更高（锁定期间执行 EmergencyDisable 仍然有效）。

---

## 13. DNS 故障域隔离

> **v7.0 新增**：GFW 环境下 DNS 是独立故障域。DNS 失败 ≠ 节点失败。混为一谈会导致 cooldown 大量误杀。

### 13.1 问题描述

在 GFW 环境下，DNS 是独立的故障域：

```
场景 A: 节点 IP 本身可达，但 DNS 解析被污染
  → DNS 解析返回错误 IP → 连接失败 → 被误判为节点故障

场景 B: DoH/DoT 走代理，代理节点故障导致 DNS 也失败
  → DNS 解析超时 → 无法区分是 DNS 问题还是节点问题

场景 C: DNS 缓存过期后新解析失败
  → 本来可达的节点因为 DNS 问题不可达 → 被误罚
```

当前 FailureType 枚举只有 Timeout / Refused / NetworkError / TlsError，在 GFW 环境下**太粗糙**。DNS 故障会直接反映为 Timeout 或 NetworkError，与真正的节点故障无法区分。

### 13.2 归因分离规则

```
DNS 解析失败
  → Step 1: 用上次成功的缓存 IP 重试连接
  → Step 2: 用备用 DNS resolver 重新解析（如果配置了）
  → Step 3: 只有缓存 IP 也连接失败时，才计入节点故障
  → Step 4: 如果缓存 IP 连接成功，标记为 DnsResolutionFailure（不计入节点惩罚）
```

### 13.3 建议新增 FailureType（Phase 2 实施）

```csharp
DnsResolutionFailure,    // DNS 解析失败，但节点 IP 层面可能可达（不计入 cooldown）
DnsPoisoningSuspected,   // DNS 返回了明显错误的 IP（如 GFW 投毒地址）
CachedIpRecovered,       // 通过缓存 IP 成功连接（不计入故障）
```

**当前状态**：FailureType 枚举尚未扩展。Phase 1 阶段 DNS 故障与节点故障共用一个笼统的 Timeout 类型承认这个粗糙性。Phase 2 应在 Resolve 层做 DNS 缓存和重试后，才启用精确归因。

> **v7.3 实现限制（重要）**：当前 DNS 故障域隔离实现仅完成了 **Step 1（缓存 IP 重试）** 和 **Step 4（DNS 故障不计入节点惩罚）**。**Step 2（备用 DNS resolver）尚未实现**——当前 `DnsCacheManager` 仅使用 `Dns.GetHostAddressesAsync()`（系统 DNS），无独立 resolver 路径（DoH/DoT 或配置化备用 DNS）。这意味着当系统 DNS 本身被污染时，缓存失效后的重新解析仍然使用可能被污染的同一 DNS 源，系统没有真正的独立 recovery path。**当前实现准确描述为：DNS cache retry + confidence lifecycle，而非完整的 dual-resolver fault-domain isolation。** 完整的备用 resolver 路径计划在 P2 实施。

### 13.4 DNS 缓存策略与 Lifecycle（v7.1 扩展）

```
每个节点维护最近一次成功 DNS 解析结果（IP + 解析时间）
TTL: 300s（5 分钟，覆盖典型的 GFW DNS 污染周期）
连接失败时优先使用缓存 IP 重试
```

#### DNS Cache Confidence Lifecycle（v7.1 新增）

仅存一个缓存 IP 是不够的——必须定义缓存**可信度生命周期**，否则长期 stale IP 自身会变成故障源：

```
[DNS_CACHE_VALID]
  │
  │ 缓存 IP 连接成功
  │   → confidence += 1，refresh last_success_time
  │
  │ 缓存 IP 连接失败
  │   → 累计连续失败次数
  │   → N 次连续失败（建议 N=3）
  │       → [DNS_CACHE_INVALIDATED]
  │           → 丢弃缓存 IP
  │           → 触发重新 DNS 解析
  │           → 使用备用 resolver
  │           → 如果所有 resolver 都失败
  │               → 标记 DnsResolutionFailure（不计入节点故障）
  │
  │ TTL 到期（300s）
  │   → 主动触发重新解析（不依赖失败事件）
  │   → 新 IP 与缓存 IP 相同 → confidence 保持
  │   → 新 IP 不同 → 更新缓存，confidence 重置
```

**设计理由**：DNS 缓存不是"一次缓存永不过期"——GFW 可能投毒特定域名，也可能在污染周期后恢复正常。缓存可信度机制允许系统渐进地评估缓存质量，而不是二值地"信任/不信任"。

---

## 14. 设计演进：关键变更、冲突与解决

本章是两份文档合并的核心价值所在。以下详细记录从原始设计到最终实现的所有重大变更，以及每项变更的原因和思考过程。

### 14.1 架构重定位：Weighted Scheduler → Active-Set Scheduler

**原设计**（CLAUDE-loadbalance.md v3.0）：通过 tag duplication 实现加权路由 — score 100 的节点在 xray selector 中出现 4 次，score 25 的出现 1 次，xray random balancer 按重复次数加权选择。

**发现过程**（2026-05-21 P0.1）：集成测试 `XrayTagDuplicationIntegrationTests` 对 xray v26.3.27 进行了双 observer 验证：
- `[A, A, A, B]` selector，N=1000 请求
- 结果：A ≈ 50%（不是预期的 75%）
- xray 源码追踪：`app/router/balancing.go` `SelectOutbounds()` 对 candidate outbound tags 做 prefix-match + **去重**

**结论**：tag duplication weighting **从未生效**。整个系统建立在错误的架构假设上。

**变更内容**：
1. 代码：移除 `V2rayAdaptiveService` 中 tag duplication 逻辑，改为 active-set unique tags
2. 测试：`XrayTagDuplicationIntegrationTests` 保留为 xray selector 行为契约检测器
3. 系统重定位：从 "Weighted Adaptive Scheduler" 收敛为 "Adaptive Active-Set Scheduler"
4. 核心目标调整：从"精确概率分流"改为"动态剔除坏节点"

**为什么这不是失败而是收敛**：系统核心能力（淘汰坏节点、恢复好节点、EWMA 学习、hysteresis 防抖）全部保留且有效。tag duplication 只是加权手段被证伪，系统本质能力没有损失。

### 14.2 Top-K 公式：0.5 → 2/3

**原设计**：`K = max(3, ceil(N × 0.5))`

**实际实现**：`K = max(2, ceil(N × 2/3))`

**变更原因**：原公式对 small-N 场景（N=3, K=3 → 全部节点都在 active set，cooldown 机制无从生效）过于约束。2/3 比例更宽松，使更多节点留在 active set，减少单点故障风险。最小值从 3 降为 2：N=3 时 K=2（而非 3），允许 cooldown 驱逐至少 1 个节点。

### 14.3 Explorer 机制演进：从 production selector 到 probe-only

**原设计**（v4.0）：每轮额外选取 1 个 ≥35 分的非 active 节点加入 production selector 给予曝光机会（一轮），但不获得 sticky 状态。

**P1 变更**：Explorer 不再进入 production selector。只通过 probe traffic（`ProbeService` 探测所有节点）验证质量。必须得分 ≥60 才能进入 sticky set。

**变更原因**：exploration traffic ≠ production traffic。Explorer 加入 production selector 引入了额外的 reload 触发路径和用户流量风险。P1 稳定性目标要求消除一切不必要的 reload 路径。

### 14.4 Cooldown Jitter：Random → FNV-1a Hash

**原设计**：`Random.Shared.NextDouble()` 随机 jitter

**P1 变更**：FNV-1a hash-based stable offset（详见 §7.5）

**变更原因**：随机 jitter 在群体节点场景下可能产生 recovery burst（多个节点同时退出 cooldown）。Hash-based offset 每个节点恢复时间永不相同，且跨重启稳定。

### 14.5 Reload Budget 从静态 15s 到自适应 window

**原设计**：静态 `MinReloadInterval = 15s`

**P1 变更**：滑动 1 小时 window 自适应 debounce（15s/60s/120s），详见 §7.9。

**变更原因**：静态 interval 无法区分"正常变更"和"reload storm"。自适应 budget 在正常时保持响应速度（15s），在异常时自动节流，但不硬拒绝。

### 14.6 Cooldown 边界规则细化（P2.2）

**原设计**：`maxAllowed = (int)(N * 1/3)` — 单节点场景下 max=0（语义正确但有歧义），2 节点场景下 max=0（但实际应允许 1 个 cooldown）。

**实际实现**：`ComputeMaxCooldown` 显式处理边界：
```
N=1 → 0  (永不冷却唯一节点)
N=2 → 1  (允许 1 个 cooldown，保留 1 个可用)
N≥3 → max(1, floor(N/3))
```

### 14.7 其他差异记录

| 差异点 | 原设计 | 实际实现 | 原因 |
|--------|--------|---------|------|
| EmergencyDisable | 内部调用 RestoreDefaultConfigAsync | StopAsync 后由调用方负责 | 调用方可能需要额外清理逻辑 |
| cooldown_enter 事件 | 独立事件类型 | 通过 score_snapshot/active_set_change 字段表达 | 不丢失信息，减少日志量 |
| TtfbProber 名称 | `TtfbProber` | `ProbeService` | 更准确反映多职责（TTFB + cooldown 恢复 + 并发控制 + 多目标） |
| 日志路径 | `{v2rayN}/adaptive.log` | `guiLogs/adaptive.log` | 与项目现有日志路径约定一致 |
| 静态单例 | P1.4 要求移除 | 保留 Lazy<T> 单例 + XmlDoc | v2rayN 不使用 DI，单例是项目标准模式 |

---

## 15. P1 稳定性增强实现

> 本章记录 2026-05-22 实施的 P1 稳定性增强。这些变更来自外部工程评审，聚焦于降低系统 churn、提升长期稳定性。

### 11.1 Explorer 隔离（ActiveSetManager.cs）

**变更前**：Explorer 节点进入 production xray balancer selector（one-round exposure）

**变更后**：Explorer 仅接收 probe traffic，不入 production selector

**实现**：
- `GetActiveTags()` 不再将 explorer 加入 active list
- 新增 safety net：当 hysteresis 产生空 active set 时，回退到 raw top-K by score
- `HasActiveSetChanged()` 比较不含 explorer 的 top-K sticky set

### 11.2 Hash-based Cooldown Jitter（CooldownFsm.cs）

**变更前**：`Random.Shared.NextDouble()` 随机 jitter

**变更后**：FNV-1a `ComputeStableOffset(tag)` → hash % 15s

**实现**：
```csharp
int hashOffset = ComputeStableOffset(node.Tag);
double totalSec = Math.Min(baseSec + hashOffset, MaxSeconds);
```

### 11.3 Reload Budget（ReloadPolicyApplier.cs）

**变更前**：静态 `MinReloadInterval = 15s`

**变更后**：滑动 1 小时 window 自适应 debounce

**实现**：
```csharp
private static readonly TimeSpan BudgetWindow = TimeSpan.FromHours(1);
private const int NormalReloadLimit = 6;
private const int ExtendedReloadLimit = 10;
private static readonly TimeSpan NormalInterval = TimeSpan.FromSeconds(15);
private static readonly TimeSpan ExtendedInterval = TimeSpan.FromSeconds(60);
private static readonly TimeSpan ThrottledInterval = TimeSpan.FromSeconds(120);
private readonly List<DateTime> _reloadTimestamps = [];
```
- `GetBudgetAdjustedInterval()` 返回当前 debounce interval
- `RecordReload()` 记录每次 reload 时间戳
- `PruneReloadTimestamps()` 清除 1 小时前记录

### 11.4 Decision Traceability（ActiveSetManager + AdaptiveSchedulerManager）

**变更前**：`active_set_change` event 不含变更原因

**变更后**：每个 active-set 变更包含 causal trace

**实现**：
- `ActiveSetManager` 新增 `LastAdded` / `LastRemoved` 属性
- `HasActiveSetChanged()` 检测到变更时填充这两个属性
- `AdaptiveSchedulerManager.BuildChangeReasons()` 为每个 added/removed node 生成详细原因（score_crossed_entry / score_below_exit / entered_cooldown / cooldown_cleared / score_ranking）
- `active_set_change` JSONL event 新增 `added`、`removed`、`change_reasons` 字段

### 11.5 Stability Objective 文档化

- `ActiveSetManager.cs` 类 doc 新增 Stability Objective / Explorer isolation / Decision traceability 章节
- §4.4 新增统一稳定性目标函数和 heuristic-稳定性映射表

### 11.6 Control Plane Event Ordering（事件时序一致性，v7.1 新增）

> **这是当前文档最重要的缺口。** 所有模块都是异步事件驱动，但缺少正式的 event ordering contract。probe timeout、cooldown enter、freeze trigger、reload debounce pending、manual override 这些事件同时发生时，谁先执行？谁取消谁？谁覆盖谁？没有 formal rule 是未来 Heisenbug 的最大来源。

#### 事件优先级定义

所有控制面事件按以下优先级执行。**高优先级事件可以 cancel 低优先级 pending action**。

| 优先级 | 事件 | 说明 | Cancel 范围 |
|--------|------|------|------------|
| **P0** | EmergencyDisable | 用户触发紧急旁路，立即停止所有自适应行为 | Cancel 所有 P1-P5 pending actions + 停止 ControlPlaneLoop |
| **P1** | ManualOverride | 用户显式接管路由，自动系统 relinquish authority | Cancel 所有 P3-P5 pending actions（Freeze 仍保持监控但不执行 reload） |
| **P2** | GlobalFreeze | >60% 节点同时 FAILED，冻结控制面 60s | Cancel 所有 P4-P5 pending reload tasks。Probe 继续。Telemetry 继续。 |
| **P3** | CooldownTransition | 节点进入/退出 cooldown | 触发 P4 active-set 重算。不直接 cancel reload（reload 由 P4 统一调度） |
| **P4** | ActiveSetMutation | sticky top-K 变化 | 创建 reload task（受 debounce budget 约束）。可以被 P0/P1/P2 cancel |
| **P5** | Telemetry | score_snapshot / probe_result / ewma_update / quality_metrics | 纯观测，不触发任何 state mutation。不被 cancel（独立于控制路径） |

#### 事件冲突解决规则

**规则 1 — 优先级抢占**：
```
当高优先级事件到达时:
  1. 立即中断当前低优先级 handler（如果可安全中断）
  2. Cancel 低优先级 pending tasks（如 pending reload）
  3. 执行高优先级 handler
  4. 高优先级 handler 完成后，低优先级事件源需要重新触发（不会自动恢复）
```

**规则 2 — 同优先级 FIFO**：
```
同优先级事件按到达顺序处理。不合并、不跳过。
例外: 如果两个 ActiveSetMutation 之间的时间 < debounce window，第二个覆盖第一个（因为第一个还没开始 reload）
```

**规则 3 — P2 Freeze 期间的 P3 CooldownTransition**：
```
Freeze 期间 cooldown 照常评估（FSM 状态迁移不受 freeze 影响），
但 cooldown 变化不触发 P4 ActiveSetMutation（因为 freeze 禁止 active-set 变更）。
Freeze 解除后，用最新的 FSM 状态一次性重算 active-set。
```

**规则 4 — P1 ManualOverride 与 P2 Freeze 的关系**：
```
ManualOverride 优先级高于 Freeze。
如果 ManualOverride 在 Freeze 期间触发:
  → 立即解除 Freeze（记录 global_freeze_overridden_by_manual JSONL event）
  → 执行 ManualOverride
  → Freeze 状态重置为 NORMAL（不是 FREEZE_COOLDOWN，因为是被 override 而非正常到期）
```

**规则 5 — Reload 执行前的 generation check**：
```
ReloadPolicyApplier 执行 reload 前必须:
  1. 检查 ConfigGeneration.Current == capturedGeneration（见 §6.4）
  2. 检查 FreezeController 不在 FREEZE_ACTIVE 状态
  3. 检查 ManualOverride 未激活
  4. 三项全部通过才执行 reload。任一失败 → 丢弃此 reload task。
```

#### 实现原则

- ControlPlaneLoop 内的事件分发按优先级排序（见 §4.6）
- 每个事件 handler 必须是 **short-lived synchronous** 操作（< 1ms），不应包含 I/O
- I/O（probe HTTP request、xray reload）在 ControlPlaneLoop **之外**异步执行
- 事件提交到 ControlPlaneLoop 通过 bounded channel（背压保护，队列深度 256，满时丢弃最旧的 P5 事件）

---

## 16. 实施行动计划与完成记录

### 16.0 当前计划与优先级总表（v7.1，2026-05-22）

> **核心一句话**：当前最大任务不是"更智能"，而是"更克制"——补状态机、堵语义漏洞、明确边界条件，让系统从"想法集合"变成"可长期维护的架构文档"。

#### 优先级定义

| 级别 | 含义 | 时间线 |
|------|------|--------|
| **P0** | 阻塞发布：缺失将导致系统行为不可预测、语义错误或架构偏离 | 立即执行 |
| **P1** | 功能完整性：不阻塞发布但显著影响可靠性或可维护性 | 本周内 |
| **P2** | 架构稳固：不阻塞发布但防止未来退化或复杂度爆炸 | 可推迟，不可遗忘 |

---

#### P0 — 立即执行（阻塞发布）

| # | 任务 | 说明 | 文档对应 |
|---|------|------|---------|
| 1 | **补齐核心状态机** | Recovery Confirmation FSM（含指数退避 + STABILITY_VERIFICATION 阶段）+ Global Instability Freeze（含 freeze hysteresis 防震荡） | §10 状态机 + §11 全局冻结 |
| 2 | **修正致命语义错误** | 吞吐量彻底退出主评分系统，明确标注"因果性错乱，仅作组合异常辅助条件" | §6.6 Throughput 因果性倒置 + §7.7 ThroughputAnomaly |
| 3 | **新增边界声明章节** | Known Limitations 中明确 HealthScore = Reachability Score ≠ UX Score；列出所有无法测量的指标（拥塞 / CDN routing / 用户本地抖动 / QUIC 状态） | §6 已知局限性与可观测性边界 |
| 4 | **文档首页重定位** | 删除"智能负载均衡"表述，改为"Conservative Failure Isolation System"；成功标准从 routing precision 改为"用户极少需要手动干预" | 文档头部定性声明 + §1 系统定位 |

---

#### P1 — 本周内完成

| # | 任务 | 说明 | 文档对应 |
|---|------|------|---------|
| 5 | **DNS 归因分离** | DNS 故障 ≠ 节点故障；增加缓存重试 + alternate resolver 逻辑；新增 FailureType: `DnsResolutionFailure`, `DnsPoisoningSuspected` | §13 DNS 故障域隔离 |
| 6 | **Runtime 可行性调研** | 明确实际使用的客户端（xray / sing-box / clash/mihomo）的热更新能力边界，写入文档作为前提约束 | §5.4 架构上界 + §7.10 RuntimePolicyApplier Capability Matrix |
| 7 | **Probe 多目标混合** | 增加对策防止机场系统性虚高（多目标 URL + 随机 payload 大小）；注明开销权衡 | §8.3 ProbeService + §6.4 机场 Probe 偏差 |

---

#### P2 — 可推迟，不可遗忘

| # | 任务 | 说明 | 文档对应 |
|---|------|------|---------|
| 8 | **active-set 内部加权分配** | Warmup 节点不进入 production selector，仅接收 probe 流量（因 xray selector uniform random，无法 partial traffic）；STABILITY_VERIFICATION 阶段不入生产 | §5.2 做不到 + §10.1 Recovery Confirmation FSM |
| 9 | **DNS 缓存生命周期** | 定义缓存可信时长 + 失效策略；缓存状态机：[DNS_CACHE_VALID] → N 次连续失败 → [DNS_CACHE_INVALIDATED] → TTL 到期 → proactive re-resolution | §13.4 DNS Cache Confidence Lifecycle |
| 10 | **拒绝 AI 调度正式理由** | 写入文档防止未来复杂度爆炸：observability 太弱、ground truth 不存在、reward function 不可验证、用户行为 causality 污染严重；heuristic + FSM 行为可解释、可审计、可回放 | §18 关键决策备忘 |

---

#### 已完成对照（v7.2 状态）

上述 P0/P1/P2 任务完成状态：

| # | 完成状态 |
|---|---------|
| 1 | ✅ **代码+文档完成**（v7.2）。`RecoveryConfirmationFsm.cs` + `GlobalFreezeController.cs` + `IClock.cs` + `NodeHealthState` 枚举 + `ProfileExItem` 持久化字段 |
| 2 | ✅ **代码+文档完成**（v7.2）。`XrayStatsPoller.cs` + `ScoreCalculator.cs` 添加三重禁止规则注释 |
| 3 | ✅ 文档已写入（§6.1~6.7 Known Limitations） |
| 4 | ✅ 文档已写入（文档头部重定位为 Conservative Failure Isolation System） |
| 5 | ✅ 文档已写入（§13），代码实现已完成（DnsCacheManager + FailureType 扩展） |
| 6 | ✅ §5.4 架构上界 + §7.10 Capability Matrix 已写入 |
| 7 | ✅ 文档已写入（§6.4 + §8.3），代码实现已完成（ProbeHeavyFraction + heavy probe） |
| 8 | ✅ 文档已写入（§5.2 + §10.1），代码实现已完成（explorer 隔离） |
| 9 | ✅ 文档已写入（§13.4），代码实现已完成（DnsCacheManager confidence lifecycle） |
| 10 | ✅ §18 决策备忘已写入 |

> **v7.2 变更**：P0 #1 和 #2 从"文档已完备，代码未同步"升级为"代码+文档完成"。新增 8 个源文件。
>
> **v7.3 变更**：P1 #5（DNS 归因分离）、#7（Probe 多目标混合）、#9（DNS 缓存生命周期）从"文档已完备，代码未同步"升级为"代码+文档完成"。新增 3 个源文件，26 个测试。全量测试 322 total（319 pass + 3 xray integration）。

---

### v7.2 P0 代码实现记录（2026-05-22）

#### 新增文件

| 文件 | 说明 |
|------|------|
| `IClock.cs` | 时间抽象接口 + `SystemClock`（生产）+ `FakeClock`（测试），三个操作：`UtcNow` / `GetTimestamp` / `Delay` |
| `RecoveryConfirmationFsm.cs` | 四阶段恢复状态机：ACTIVE → FAILED → RECOVERY_PROBING → STABILITY_VERIFICATION → ACTIVE。含指数退避（上限 30min）+ 状态迁移合法性检查（§10.7） |
| `GlobalFreezeController.cs` | 全局不稳定性冻结机制：>60% active 节点在 15s 窗口内失败 → 冻结 60s。含 freeze hysteresis（120s cooldown，期间再触发 → escalate 到 EmergencyDisable） |
| `RecoveryConfirmationFsmTests.cs` | 23 tests：合法/非法状态迁移、recovery probing 成功计数、stability verification 计时器、指数退避、cooldown budget、完整生命周期 |
| `GlobalFreezeControllerTests.cs` | 21 tests：freeze 触发/阻塞/解除、freeze hysteresis、escalation、边界条件、窗口过期、幂等性 |

#### 修改文件

| 文件 | 变更 |
|------|------|
| `NodeState.cs` | 新增 `NodeHealthState` 枚举（Active/Failed/RecoveryProbing/StabilityVerification）；新增 recovery FSM 状态字段和方法 |
| `AdaptiveSchedulerManager.cs` | 创建并管理 `RecoveryConfirmationFsm` + `GlobalFreezeController`；monitor 循环中集成 freeze 评估 + recovery 状态推进；`_freezeController.EmergencyDisableRequested` 事件订阅 |
| `FailureCollector.cs` | 新增可选 `GlobalFreezeController` 参数；`RecordFailure` 中通知 freeze controller |
| `ProbeService.cs` | 新增可选 `RecoveryConfirmationFsm` 参数；`ProbeOneAsync` 中路由 recovery 状态节点到 FSM；`ProbeCooldownRecoveryAsync` 中转换 FAILED → RECOVERY_PROBING |
| `ScoreCalculator.cs` | 新增 throughput 禁止进入评分的注释声明 |
| `XrayStatsPoller.cs` | 新增 throughput 三重禁止规则（P0 #2） |
| `ProfileExItem.cs` | 新增 4 个 recovery FSM 持久化字段（`AdaptiveHealthState` / `AdaptiveRecoveryProbeSuccess` / `AdaptiveBackoffLevel` / `AdaptiveStabilityVerificationStart`） |
| `CooldownFsm.cs` | FNV-1a hash 添加 `unchecked` 块（修复 checked arithmetic 溢出） |

#### 与设计文档的偏差

| 项目 | 设计文档要求 | 实际实现 | 原因 |
|------|-------------|---------|------|
| IClock 采用范围 | 所有时间敏感模块通过构造函数注入 IClock | 仅新模块（RecoveryConfirmationFsm、GlobalFreezeController）使用 IClock；已有模块（CooldownFsm、FailureCollector、ProbeService）保持直接使用 `DateTime.UtcNow` | 最小化对已有代码的改动范围，避免引入回归风险。已有模块的 IClock 迁移可作为后续 P2 重构项 |
| Freeze 期间 cooldown ejection | §11.3: "不触发新的 cooldown" | **v7.3 已修复**：freeze 期间 `FailureCollector.RecordFailure` 检查 `_freezeController.IsFrozen`，仅更新 EWMA 后 return，不递增 `consecutiveFailures`，不调用 `CooldownFsm.TryEnterCooldown`。观测继续（EWMA 真实反映质量），状态迁移冻结（cooldown 不推进）。telemetry 标记 `freeze_gate: true`。详见 §11.8 + `FailureCollector.cs` freeze gate + `FreezeGateTests.cs`（7 tests） |
| STABILITY_VERIFICATION 时长 | 默认 5 分钟，可配置 | 默认 5 分钟，构造函数参数可配置 | 一致 |

#### 测试覆盖增量

| 新增测试文件 | 测试数 | 覆盖内容 |
|-------------|--------|---------|
| `RecoveryConfirmationFsmTests.cs` | 23 | 合法/非法状态迁移、recovery 成功计数、STABILITY_VERIFICATION 计时器、指数退避、cooldown budget、完整生命周期、ResetHealthFsm |
| `GlobalFreezeControllerTests.cs` | 21 | freeze 触发/阻塞/解除、hysteresis、escalation、边界条件、窗口过期、snapshot |
| **新增总计** | **44** | |

> 全量测试：296 total（293 pass + 3 xray integration tests 需要 xray-core）。

---

### v7.3 P1 DNS 归因分离 + 随机载荷探活实现记录（2026-05-22）

#### 新增文件

| 文件 | 说明 |
|------|------|
| `DnsCacheManager.cs` | DNS 缓存管理器，实现 DNS Cache Confidence Lifecycle（§13.4）。缓存 IP 解析结果（TTL 300s），N 次连续失败（默认 3）后失效。支持 `ResolveWithCacheAsync`、`OnCachedIpConnectionFailed`、`OnCachedIpConnectionSucceeded`、`InvalidateCache` |
| `DnsCacheManagerTests.cs` | 18 tests：初始状态、缓存写入/读取、confidence 生命周期（hit 递增 / miss 递减 / N 次后失效）、TTL 到期（含边界）、缓存失效、ResolveWithCacheAsync 缓存命中路径、线程安全、节点间缓存独立性 |
| `DnsAttributionTests.cs` | 8 tests：DnsResolutionFailure/DnsPoisoningSuspected 零惩罚、RecordFailure no-op、DNS vs Timeout 对比、10 次 DNS 失败不进 cooldown、DNS 失败不喂入 GlobalFreeze、DNS+真实失败混合场景 |

#### 修改文件

| 文件 | 变更 |
|------|------|
| `FailureCollector.cs` | 新增 `DnsResolutionFailure`、`DnsPoisoningSuspected` 枚举值。`RecordFailure` 对两个 DNS 类型 early return（与 TlsError 一致，无惩罚、不进 cooldown）。`GetPenalty` 返回 `(0.0, node.EwmaLatencyMs)` |
| `NodeState.cs` | 新增 4 个 DNS 缓存字段（`_cachedIp`、`_dnsLastResolved`、`_dnsCacheConfidence`、`_dnsConsecutiveCacheFailures`）。方法：`SetCachedIp`、`OnDnsCacheHit`、`OnDnsCacheMiss(invalidateAfter)`、`InvalidateDnsCache`、`IsDnsCacheExpired(ttlSeconds, now?)`。`IsDnsCacheExpired` 新增可选 `DateTime? now` 参数用于测试确定性 |
| `BootstrapProber.cs` | `InitializeAsync` 新增可选 `DnsCacheManager?` 参数。`ProbeOneAsync` 先尝试 DNS 缓存解析（如 host 非 IP），用缓存 IP 进行 TCP connect。成功则调用 `dnsCache.OnCachedIpConnectionSucceeded`，失败则调用 `OnCachedIpConnectionFailed` |
| `ProbeService.cs` | P1#7: 新增 `_heavyFraction`（来自 `config.ProbeHeavyFraction`）和 `_rng` 字段。`ProbeOneAsync` 按概率随机选择 heavy probe（GET + 下载最多 64KB 响应体）或 light probe（HEAD），以破坏机场小包加速分类。配置文件支持多 URL 探活 + heavy probe 比例可配 |
| `ConfigItems.cs` | `AdaptiveSchedulerItem` 新增 `ProbeHeavyFraction` 属性（默认 0.2，范围 [0.0, 1.0]） |
| `AdaptiveSchedulerManager.cs` | 构造函数创建 `DnsCacheManager(_clock)`。`BootstrapAsync` 将 `_dnsCache` 传入 `_bootstrapper.InitializeAsync` |
| `FailureCollector.cs` | **§11.8 Freeze gate**：`RecordFailure` 在 freeze 期间（`_freezeController.IsFrozen`）仅更新 EWMA 后 return — 不递增 `consecutiveFailures`，不调用 `CooldownFsm.TryEnterCooldown`。观测继续，状态迁移冻结。telemetry 新增 `freeze_gate: true` 标记 |
| `FreezeGateTests.cs` | 7 tests：freeze 期间 EWMA 更新但 consecutiveFailures 不递增、不进 cooldown、多次失败 score 真实退化、freeze 结束后恢复正常路径、DNS 故障在 freeze 期间仍 no-op、多节点全部 blocked |

#### 与设计文档的偏差

| 项目 | 设计文档要求 | 实际实现 | 原因 |
|------|-------------|---------|------|
| 备用 DNS resolver（§13.2 Step 2） | "用备用 DNS resolver 重新解析（如果配置了）" | 仅使用 `Dns.GetHostAddressesAsync()` 解析，无备用 resolver 配置 | 备用 resolver 需要额外的 DNS 配置（DoH/DoT）和配置持久化，属于 P2 增强项。当前缓存 IP 重试已覆盖主要 DNS 故障场景 |
| `CachedIpRecovered` FailureType（§13.3） | 新增 `CachedIpRecovered` 类型标记"通过缓存 IP 成功连接" | 未添加此类型。缓存 IP 连接成功改为调用 `OnCachedIpConnectionSucceeded` 提升 confidence | 设计选择："通过缓存 IP 成功连接"是正面信号而非故障类型。用 confidence 递增（而非新 FailureType）更准确反映语义——这不是 failure，是 recovery |
| TTL 到期主动触发（§13.4） | "TTL 到期 → 主动触发重新解析（不依赖失败事件）" | TTL 过期检查是懒惰的（lazy）——仅在 `ResolveWithCacheAsync` 被调用时检查并刷新 | 主动触发需要后台定时器轮询所有节点缓存，增加复杂度和资源开销且收益有限。lazy check-on-use 对于 probe/连接场景已足够（每次连接前都会调用 `ResolveWithCacheAsync`） |
| 新 IP = 缓存 IP 时 confidence 行为（§13.4） | "新 IP 与缓存 IP 相同 → confidence 保持" | `OnDnsCacheHit()` 递增 confidence（+1）而非保持不变 | 当 DNS 重新解析返回相同 IP 时，increment 比 keep 更优——表明解析结果仍然有效、未被污染，confidence 理应增强 |
| **`IsDnsCacheExpired` 新增 `DateTime? now` 参数** | NodeState 使用 `DateTime.UtcNow` | 方法签名新增可选 `DateTime? now = null`，测试可传入 `FakeClock.UtcNow`，生产代码不传参使用真实时间 | 延续 §17.3 IClock 测试哲学但应用于方法级别（而非 NodeState 注入 IClock），避免模型类承担基础设施依赖。这是 P0 IClock 偏差的延续 |

#### 测试覆盖增量

| 新增测试文件 | 测试数 | 覆盖内容 |
|-------------|--------|---------|
| `DnsCacheManagerTests.cs` | 18 | 缓存 CRUD、confidence 生命周期、TTL 到期（含 ExactAtTtl 边界）、失效策略、ResolveWithCacheAsync 缓存命中路径、线程安全、节点间独立性 |
| `DnsAttributionTests.cs` | 8 | GetPenalty 零惩罚、RecordFailure no-op、DNS vs Timeout 对比、10 次 DNS 失败不进 cooldown、DNS 失败不喂入 GlobalFreeze、混合失败场景 |
| `FreezeGateTests.cs` | 7 | §11.8: freeze 期间 EWMA 更新/consecutiveFailures 不递增/cooldown blocked、freeze 结束恢复、DNS no-op 保持、多节点 blocked |
| **P1 新增总计** | **33** | |

> 全量测试：329 total（326 pass + 3 xray integration tests 需要 xray-core）。

---

### 历史完成记录

#### P0 — 立即执行（1~3 天，阻断上线）✅ 全部完成（2026-05-21）

| # | 任务 | 状态 | 实现 | 测试 |
|---|------|------|------|------|
| 0.1 | 验证 xray tag duplication 行为 | ✅ | 集成测试 + 源码确认。结论：xray v26.3.27 去重 selector，duplication 无效。系统重定位为 Active-Set Scheduler | `XrayTagDuplicationIntegrationTests.cs`（2 tests，作为行为契约检测器） |
| 0.2 | FailureType 差异化惩罚 | ✅ | `FailureCollector.GetPenalty()`：Refused(1.0), Timeout(0.8), NetworkError(0.7), UnexpectedEof(0.4), TlsError(0.0, early return, skips cooldown) | `FailureCollectorTests.cs`（9 tests） |
| 0.3 | Bootstrap 覆盖历史分数验证 | ✅ | 代码审查确认：`BootstrapProber.ProbeOneAsync` ALL code paths call `node.UpdateScore()` — 无路径保留旧分数。ScoreCalculator worst-case → 1.0 | `BootstrapAndScorePersistenceTests.cs`（5 tests） |
| 0.4 | Active Set Hysteresis | ✅ | `ActiveSetManager`：Entry=60, Exit=35, `_currentActiveSet` tracking, explorer 机制 | `ActiveSetManagerTests.cs`（10 tests） |
| 0.5 | Adaptive Feature Flag 紧急旁路 | ✅ | `EmergencyDisableAdaptiveAsync()`：设置 Enabled=false + StopAsync | `EmergencyDisableAdaptiveTests.cs`（4 tests） |

**P0 关键发现**：tag duplication 的发现是整个项目最关键的架构修正。

### P1 — 近期执行（3~7 天，功能完整性）✅ 全部完成

| # | 任务 | 状态 | 实现 |
|---|------|------|------|
| 1.1 | debounce 从 30s 降至 15s | ✅ | `ReloadPolicyApplier.MinReloadInterval` = 15s |
| 1.2 | ScoreLogger → adaptive.log（JSONL） | ✅ | `ScoreLogger` 重写为文件型 JSONL logger |
| 1.3 | ActiveSetManager top-K 逻辑文档化 | ✅ | K 公式 `max(2, ceil(N*2/3))` |
| 1.4 | AdaptiveSchedulerManager 生命周期 | ✅ | XmlDoc 覆盖 lifecycle/singleton/profile switching |
| 1.5 | xray 版本兼容性检查 | ✅ | `XrayVersionChecker` 解析 `xray -version` |
| 1.6 | ProbeUrl 暴露到 Settings UI | ✅ | Adaptive 设置 Tab |
| 1.7 | 分数过期机制 | ✅ | >4h 过期分数重置为 50 |
| **1.8** | **Explorer 隔离** | ✅ | Explorer 不入 production selector（§11.1） |
| **1.9** | **Hash-based cooldown jitter** | ✅ | FNV-1a stable offset（§11.2） |
| **1.10** | **Reload Budget** | ✅ | 滑动 window 自适应 debounce（§11.3） |
| **1.11** | **Decision Traceability** | ✅ | causal trace in JSONL events（§11.4） |
| **1.12** | **Stability Objective 文档化** | ✅ | §4.4 + ActiveSetManager.cs doc（§11.5） |
| **1.13** | **DNS 归因分离** | ✅ | FailureType 扩展（DnsResolutionFailure/DnsPoisoningSuspected）；FailureCollector DNS 故障零惩罚/不进 cooldown；DnsCacheManager 缓存 confidence 生命周期 |
| **1.14** | **Probe 多目标混合** | ✅ | ProbeService 支持多 URL（换行分割）；heavy probe（GET + 64KB body drain）按 `ProbeHeavyFraction` 比例随机触发，破坏机场小包加速 |
| **1.15** | **DNS 缓存生命周期** | ✅ | DnsCacheManager + NodeState 缓存字段；300s TTL；N=3 连续失败失效；confidence 递增/递减状态机 |

### P2 — 中期执行（1~2 周，稳固性）✅ 全部完成（2026-05-22）

| # | 任务 | 状态 | 实现 | 测试 |
|---|------|------|------|------|
| 2.1 | XrayStatsPoller | ✅ | `XrayStatsPoller.cs` + `IXrayStatsClient.cs`；可配置 pollInterval | `XrayStatsPollerTests.cs`（12 tests） |
| 2.2 | 边界情况：1/2 节点处理 | ✅ | `CooldownFsm.ComputeMaxCooldown`：1→0, 2→1, 3+→max(1, floor(N/3)) | `BoundaryNodeCountTests.cs`（10 + 12 inline） |
| 2.3 | PerTagProxyTraffic 线程安全 | ✅ | `ConcurrentDictionary<string, NodeTrafficSnapshot>` | `PerTagProxyTrafficTests.cs`（6 tests） |
| 2.4 | ProbeService 并发上限 | ✅ | SemaphoreSlim gate：`max(3, ceil(N/5))` | `ProbeConcurrencyTests.cs`（5 + 14 inline） |
| 2.5 | Replayable Telemetry 完整事件 | ✅ | FailureCollector 发出 probe_result + ewma_update | `ReplayableTelemetryTests.cs`（9 tests） |
| 2.6 | 探活多目标支持 | ✅ | ProbeUrl 按换行分割；所有目标成功取平均 TTFB | `MultiTargetProbeTests.cs`（8 tests） |

### P3 — 长期执行 ✅ 3.1/3.2 已完成

| # | 任务 | 状态 |
|---|------|------|
| 3.1 | RuntimePolicyApplier | ✅ `IXrayHandlerClient` + `RuntimePolicyApplier` 双模策略（11 tests） |
| 3.2 | 调度质量指标 | ✅ `SchedulingQualityMetrics` + `QualityMetricsReporter`（15 tests） |
| 3.3 | UDP/QUIC 独立节点池 | 🔵 未开始 |
| 3.4 | 调度决策审计日志 UI | 🔵 未开始 |
| 3.5 | 外部 balancer / true weighted routing | 🚫 禁止 |

### 完整测试清单

| 测试文件 | 测试数 | 覆盖内容 |
|---------|--------|---------|
| `FailureCollectorTests.cs` | 9 | 各 FailureType penalty 值，TlsError no-op |
| `BootstrapAndScorePersistenceTests.cs` | 5 | Score floor, worst-case, Bootstrap 覆盖历史, 分数过期 |
| `ActiveSetManagerTests.cs` | 12 | Entry/Exit 阈值, sticky, oscillation 免疫, explorer 排除, cooldown 排除, 全冷却兜底 |
| `EmergencyDisableAdaptiveTests.cs` | 4 | 幂等性, IsRunning/GetCurrentConfig/Nodes/ProbePorts 清空 |
| `XrayTagDuplicationIntegrationTests.cs` | 2 | xray selector dedup 行为契约（需 xray-core） |
| `ScoreLoggerJsonlTests.cs` | 6 | JSONL 格式, 事件类型, 文件写入 |
| `TopKFormulaTests.cs` | 4 | N=1~20 top-K 边界 |
| `XrayVersionCheckerTests.cs` | 14 | 版本解析, 比较, 最低版本 |
| `ScoreExpirationTests.cs` | 5 | 4h 过期重置, 持久化读写 |
| `XrayStatsPollerTests.cs` | 12 | 异常检测, 计数器重置, 边界, 生命周期 |
| `BoundaryNodeCountTests.cs` | 10 + 12 inline | Cooldown 边界, 全冷却兜底, 变更检测 |
| `PerTagProxyTrafficTests.cs` | 6 | 线程安全, 并发读写 |
| `ProbeConcurrencyTests.cs` | 5 + 14 inline | SemaphoreSlim gate, 并发公式验证 |
| `ReplayableTelemetryTests.cs` | 9 | probe_result, ewma_update, TlsError event, 完整链路 |
| `MultiTargetProbeTests.cs` | 8 | 多 URL 探活, 平均 TTFB, 全失败判定 |
| `RuntimePolicyApplierTests.cs` | 11 | API 可用/不可用, diff, fallback, disposal |
| `SchedulingQualityMetricsTests.cs` | 15 | 熵, P95, 均值, 标准差 |
| `RecoveryConfirmationFsmTests.cs` | 23 | P0#1: 四阶段恢复状态机、合法/非法迁移、指数退避、完整生命周期 |
| `GlobalFreezeControllerTests.cs` | 21 | P0#1: freeze 触发/阻塞/解除、hysteresis、escalation、边界条件 |
| `DnsCacheManagerTests.cs` | 18 | P1#5: 缓存 CRUD、confidence 生命周期、TTL 到期（含边界）、失效策略、ResolveWithCacheAsync 缓存命中、线程安全、独立性 |
| `DnsAttributionTests.cs` | 8 | P1#5: GetPenalty 零惩罚、RecordFailure no-op、DNS vs Timeout 对比、多次 DNS 失败不进 cooldown、GlobalFreeze 隔离、混合失败场景 |
| `FreezeGateTests.cs` | 7 | §11.8: freeze 期间 EWMA 更新/cooldown blocked、freeze 结束恢复、DNS no-op、多节点 blocked |
| **总计** | **304** (301 pass, 3 xray integration tests need xray-core) | |

---

## 17. 验收标准与测试覆盖

### 13.1 核心调度行为（10/10 全部覆盖）

| # | 测试场景 | 预期行为 | 覆盖状态 |
|---|---------|---------|---------|
| 1 | 全部节点 cooldown | 选 cooldown 剩余最短节点，不崩溃 | ✅ `BoundaryNodeCountTests`：4 tests |
| 2 | 节点连续 2 次失败 | 进入 cooldown，其他节点接管 | ✅ `BoundaryNodeCountTests`：4 tests |
| 3 | cooldown 节点数达到上限 | 超出节点降权而非 cooldown | ✅ `BoundaryNodeCountTests`：12 inline + 1 test |
| 4 | Bootstrap 发现死节点 | Score=1.0，不自动进 cooldown | ✅ `BootstrapAndScorePersistenceTests`：2 tests |
| 5 | 历史分数 90 + Bootstrap 失败 | 分数覆盖为 1.0 | ✅ `BootstrapAndScorePersistenceTests`：1 test |
| 6 | TlsError 失败 | EWMA 不更新，不进入 cooldown | ✅ `FailureCollectorTests` + `ReplayableTelemetryTests`：3 tests |
| 7 | xray selector 去重行为 | `[A×3, B×1]` → A≈50% | ✅ `XrayTagDuplicationIntegrationTests`（需 xray） |
| 8 | active-set 内均匀分配 | 各节点流量接近均匀 | ✅ 与 #7 同一测试 |
| 9 | score 在 45~55 抖动 | active set 不频繁变化 | ✅ `ActiveSetManagerTests`：2 tests |
| 10 | 紧急旁路触发 | adaptive 停止，不崩溃 | ✅ `EmergencyDisableAdaptiveTests`：5 tests |

### 13.2 用户体验指标（观测用，不作为阻断条件）

| # | 指标 | 目标值 | 测量方法 |
|---|------|--------|---------|
| 1 | 节点质量变化响应时间 | ≤ 25s（正常）/ ≤ 130s（节流） | 实测 |
| 2 | 好节点 vs 差节点选中概率比 | ≥ 3:1（score 差 50 分时） | 从 adaptive.log 计算 |
| 3 | 冷启动后首次请求成功率 | ≥ 95% | 实测 |
| 4 | active set reload 频率 | < 4 次/小时（正常网络环境） | 从 adaptive.log 统计 xray_reload 事件 |

### 13.3 确定性测试基础设施（v7.1 新增）

当前测试依赖真实 `DateTime.UtcNow`、`Random.Shared`、`Task.Delay`、`Stopwatch.GetTimestamp`，随着 Cooldown、Freeze、Debounce 等时间敏感逻辑增多，测试会越来越 flaky。正式定义两个核心抽象接口：

#### IClock — 时间抽象

```csharp
public interface IClock
{
    /// <summary>当前 UTC 时间。生产实现 = DateTime.UtcNow。</summary>
    DateTime UtcNow { get; }

    /// <summary>高精度时间戳，用于延迟测量。生产实现 = Stopwatch.GetTimestamp。</summary>
    long GetTimestamp();

    /// <summary>高精度计时器频率。生产实现 = Stopwatch.Frequency。</summary>
    long TimestampFrequency { get; }

    /// <summary>异步延迟。生产实现 = Task.Delay。测试实现可跳过或加速。</summary>
    Task Delay(TimeSpan duration, CancellationToken ct = default);
}

public sealed class SystemClock : IClock
{
    public DateTime UtcNow => DateTime.UtcNow;
    public long GetTimestamp() => Stopwatch.GetTimestamp();
    public long TimestampFrequency => Stopwatch.Frequency;
    public Task Delay(TimeSpan duration, CancellationToken ct = default)
        => Task.Delay(duration, ct);
}

public sealed class FakeClock : IClock
{
    // 测试用：手动推进时间，不等待真实 wall-clock
    public DateTime UtcNow { get; set; } = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    public long GetTimestamp() => _timestamp;
    public long TimestampFrequency => 10_000_000; // 1 tick = 100ns
    public Task Delay(TimeSpan duration, CancellationToken ct = default)
    {
        UtcNow = UtcNow.Add(duration);  // 瞬间跳跃
        return Task.CompletedTask;
    }
}
```

#### IRandom — 随机数抽象（当前已通过 FNV-1a hash 消除大部分随机依赖）

```csharp
public interface IRandom
{
    /// <summary>返回 [0.0, 1.0) 的 double。仅在需要真随机时使用。</summary>
    double NextDouble();
}

public sealed class SystemRandom : IRandom
{
    public double NextDouble() => Random.Shared.NextDouble();
}

public sealed class SeededRandom(int seed) : IRandom
{
    private readonly Random _rng = new(seed);
    public double NextDouble() => _rng.NextDouble();
}
```

#### 使用原则

1. **`IClock` 是必需的**：CooldownFsm、FreezeController、ReloadPolicyApplier、ProbeService 都依赖时间。所有模块通过构造函数注入 `IClock`，默认使用 `SystemClock`。测试中使用 `FakeClock` 实现确定性时间推进。

2. **`IRandom` 是备选的**：v7.1 已将 Cooldown jitter 从 `Random` 迁移到 FNV-1a hash（确定性），大部分模块不再需要随机数。`IRandom` 作为接口保留，用于未来可能需要随机性的场景（如 explorer selection）。

3. **不引入完整的 "TimeProvider" 抽象**：.NET 8 的 `TimeProvider` 功能丰富但抽象层级过高。`IClock` 只包含系统实际需要的三个时间操作，避免过度抽象。

#### Current Time Source Limitation（v7.3 警告）

**当前系统存在 dual-time-source（双时间源）**：

| 模块类别 | 时间源 | 确定性测试 |
|---------|--------|-----------|
| **新 FSM 模块**：`RecoveryConfirmationFsm`、`GlobalFreezeController`、`DnsCacheManager` | 注入的 `IClock`（生产 = `SystemClock`，测试 = `FakeClock`） | ✅ 完全确定性 |
| **Legacy 模块**：`CooldownFsm`、`FailureCollector`、`ProbeService`、`BootstrapProber`、`ReloadPolicyApplier` | 直接调用 `DateTime.UtcNow` / `Task.Delay` / `Stopwatch.GetTimestamp` | ❌ 依赖真实 wall-clock |
| **Model 层**：`NodeState.IsDnsCacheExpired`、`NodeState.IsInCooldown` | `DateTime.UtcNow`（可选 `DateTime? now` 参数用于测试） | ⚠️ 部分确定性 |

**风险**：任何人对代码库的认知如果假定"全系统已 deterministic"，会在 legacy 模块中写出 flaky 的基于 real time 的测试。在 double-time-source 环境下跨模块时序语义可能不一致（例如 legacy cooldown 使用真实时间判断到期而新 freeze controller 使用注入时钟）。

**当前策略**：增量迁移——新模块全部注入 `IClock`，legacy 模块暂不强制重构（避免 constructor pollution + regression surface 爆炸）。**Full unification planned for P2**。在 unification 完成前，所有跨模块时序依赖必须显式传参（如 `DateTime? now`）而非隐式信任同一时间源。

4. **测试中的典型使用**：
```csharp
var clock = new FakeClock();
var cooldown = new CooldownFsm(nodes, clock, maxCooldown: 3);
// 节点进入 cooldown
cooldown.EnterCooldown(nodes[0]);
clock.UtcNow = clock.UtcNow.AddMinutes(10); // 快进 10 分钟
// cooldown 应已到期
Assert.False(nodes[0].IsInCooldown);
```

---

## 18. 关键决策备忘

| 决策点 | 选择 | 理由 |
|--------|------|------|
| 系统定位 | Active-Set Health Controller（机制）+ Mixed-Flow Stability Controller（目标） | failure-driven proxy control plane，不是智能负载均衡器 |
| 调度粒度 | 连接级 | 请求级需协议感知；会话级太粗糙 |
| 延迟数据源 | TTFB via HTTP HEAD | xray 不暴露 TCP 级统计，唯一可行方案 |
| EWMA α | time-decayed | 采样间隔不均匀，固定 α 会冻结或过度响应 |
| 评分放大 | 平方 | 线性映射好坏节点权重差距不足以驱动调度倾斜 |
| 并发锁 | 私有 `_lock` 对象 | 消除 `lock(this)` 外部竞争风险（CA2002） |
| HttpClient | 按 tag 复用 handler | 避免 socket exhaustion |
| Cooldown 上限 | 1→0, 2→1, 3+→max(1, floor(N/3)) | 防雪崩；小数节点更宽松 |
| Cooldown Jitter | FNV-1a hash-based stable offset | 防 recovery burst + 跨重启稳定 |
| 计时器 | `Stopwatch.GetTimestamp` | Windows `DateTime` 误差 ±15ms，不可接受 |
| QUIC 处理 | 独立池（Phase 3） | QUIC 连接语义与 TCP 完全不同 |
| 吞吐率 | 辅助指标，不入主公式 | 主要用于异常检测，xray stats 分辨率不足 |
| 兜底策略 | 选 cooldown 最短节点 | 总好过返回空导致 xray 无可路由 outbound |
| Active Set 迟滞 | Entry=60, Exit=35, 缓冲带 25 分 | 防 score 震荡导致频繁 reload |
| Bootstrap 覆盖策略 | 始终覆盖历史分数（包括高分） | 历史高分可能来自已死亡节点 |
| 分数过期 | 历史分数超过 4h 回退到 50 | 长时间关机后历史分数无效 |
| 探活多目标 | 支持多个 ProbeUrl，取平均值 | 减少单一目标的偶发抖动影响 EWMA |
| Reload Budget | 软节流（15s/60s/120s），不硬拒绝 | xray reload = 连接中断，但坏节点更差 |
| Explorer | 仅 probe traffic，不入 production selector | exploration ≠ production；消除不必要 reload 路径 |
| Decision Traceability | causal trace in JSONL | 多 heuristic 系统必须可解释 |
| ScoreLogger 格式 | JSONL，独立文件，可回放 | 出问题时能重现调度决策链 |
| **禁止 weighted routing hack** | 新加权方案必须在 xray 源码级验证后才能设计 | tag duplication 已被证伪 |
| Stability Objective | 稳定性 > 响应性 > 最优性 | 所有 heuristic 的统一约束框架 |
| **拒绝 AI/RL scheduling** | 使用 heuristic + FSM，不引入 AI/强化学习调度 | observability 太弱、ground truth 不存在、reward function 不可验证、用户行为 causality 污染严重。AI 调度需要精确的 state observation 和可验证的 reward signal，而当前系统两者都不具备。probe latency 是复合路径指标，throughput 受用户行为因果倒置——用这些信号训练 RL agent 只会学到 noise pattern，不会学到节点质量。heuristic + FSM 虽然"笨"，但行为可解释、可审计、可回放 |

---

## 19. 实时节点速度显示

**日期**：2026-05-21

### 问题

主界面表格的 Speed 列只在手动测速时才更新。Adaptive 负载均衡运行时，右下角状态栏有总速度，但无法知道每个节点当前的实际吞吐量。

### 实现方案

不改表结构、不新增列、不新增事件。利用已有的统计管线数据直接写到现有 Speed 列的显示属性。

### 数据流

```
xray /debug/vars (1s 轮询)
  → StatisticsXrayService.ParseOutput()
    → PerTagProxyTraffic: { tag → NodeTrafficSnapshot }
  → StatisticsManager.UpdateServerStat()
    → 对每个 tag 计算 delta，生成 child ServerSpeedItem
  → ProfilesViewModel.UpdateStatistics()
    → 设置 item.SpeedVal = format(ProxyUp + ProxyDown)
```

### 设计决策

| 决策 | 选择 | 理由 |
|------|------|------|
| 新增列？ | 否，复用现有 Speed 列 | 零 UI 改动 |
| 改 `Speed` 字段？ | 否，只写 `SpeedVal` | `Speed` 是持久化测速值 |
| 无流量时显示什么？ | 保持上次值或空 | 避免列频繁闪烁 |
| 单位 | KB/s 自动升档 MB/s | 与现有速度显示风格一致 |
| Adaptive 关闭后？ | SpeedVal 自动恢复为测速值 | 下次表格刷新从 DB 读取原始值 |

---

## 20. 未来方向：被动混合流观测器

> **核心认知升级**：不需要识别"这是 YouTube"，只需要识别"当前连接像不像长流媒体"。

### 16.1 为什么不做 L7 业务识别

TLS1.3、ECH（未来更严重）、QUIC、HTTP3、CDN 共用 IP、domain fronting、connection coalescing 都会让 L7 识别越来越不可能。且项目不想做 MITM / DPI / 解密 TLS。

因此：**精确业务识别 → 基本放弃**。

### 16.2 正确方向：流量行为模式识别

不识别"业务"，而识别**"流量行为模式"**。

即使 TLS 加密，仍然可观测（不需要解密）：

- 连接持续时间
- bytes/sec
- throughput variance
- stall 模式
- idle pattern
- upstream/downstream ratio

这些是 **metadata**，不是 payload。

### 16.3 LongFlowCandidate 启发式检测

满足以下条件则标记为 LongFlowCandidate：

```
connection_duration > 20s
AND avg_throughput > 3Mbps
AND continuous chunk transfer pattern
```

**不需要知道**是 YouTube、Twitter 还是 Telegram——因为它们对代理的要求实际上很接近：稳定吞吐、低 stall、长流稳定性。

### 16.4 Stall Suspicion 检测

```
连续 5s: throughput ≈ 0
但 connection 未关闭
```

这非常像 buffering / QoS throttling。是重要的节点质量退化信号。

### 16.5 三层架构（未来推荐）

**第一层 — Health Layer（当前已有）**

负责：dead node、RTT、loss、timeout

**第二层 — Passive Flow Observer（未来）**

负责：long-flow suspicion、stall ratio、sustained degradation

**第三层 — Conservative Policy Adjustment**

例如：某节点长期 stall ratio 很高 → 降低 active-set 优先级。**不是立刻 eject**，而是渐进降级。

**关键克制：Phase 2 只观察，不直接调度。**

### 16.6 Throughput 信号使用原则

**当前禁止**：`throughput → score`（直接映射）

原因：throughput signal 因果性弱、噪声极大、受用户行为影响严重（暂停视频、页面切后台、chunk 较小、CDN cache hit、视频码率变化都会影响吞吐）。

**未来如果引入**：仅允许 **Throughput Veto**：

```
进入 active-set 条件:
  score > 60
  AND sustained_throughput_healthy
```

但**必须先长期 telemetry 验证** throughput 与真实体验的相关性。

### 16.7 为什么行为观测比域名识别更有价值

很多代理 QoS 问题根本不是"网站"层面的，而是 ISP QoS、国际出口、congestion、peering、packet pacing 层面的——这些与具体域名无关。所以**行为观测比域名识别更有价值**。

### 16.8 现实能力边界

真正能做到的不是"AI 理解用户业务"，而是**"观察哪些节点长期不适合媒体长流"**。这已经非常有价值，而且复杂度仍然可控。

### 16.9 Time Window Hysteresis（未来考虑）

当前 hysteresis 仅基于瞬时 score 检查。GPT 评审建议增加时间窗口：

```
进入 active-set: score > 60 持续 20s
退出 active-set: score < 35 持续 10s
```

目的：防止边界抖动、防止 reload storm。当前未实施，作为 Phase 2 候选优化项。

---

## 21. 真正成功标准

> **v7.0 修订**：成功标准从"媒体体验视角"改为"控制面稳定性视角"。

**本系统不是智能负载均衡器，成功标准不是"找到最好的节点"。**

真正的成功标准是控制面稳定性 + 用户自主性：

```
用户极少需要手动干预（核心指标）
晚高峰不明显恶化（所有流量类型的整体稳定性）
长连接不频繁断开（WebSocket / Telegram / SSH 持续性）
control plane 不 oscillation（active-set churn 低）
reload 频率低（< 4 次/小时正常环境）
用户感觉"它一直稳定可用"（最终体验标准）
```

**明确不是成功标准的内容**：

- routing precision（75% vs 25% 无意义——系统不测量也不优化这个）
- latency optimality（不追求找 latency 最低的节点）
- throughput maximization（因果性错乱，无法作为优化目标）
- media quality scores（无法测量 buffering / stall / congestion，强行声称是虚伪的）
- AI scheduling intelligence（没有 AI，只有 heuristic + FSM）

**如果用户有以下感知，说明系统在工作**：不需要频繁手动切节点、晚上高峰期体验不显著变差、下载/视频/聊天不会突然中断、不会注意到"系统在后台调度"。

---

## 附录 A：优先级总表

| 级别 | # | 问题 | 影响 | 状态 |
|------|---|------|------|------|
| P0 | 0.1 | tag duplication 行为验证 | 整个 weighted scheduling 假设不成立 → 系统重定位 | ✅ |
| P0 | 0.2 | FailureType 差异化惩罚 | TlsError 不惩罚；Refused/Timeout/NetworkError/UnexpectedEof 梯度惩罚 | ✅ |
| P0 | 0.3 | Bootstrap 覆盖历史分数 | 历史高分可能来自已死亡节点 → 始终覆盖 | ✅ |
| P0 | 0.4 | Active Set Hysteresis | 防 score 震荡导致频繁 reload → Entry=60/Exit=35 | ✅ |
| P0 | 0.5 | EmergencyDisableAdaptive | 一键紧急旁路 | ✅ |
| P1 | 1.1 | debounce 30s→15s | 调度响应延迟缩短 ~40% | ✅ |
| P1 | 1.2 | ScoreLogger → adaptive.log | JSONL 文件型 logger | ✅ |
| P1 | 1.3 | top-K 逻辑文档化 | K=2/3 写入代码+文档 | ✅ |
| P1 | 1.4 | 生命周期文档化 | XmlDoc coverage | ✅ |
| P1 | 1.5 | xray 版本兼容性检查 | XrayVersionChecker + 14 tests | ✅ |
| P1 | 1.6 | ProbeUrl 暴露到 Settings UI | Adaptive 设置 Tab | ✅ |
| P1 | 1.7 | 分数过期机制 | 4h 过期 → 50 | ✅ |
| P1 | 1.8 | Explorer 隔离 | Explorer 不入 production selector | ✅ |
| P1 | 1.9 | Hash-based cooldown jitter | FNV-1a stable offset 防 recovery burst | ✅ |
| P1 | 1.10 | Reload Budget | 滑动 window 自适应 debounce（15/60/120s） | ✅ |
| P1 | 1.11 | Decision Traceability | causal trace in JSONL events | ✅ |
| P1 | 1.12 | Stability Objective 文档化 | §4.4 + ActiveSetManager.cs doc | ✅ |
| P2 | 2.1-2.6 | 稳固性增强 | 见 §12 详情 | ✅ |
| P3 | 3.1 | RuntimePolicyApplier | 双模策略：API+fallback | ✅ |
| P3 | 3.2 | 调度质量指标 | 熵 + P95 + 均值 + 标准差 | ✅ |
| P3 | 3.3 | UDP/QUIC 独立节点池 | 未开始 | 🔵 |
| P3 | 3.4 | 调度决策审计日志 UI | 未开始 | 🔵 |
| P3 | 3.5 | 外部 balancer / true weighted routing | 禁止实施 | 🚫 |
| Phase 2 | — | LongFlowCandidate / Stall Suspicion / Throughput Veto | 未来方向，只观察不调度 | 🔵 |
| Phase 2 | — | Time Window Hysteresis | 未来考虑 | 🔵 |

---

## 附录 B：文档来源与版本历史

| 版本 | 日期 | 文档 | 说明 |
|------|------|------|------|
| v3.0 | 2026-05-21 | CLAUDE-loadbalance.md | 原始设计（含 tag duplication 加权方案） |
| v3.1 | 2026-05-21 | adaptive-scheduler-final-audit.md | 综合审计报告（P0 验证后更新） |
| v4.0 | 2026-05-21 | CLAUDE-loadbalance.md | 设计文档更新（系统重定位为 Active-Set Scheduler） |
| v3.1 | 2026-05-22 | adaptive-scheduler-final-audit.md | 审计报告更新（P2/P3.1/P3.2 完成记录） |
| v5.0 | 2026-05-22 | CLAUDE-loadbalance-20260522.md | 初版合并：设计文档 + 审计文档 → 统一工程文档 |
| v2.0 | 2026-05-22 | CLAUDE-loadbalance-GPT.md | GPT 外部评审重构（设计哲学 + 未来方向） |
| **v6.0** | **2026-05-22** | **CLAUDE-loadbalance.md** | **最终合并版：v5.0 工程实现 + v2.0 设计哲学 + P1 稳定性增强** |
| v7.1 | 2026-05-22 | CLAUDE-loadbalance.md | 10 项语义精炼：DNS Cache Confidence Lifecycle、Freeze Hysteresis、State Transition Invariants |
| v7.2 | 2026-05-22 | CLAUDE-loadbalance.md | P0 代码实现：Recovery Confirmation FSM + Global Freeze Controller + 吞吐量三重禁止规则 |
| **v7.3** | **2026-05-22** | **CLAUDE-loadbalance.md** | **P1#5/#7/#9 代码实现：DNS 归因分离 + 随机载荷探活 + DNS 缓存生命周期** |

### 合并说明

v6.0 合并了三类信息源：

| 维度 | v5.0（工程文档） | v2.0（GPT 评审） | 合并处理 |
|------|-----------------|------------------|---------|
| 系统定位 | Active-Set Scheduler | Adaptive Media QoS Scheduler | 两层描述模型（§1.1） — 互补不冲突 |
| 视角 | 工程实现细节 | 设计哲学与未来方向 | 分别保留，明确标注 |
| 时间框架 | P0-P3 已完成 | Phase 2 展望 | §12 实施记录 + §16 未来方向 |
| 探活定义 | 技术实现 | 能力边界认知 | §7.3 整合（实现 + 认知约束） |
| 调度目标 | stability objective | media QoS objective | §4.4 统一稳定性框架 + §17 用户体验标准 |
| P1 稳定性 | — | Explorer 隔离 / Hash jitter / Reload Budget / Decision Traceability | §11 独立章节 |
| 未来方向 | 简要提及 | LongFlowCandidate / Stall Suspicion / 三层架构 / Throughput Veto | §16 独立章节 |

**两份源文档互补而非替代**。v5.0 是工程实现的权威记录，v2.0 是设计方向和远期目标的参考。v6.0 将两者统一在一份文档中，明确标注信息来源，消除冲突。