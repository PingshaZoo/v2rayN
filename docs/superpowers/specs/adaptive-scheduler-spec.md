# Adaptive Node Scheduler — Core Specification

**版本**: 1.3（2026-05-25 — §5.1 Anti-Churn: Adaptive Exit + Fallback Repair + MinTenure + ReloadCooldown）
**系统正式名称**: **Conservative Production Admission System**
**定位**: Conservative Production Admission System（机制层：Bounded Production Pool + Failure-Driven Promotion）
**核心收益来源**: 压缩 production surface area，减少错误调度，而非提高调度精度或寻找最快节点
**约束**: C# / Windows / v2rayN 架构，不 fork xray-core

> **本系统不是智能负载均衡器。** 核心价值在于减少错误调度——不把流量发给坏节点、不因 score 震荡频繁切换 active-set、不让探测流量混入生产流量。成功标准不是 routing precision，而是"用户极少需要手动干预"。

---

## 目录

0. [Terminology（术语定义）](#0-terminology术语定义)
1. [Stability Objective（稳定性目标）](#1-stability-objective稳定性目标)
2. [Runtime Constraints（运行时约束）](#2-runtime-constraints运行时约束)
3. [Control Plane vs Data Plane](#3-control-plane-vs-data-plane)
4. [State Machine（状态机）](#4-state-machine状态机)
5. [Active Set Lifecycle](#5-active-set-lifecycle)
6. [Reload Lifecycle](#6-reload-lifecycle)
7. [Mutation Authority（变更权限模型）](#7-mutation-authority变更权限模型)
8. [Failure Handling](#8-failure-handling)
9. [Recovery Semantics](#9-recovery-semantics)
10. [Scoring Formula](#10-scoring-formula)
11. [Global Instability Freeze](#11-global-instability-freeze)
12. [Data Structures](#12-data-structures)
13. [Module Contracts](#13-module-contracts)
14. [Telemetry](#14-telemetry)
15. [UI Ownership](#15-ui-ownership)
16. [Non-Goals](#16-non-goals)
17. [Known Runtime Limits](#17-known-runtime-limits)
18. [Invariant Checklist](#18-invariant-checklist)

---

## 0. Terminology（术语定义）

本节定义系统中的所有核心术语，消除跨实现、跨 review 的词义漂移。

| 术语 | 定义 | 作用域 |
|------|------|--------|
| **Active Set** | 当前参与 production traffic routing 的节点集合。MUST 仅包含 HealthState=Active 且 !IsInCooldown 的节点 | Control Plane |
| **Candidate Set** | 满足 Entry threshold 但尚未进入 Active Set 的节点集合 | Control Plane |
| **Sticky Set** | 已在 Active Set 中且 score ≥ ExitThreshold 的节点集合，受 hysteresis 保护 | Control Plane |
| **Cooldown** | 节点因连续失败被临时排除出 Active Set 的状态。节点 MUST 经历完整的 Recovery FSM 后才能重新进入 Active Set | Node State |
| **Recovery Probing** | Cooldown 到期后的第一阶段恢复。节点仅接收 probe traffic，MUST NOT 进入 production selector | Node State |
| **Stability Verification** | Recovery Probing 成功后的第二阶段验证。节点仅接收 probe traffic，MUST 持续稳定 N 分钟后才能进入 Active Set | Node State |
| **Freeze** | Global Instability Freeze。当 >60% Active Set 节点同时失败时触发，冻结所有 control-plane mutation 60s | Control Plane |
| **Reload** | 重新生成 xray config.json 并调用 LoadCore()。Reload = xray 进程重启 = 用户连接中断 | Data Plane |
| **Mutation** | 任何修改 Active Set membership 的操作（cooldown 进入/退出、recovery 推进、manual override） | Control Plane |
| **Mutation Authority** | 有权触发 Mutation 的实体及其优先级链。P0(EmergencyDisable) > P1(ManualOverride) > P2(Freeze) > P3(CooldownTransition) > P4(ActiveSetMutation) | Control Plane |
| **Effective Topology** | xray-core 运行时实际使用的 outbound selector 配置。由 Control Plane 通过 config.json 注入 | Data Plane |
| **Desired Topology** | Control Plane 根据当前 NodeState 计算出的目标 Active Set | Control Plane |
| **Debounce** | Reload 触发前的等待窗口。MUST 合并窗口内所有 Mutation，单次 Reload 应用最终状态 | Control Plane |
| **HealthState** | 节点的四阶段健康状态：Active / Failed / RecoveryProbing / StabilityVerification | Node State |
| **EWMA** | Exponentially Weighted Moving Average。使用 time-decayed α 计算延迟和丢包的平滑值 | Scoring |
| **Probe** | 通过 xray SOCKS5 入站发送 HTTP HEAD/GET 请求的主动探测。MUST NOT 进入 production traffic 路径 | Data Plane |
| **Bootstrap** | 冷启动阶段的并行 TCP connect 探活。在 xray 启动前执行，结果 MUST 覆盖历史分数 | Control Plane |
| **Hysteresis** | Active Set 进出使用不同门槛（Entry=60 / Exit=35）。MUST 防止 score 在阈值附近的振荡 | Control Plane |
| **Explorer** | 未进入 Active Set 但 score ≥ 35 的节点。MUST 仅接收 probe traffic，MUST NOT 进入 production selector | Control Plane |
| **Production Pool (Tier A)** | 当前承载 production traffic 的节点集合，进入 xray balancer selector。大小 clamp 在 MinProductionNodes~MaxProductionNodes 之间。MUST 仅因 failure/cooldown/HealthState 离开，MUST NOT 因 score 比较被替换 | Control Plane |
| **Standby Pool (Tier B)** | 健康（HealthState=Active, !IsInCooldown）但因 Production Pool 已满而未进入 production selector 的节点。持续 probe + EWMA + telemetry + FSM，MUST NOT 进入 production selector | Control Plane |
| **TrafficTier** | 节点在 traffic exposure 维度的分类：Production / Standby。与 HealthState 正交——HealthState=Active 的节点可以是 Standby | Node State |
| **Vacancy-Driven Promotion** | Standby → Production 晋升仅在 Production Pool 出现空缺时触发。空缺定义：ProductionCount < TargetProductionSize。MUST NOT 因 score 比较主动替换 Production 节点 | Control Plane |
| **TargetProductionSize** | Production Pool 的目标大小：`clamp(ceil(eligible × ActiveFraction), MinProductionNodes, MaxProductionNodes)`。默认 ActiveFraction=0.35, Min=3, Max=6 | Control Plane |
| **Measurement Asymmetry** | Production 节点 EWMA 反映真实用户流量负载，Standby 节点 EWMA 反映 probe-only 空载条件。两者不可直接比较。Standby score 仅用于 Standby 内部排序，MUST NOT 与 Production score 比对 | Scoring |

---

## 1. Stability Objective（稳定性目标）

### 1.1 优先级铁律

```
稳定性 > 响应性 > 最优性
```

任何 heuristic 的设计和参数选择 MUST 遵守此优先级。当稳定性与最优性冲突时，**稳定性 MUST win**。Reload MUST NOT 因追求更低 latency 或更优 score ranking 而触发。

### 1.2 核心哲学

```
系统默认假设：所有健康节点都"足够好"。
系统 MUST NOT 寻找"最快节点"。
系统 MUST 只淘汰明显坏节点。
```

### 1.3 约束所有 heuristic 的统一框架

| Heuristic | 稳定性角色 | 违反稳定性时如何退让 |
|-----------|-----------|-------------------|
| Hysteresis（Entry=60/Exit=35） | 25 分缓冲带防止 score 震荡触发 reload | 已在稳定侧，无需退让 |
| Bounded Production Pool（3~6 节点） | 压缩 production surface area，降低 long-flow variance | 故障驱逐后 vacancy-driven promotion 自动补位 |
| Explorer/Standby 隔离 | 仅通过 probe traffic 验证，不入 production selector | 彻底消除非 production 节点触发 reload 的路径 |
| Debounce（30s baseline） | 合并短时间内多次变更 | 超预算时自动延长至 60~120s |
| Reload Budget | 直接限制每小时 reload 次数 | Soft budget：超预算后 throttle 而非 deny |
| Cooldown Jitter（FNV-1a hash） | 防止群体同步恢复 | 确定性偏移，每个节点恢复时间点永不相同 |
| Throughput Anomaly | 检测高分低吞吐 | 仅 observer，不入 score，不入 active-set gate |
| Vacancy-Driven Promotion | 仅在 Production 出现空缺时晋升，不做 score 比较替换 | 消除 score-driven replacement 的 oscillation 风险 |
| Adaptive Exit（v1.3） | `max(25, min(35, median-15))` — 环境自适应 failure sensitivity | 降低整体退化时的误淘汰 |
| Fallback Repair（v1.3） | 空缺修复门槛从 35→48，仅 degraded mode 启用 | 防止"一进就出" |
| MinTenure 3 档（v1.3） | 新晋升节点 tenure 基于 runningScore 分 3 档（30s/120s/300s） | 时间维度防抖，runningScore 可信度越高越粘 |
| ReloadCooldown 60s（v1.3） | 全局 mutation rate limiter | 硬兜底，保证 reload 频率上限 |

### 1.4 统一目标函数

```
MUST 最小化不必要的连接中断，同时渐进排除持续降级节点。
```

- **MUST NOT 追求最优**：MUST NOT 尝试找 latency 最低的节点
- **MUST NOT 追求最大 exposure**：Production Pool MUST 保持 bounded（3-6 节点）。健康节点可以留在 Standby
- **MUST 只追求"不明显坏"**：MUST 淘汰那些持续、明显故障的节点

### 1.5 成功标准

```
用户极少需要手动干预（核心指标）
晚高峰不明显恶化
长连接不频繁断开（WebSocket / Telegram / SSH）
control plane 不 oscillation（active-set churn 低）
reload 频率低（< 4 次/小时正常环境）
```

**明确不是成功标准**：routing precision、latency optimality、throughput maximization、media quality scores、AI scheduling intelligence。

---

## 2. Runtime Constraints（运行时约束）

### 2.1 xray 缺失的 Runtime Capability

任何 proxy control-plane 系统的能力上限由 data-plane runtime 的 API surface 决定：

| xray 缺失的 runtime capability | 对 control-plane 的直接影响 |
|------|------|
| 无 per-outbound weighted routing | selector dedup 导致 tag duplication 加权无效，score 无法映射 traffic ratio |
| 无动态 balancer API | active-set 变更只能通过 config reload，reload = 连接中断 |
| 无 per-connection RTT | 只能通过应用层 probe 间接测量 latency |
| 无 per-connection 状态暴露 | 无法知道哪个连接正在用哪个 outbound |
| 无 QUIC connection migration | QUIC 节点需独立池处理 |
| config reload 必然中断已有连接 | reload 频率受 budget 严格控制 |
| Stats API 只暴露累计字节数 | 无法获取 per-connection throughput |

### 2.2 核心结论

当前系统的天花板不是 heuristic 不够聪明，而是 **xray runtime 的 API surface 几乎不存在**。C# control-plane 能在这些约束下实现"坏节点自动消失 + 好节点自动恢复 + 无震荡"已经是当前架构的上界。

### 2.3 Config-Level vs Runtime-Level State

```
cooldown 本质上应该是 "routing eligibility change"（运行时资格变更）
但因为 xray 没有 runtime API，被迫用 "topology rebuild"（拓扑重建）来模拟
→ 所有 eligibility change 都变成了 config rewrite + restart
```

cooldown node 不应该是"真实配置删除"，而应该是 runtime logical exclusion。但 xray 当前做不到 runtime selector mutation，只能 config-level emulate runtime。**reload unavoidable，但 reload frequency 可控。**

### 2.4 探测约束

**可测量**：reachability、probe latency (TTFB)、connection failure、basic transport health

**无法测量**：sustained media QoS、congestion quality、buffering frequency、HTTP/2 multiplexing stall、QUIC pacing quality、单连接 RTT

**Probe 的正确使用边界**：
- 用于：failure detection、reachability estimation、basic latency comparison
- 禁止用于：QoS optimization、media quality inference、throughput prediction、"best node" selection

**复合路径归因困境**：`probe latency = f(client → local ISP → proxy node → remote ISP → CDN edge)`。系统无法区分节点拥塞 vs 上游 ISP QoS throttling vs CDN edge routing 变化 vs 用户本地网络抖动。

### 2.5 Throughput 信号的因果性错乱

```
用户行为 → throughput（不是 node quality → throughput）
```

吞吐量测的是"用户在做什么"，不是"节点有多好"。

**禁止**：直接进入 HealthScore、直接影响 cooldown 决策、直接影响 active-set membership
**仅允许**：作为组合异常检测的辅助条件 + telemetry 和 debugging hint

---

## 3. Control Plane vs Data Plane

### 3.1 架构层次

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
└──────────────────────────────────────────────────────┘
```

### 3.2 架构规则

```
v2rayN 禁止接管 socket
禁止本地 SOCKS 中间层
禁止 request-level dispatch
禁止 fake weighted hack
v2rayN = control plane
xray-core = data plane
```

### 3.3 调度粒度

连接级（TCP connection），不做请求级切换。

### 3.4 Active-Set 内流量分配

xray random balancer 的 uniform random。**无法实现**：true weighted warmup、low-weight recovery routing、runtime probability shaping。Warmup 节点只能接收 probe traffic，必须在确认稳定后才能加入 production selector。

---

## 4. State Machine（状态机）

### 4.1 节点健康状态机

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
           selector,           指数退避                                │
           仅接收probe          上限30min)                               │
           traffic)                                                  │
                  │                                                   │
                  │ 正常probe持续稳定 N分钟                             │
                  │ (默认5min，可配置)                                  │
                  ▼                                                   │
             [ACTIVE] ──────────────────────────────────────────────┘
```

### 4.2 合法迁移

```
ACTIVE → FAILED              : consecutiveFailures >= 2 AND cooldown 未达上限
FAILED → RECOVERY_PROBING    : cooldown 到期
RECOVERY_PROBING → STABILITY_VERIFICATION : 连续 3 次 probe 成功
RECOVERY_PROBING → FAILED    : 任意一次 probe 失败 (指数退避延长 cooldown)
STABILITY_VERIFICATION → ACTIVE : 持续 N 分钟 probe 正常 (默认 5min)
STABILITY_VERIFICATION → FAILED : probe 失败 (退回 FAILED, 重置验证进度)
```

### 4.3 非法迁移（代码必须阻止）

```
禁止: FAILED → ACTIVE (跳过 RECOVERY_PROBING + STABILITY_VERIFICATION)
禁止: RECOVERY_PROBING → ACTIVE (跳过 STABILITY_VERIFICATION)
禁止: STABILITY_VERIFICATION → FAILED (仅 probe 失败, 必须走 RECOVERY_PROBING)
禁止: ACTIVE → RECOVERY_PROBING (cooldown 到期后才是 RECOVERY_PROBING)
禁止: 任何状态 → ACTIVE (除非从 STABILITY_VERIFICATION 且 verification_timer 到期)
```

### 4.4 状态持久化

Recovery Confirmation FSM 的状态必须持久化到 `ProfileExItem`。进程重启后状态不能丢失。

### 4.5 Active-Set Eligibility 规则

**只有 `HealthState == Active` 且 `!IsInCooldown` 的节点才可进入 production selector。**
RECOVERY_PROBING 和 STABILITY_VERIFICATION 节点仅接收 probe traffic，不入 production selector。

---

## 5. Active Set Lifecycle

### 5.1 Admission & Exit 门槛

#### 5.1.1 Normal Admission（正常晋升）

```
EntryThreshold = 60
语义: "优秀准入"——节点足够优秀，值得进入 production
触发: Standby score ≥ 60，且存在 vacancy（vacancy-driven only）
```

#### 5.1.2 Fallback Repair（空缺修复）

```
FallbackPromotionThreshold = 48
语义: "够用修复"——Production pool 已损坏（有 vacancy 但无 score≥60 的 Standby），
      允许"够用"而非"优秀"。仅用于 topology repair，不是 normal admission。
触发: 有 vacancy 但无 score ≥ 60 的 Standby 可用
```

> 为什么是 48 而不是 60？Vacancy repair 属于 degraded mode。48 提供 13 分缓冲（高于 Exit=35），防止"一进就出"。
> FallbackPromotionThreshold 不改变节点正常 admission 资格，仅用于 vacancy repair。

#### 5.1.3 Adaptive Exit（环境自适应淘汰）

```
EffectiveExitThreshold = max(FloorExit, min(ConfiguredExit, ProductionMedian - DynamicMargin))
参数: ConfiguredExit = 35, DynamicMargin = 15, FloorExit = 25
语义: Environment-adaptive failure sensitivity——仍然是 absolute failure semantics，
      不是 relative competition。"节点自己是否 failure"，不是"节点是否比别的节点差"。
```

| Production Scores | Median | EffectiveExit | 含义 |
|-------------------|--------|---------------|------|
| 95,92,88,86,84,80 | 87 | max(25, min(35, 72)) = **35** | 高质量环境，正常 sensitivity |
| 55,52,50,48,46,44 | 49 | max(25, min(35, 34)) = **34** | 整体退化，自动降低 sensitivity |
| 30,28,26,25,24,22 | 25.5 | max(25, min(35, 10.5)) = **25** | 极差环境，触 floor 保护 |

> Floor=25 是 absolute minimum survivability——防止极差环境下 Exit 塌到 15 导致永不 eject。

#### 5.1.4 MinTenure（时间门槛）

离散 3 档（非连续函数），依据节点在 Production 中的 runningScore（真实流量 EWMA，非探针分数）：

| runningScore | MinTenure | 语义 |
|-------------|-----------|------|
| >= 55 | 300s (5min) | 稳定节点，高粘性 |
| >= 40 | 120s (2min) | 中等节点，给时间证明 |
| < 40 | 30s | 边缘节点，允许快速纠错 |

> 为什么不用连续函数？系统已有 EWMA、score、hysteresis、debounce、freeze、cooldown、recovery FSM 等多层动态系统。Tenure 必须保持低维、离散、可解释。
> 为什么用 runningScore 而非 promotionScore？探针分数是小样本测试请求，运行分数是真实代理流量——可信度不在一个量级。

#### 5.1.5 ReloadCooldown（频率硬地板）

```
ReloadCooldown = 60s
语义: Global mutation rate limiter。不管什么原因，两次 reload 至少隔 60 秒。
      xray reload = 用户连接中断，是当前系统最大的真实 UX 成本。
```

#### 5.1.6 机制关系：AND 串联门

```
Reload 执行 =
    Production Set 实际发生了变化
    AND ReloadCooldown 已过期 (60s)

节点降级 =
    score < EffectiveExitThreshold
    AND MinTenure 已过期

Vacancy 填补 =
    选 Standby 中 score >= FallbackPromotionThreshold(48) 的节点按分数降序补位
```

所有机制为 AND 关系，每一层只控制一个维度，不互相争夺 topology authority。

### 5.2 多阶段评估

1. **Sticky 保护**：已在 Production 中的节点，只要 score >= EffectiveExitThreshold 且 MinTenure 未过期就保留
2. **Normal Admission**：不在 Production 中的节点，需 score ≥ EntryThreshold(60) 才能进入 candidates
3. **Fallback Repair**：若 candidates 不足填补 vacancy，允许 score ≥ FallbackPromotionThreshold(48) 的 Standby 临时补位
4. **Vacancy 填充**：sticky 节点优先（按 score 降序），剩余空位用 candidates 填充，再不足用 fallback 填充
5. **安全底线**：若 sticky + candidates + fallback 均为空，回退到全节点按 score 降序选 top-K
6. **ReloadCooldown 检查**：Production Set 变化后，检查距离上次 reload 是否 ≥ 60s，否则推迟

### 5.3 TargetProductionSize 公式

```
TargetProductionSize = clamp(ceil(eligible_count × ActiveFraction), MinProductionNodes, MaxProductionNodes)
```

默认参数：`ActiveFraction = 0.35`, `MinProductionNodes = 3`, `MaxProductionNodes = 6`。

Production Pool 是 **bounded elastic** 而非固定大小——节点少时全入池，节点多时封顶：

| eligible_count | TargetProductionSize | 说明 |
|---------------|---------------------|------|
| 1–2 | eligible_count | 小池全入 production |
| 3 | 3 | 踩到 Min 下限 |
| 5 | 3 | ceil(5×0.35)=2 → clamp→3 |
| 10 | 4 | ceil(10×0.35)=4 |
| 15 | 6 | ceil(15×0.35)=6 → clamp→6 |
| 20 | 6 | ceil(20×0.35)=7 → clamp→6 |
| 30 | 6 | ceil(30×0.35)=11 → clamp→6 |

> 旧公式 `K = max(2, ceil(N × 2/3))` 在 20 节点下得到 14，production surface 过大。新公式将 production 严格控制在 3-6 节点，符合"压缩 production surface area"的 P2 目标。

### 5.4 Active-Set Eligibility（强制性规则）

**MUST**：只有同时满足以下**三个**条件的节点才能进入 production selector：
1. `IsInCooldown == false`
2. `HealthState == Active`
3. `TrafficTier == Production`

**MUST NOT**：RECOVERY_PROBING 和 STABILITY_VERIFICATION 节点进入 production selector。这些节点 MUST 仅接收 probe traffic。

**MUST NOT**：Standby 节点（HealthState=Active, TrafficTier=Standby）进入 production selector。Standby score MUST NOT 与 Production score 直接比较来决定替换。

### 5.5 Explorer 隔离（P2: 已纳入 Standby Pool）

Explorer 是 Standby Pool 的子集：score 在 [48, 60) 的 Standby 节点。

Explorer MUST 仅接收 probe traffic + telemetry + passive evaluation。
Explorer MUST NOT 进入 production selector（正常晋升）。
Explorer 可在 Fallback Repair（§5.1.2）时临时补位——vacancy 存在且无 score≥60 的 Standby。
Explorer MUST 稳定超过 EntryThreshold(60) 后才能通过 Normal Admission 晋升。
Explorer 语义完全保留于 Standby Pool 框架内。

### 5.6 启动序列

```
T=0ms     InitializeNodes() — 构建 NodeState, 分配探活 SOCKS5 端口
T=0ms     BootstrapAsync() — RestorePersistedScoresAsync → BootstrapProber 并行 TCP 探活
T≤3000ms  Bootstrap 完成，返回初始 AdaptiveConfig（含 probe inbounds）
T=3001ms  LoadCore — 首次 xray 配置加载（含探活入站）
T≈4~5s    xray SOCKS5 ready（重启实测 ~1.1s）
           StartProbesAsync() — ProbeService + ScoreLogger + MonitorActiveSet 启动
T+15~30s  EWMA 逐步替代 Bootstrap 初始值
```

### 5.7 Bounded Production Pool（P2 新增）

#### 5.7.1 设计动机

当前 topK = ceil(N × 2/3) 在 20+ 节点环境下导致 active-set ≈ 14，所有 14 节点在 xray uniform random selector 下等概率承接 YouTube/Telegram/QUIC long-lived flows。Dataplane 的 primitive random dispersion 意味着 **active-set 越大 → mediocre node 被命中概率越高 → long-flow variance 越大**。

Control-plane 已经非常 conservative（hysteresis, cooldown, recovery FSM, freeze, debounce），但 dataplane 仍然是 uniform random。**Production pool 必须承担 quality concentration 职责——限制 exposure，而非优化 routing precision。**

#### 5.7.2 Two-Tier Architecture

```
eligible nodes (HealthState=Active, !IsInCooldown)
    │
    ├── Tier A: Production Pool (3~6 nodes)
    │     • 进入 xray balancer selector
    │     • 承载真实 production traffic（uniform random）
    │     • MUST 仅因 failure/cooldown/HealthState!=Active 离开
    │     • MUST NOT 因 Standby 有更高 score 而被替换
    │
    └── Tier B: Standby Pool (remaining eligible nodes)
          • 持续 probe + EWMA + telemetry + FSM
          • MUST NOT 进入 production selector
          • 仅在 Production 出现 vacancy 时晋升
          • score 仅用于 Standby 内部排序，不与 Production score 比较
```

#### 5.7.3 Production Pool Membership

**进入（Promotion）**：仅当 `ProductionCount < TargetProductionSize`（出现 vacancy）时触发。

1. **Normal Admission**：从 Standby 中选择 `HealthState == Active && Score >= EntryThreshold(60)` 的节点，按 score 降序补位。
2. **Fallback Repair**：若无 score≥60 的 Standby，放宽至 `Score >= FallbackPromotionThreshold(48)` 临时补位。语义为 degraded mode repair——Production pool 已损坏，允许"够用"而非"优秀"。
3. 一次 reload 可晋升多个 Standby（合并 debounce 窗口内的多个 vacancy）。

**离开（Demotion）**：仅以下条件触发，**所有条件为 AND 关系**：

- `Score < EffectiveExitThreshold`（见 §5.1.3）——环境自适应 failure 判断
- `MinTenure 已过期`（见 §5.1.4）——时间门槛，防止新晋升节点立即被淘汰
- `进入 cooldown`（consecutiveFailures ≥ 2）
- `HealthState != Active`（进入 Recovery FSM）
- `Score < ExitThreshold(35)` 且不在 sticky 保护期
- 进入 cooldown（consecutiveFailures ≥ 2）
- `HealthState != Active`（进入 Recovery FSM）

**MUST NOT**：因 Standby 节点 score 更高而替换 Production 节点（禁止 score-driven replacement）。

#### 5.7.4 Standby Pool

Standby 节点 = `HealthState == Active && !IsInCooldown` 但不属于 Production Pool 的节点。

Standby 行为：
- 持续接收 probe traffic（ProbeService 探测所有非 cooldown 节点）
- 持续 EWMA 更新 + telemetry（FailureCollector 正常运作）
- 持续 cooldown FSM / recovery FSM（完整状态机保护）
- **score 仅用于 Standby 内部排序**——决定 vacancy 出现时谁先晋升

**Explorer 子集**：Standby 中 score 在 [48, 60) 的节点仍为 Explorer——有资格留在 Standby 但不满足正常晋升门槛（可在 Fallback Repair 时补位）。Explorer 语义完全保留。

#### 5.7.5 TrafficTier — 与 HealthState 正交

```csharp
public enum TrafficTier { Production, Standby }
```

| HealthState | TrafficTier | 含义 |
|-------------|-------------|------|
| Active | Production | 健康，正在承载 production traffic |
| Active | Standby | 健康，但因 pool 已满暂不承载 production traffic |
| RecoveryProbing | Standby | 恢复中探测，不入 production |
| StabilityVerification | Standby | 恢复中验证，不入 production |
| Failed (cooldown) | Standby | 冷却中，不入 production |

TrafficTier **只影响 selector membership**，不影响 cooldown FSM、recovery FSM、score calculation、probe execution。

**关键约束**：`TrafficTier = Production` ⇒ `HealthState = Active && !IsInCooldown`（逆命题不成立——Active 节点可以是 Standby）。

#### 5.7.6 Vacancy 定义与 Debounce 合并

**Vacancy**：`ProductionCount < TargetProductionSize`。

多个节点在 debounce 窗口（30s baseline）内先后离开 Production → 多个 vacancy 被合并为一次 reload，一次性晋升多个 Standby 补位。现有 `ReloadPolicyApplier` 的 trailing debounce 天然支持此行为。

**Catastrophic bypass**：若 Production Pool 因大规模故障而清空（ProductionCount = 0），走 `ApplyImmediateAsync` 立即晋升，不等待 debounce。

#### 5.7.7 Standby 不足时的 Fallback

当 `eligible Standby (score ≥ 60) < vacancy count` 时，放宽晋升条件：
1. 优先晋升 score ≥ 60 的 Standby（Normal Admission）
2. 仍不足时，允许 score ≥ FallbackPromotionThreshold(48) 的 Standby 临时补位（Fallback Repair，degraded mode）
3. Fallback 仅用于 topology repair，不改变节点正常 admission 资格
4. 极端情况（全 eligible 为空）沿用现有 fallback：RECOVERY_PROBING > STABILITY_VERIFICATION > cooldown

**Invariant**：Production Pool 永不为空（延续 Invariant I1）。

#### 5.7.8 TrafficTier 持久化

TrafficTier MUST 持久化到 `ProfileExItem`。进程重启后 MUST 恢复，防止所有 Standby 误入 Production 导致 pool 膨胀。持久化逻辑与 Recovery FSM 状态持久化（§4.4）一致。

#### 5.7.9 配置参数

| 参数 | 默认值 | 范围 | 说明 |
|------|--------|------|------|
| `ActiveFraction` | 0.35 | 0.25 – 0.50 | eligible 节点中进入 production 的比例 |
| `MinProductionNodes` | 3 | 2 – 4 | Production pool 最小节点数 |
| `MaxProductionNodes` | 6 | 4 – 8 | Production pool 最大节点数 |

配置位置：PolicyGroup → Adaptive Settings，per-group 级别（与现有 AdaptiveEnabled 同级）。

---

## 6. Reload Lifecycle

### 6.1 核心约束

xray 不支持动态 outbound 注入。任何 Active Set membership 变更 = config 重新生成 + `LoadCore()` = xray 进程重启 = 用户连接中断。

**ReloadPolicyApplier MUST enforce minimum reload interval.**
**Reload frequency MUST remain bounded（正常环境 < 4 次/小时）。**

### 6.2 Debounce 策略

**MUST use trailing debounce**：变更到达时若在 reload 预算窗口内，MUST 保存最新配置，窗口到期后 MUST 应用最终配置。MUST NOT 丢失更新。

### 6.3 自适应 Debounce Interval

滑动 1 小时窗口内统计 reload 次数：

| Reload 次数/小时 | Debounce Interval | 状态 |
|-----------------|-------------------|------|
| ≤ 6 次 | 30s | 正常 |
| 7–10 次 | 60s | 扩展 |
| > 10 次 | 120s | 节流 |

Soft throttle，不硬拒绝。debounce 上限 120s 远小于 cooldown 最小退避（30s），真坏节点仍能通过 cooldown 机制被驱逐。

### 6.4 Debounce Severity 分级

| 变更类型 | Debounce 行为 | 规则 |
|---------|-------------|------|
| 普通 cooldown（单个节点进/出） | 走标准 debounce window（30s baseline） | MUST debounce |
| 全 eligible 为空（所有可用节点消失） | 立即执行 | MUST NOT debounce |
| Freeze 期间 | cooldown 入队但不 apply | MUST defer until unfreeze |

### 6.5 ConfigGeneration（单调版本号）

```
MUST: 每次 Active Set 变更 → ConfigGeneration.Next()
MUST: Manual Override → ConfigGeneration.Next()
MUST: Reload 执行前检查 generation。过期则丢弃此 reload task
```

### 6.6 Reload 执行前置检查

Reload 执行前 MUST 检查以下三项全部通过：

```
1. ConfigGeneration.Current == capturedGeneration
2. FreezeController.State != FREEZE_ACTIVE
3. ManualOverride.IsActive == false
```

**任一失败 MUST 丢弃此 reload task。**

---

## 7. Mutation Authority（变更权限模型）

### 7.1 事件优先级

所有控制面事件按以下优先级执行。**高优先级事件可以 cancel 低优先级 pending action**。

| 优先级 | 事件 | Cancel 范围 |
|--------|------|------------|
| **P0** | EmergencyDisable | Cancel 所有 P1-P5 pending actions + 停止 ControlPlaneLoop |
| **P1** | ManualOverride | Cancel 所有 P3-P5 pending actions（Freeze 仍保持监控但不执行 reload） |
| **P2** | GlobalFreeze | Cancel 所有 P4-P5 pending reload tasks。Probe 继续。Telemetry 继续。 |
| **P3** | CooldownTransition | 触发 P4 active-set 重算。不直接 cancel reload。 |
| **P4** | ActiveSetMutation | 创建 reload task（受 debounce budget 约束）。可被 P0/P1/P2 cancel。 |
| **P5** | Telemetry | 纯观测，不触发任何 state mutation。不被 cancel。 |

### 7.2 事件冲突解决规则

**规则 1 — 优先级抢占**：高优先级事件到达时，立即中断当前低优先级 handler，cancel 低优先级 pending tasks，执行高优先级 handler。

**规则 2 — 同优先级 FIFO**：同优先级事件按到达顺序处理。例外：两个 ActiveSetMutation 之间时间 < debounce window，第二个覆盖第一个。

**规则 3 — Freeze 期间的 CooldownTransition**：Freeze 期间 cooldown 照常评估，但 cooldown 变化不触发 ActiveSetMutation（freeze 禁止 active-set 变更）。Freeze 解除后，用最新 FSM 状态一次性重算 active-set。

**规则 4 — ManualOverride 与 Freeze 的关系**：ManualOverride 优先级高于 Freeze。ManualOverride 在 Freeze 期间触发 → 立即解除 Freeze → 执行 ManualOverride。

**规则 5 — Reload 执行前 generation check**：见 §6.6。

### 7.3 Freeze Gate（v7.3 新增）

Freeze 期间 `FailureCollector.RecordFailure` 仅更新 EWMA（观测继续），不递增 `consecutiveFailures`，不调用 `CooldownFsm.TryEnterCooldown`。观测继续，状态迁移冻结。

### 7.4 Control-Plane Loop（单线程状态所有者）

所有 state mutation 最终串行提交到 ControlPlaneLoop：

```
ProbeService (并发探活)
    │  结果通过 channel 提交
    ▼
ControlPlaneLoop (单线程消费)
    │  1. UpdateScore()          — NodeState 变更
    │  2. CooldownFsm.Evaluate() — FSM 状态迁移
    │  3. ActiveSetManager       — 重算 active set
    │  4. FreezeController       — 评估冻结条件
    │  5. ReloadPolicyApplier    — 调度 reload
```

---

## 8. Failure Handling

### 8.1 FailureType 枚举

```csharp
public enum FailureType { None, Timeout, Refused, TlsError, NetworkError, UnexpectedEof, DnsResolutionFailure, DnsPoisoningSuspected }
```

### 8.2 差异化惩罚

| FailureType | penaltyLoss | penaltyLatencyMs | 原理 |
|-------------|-------------|------------------|------|
| Refused | 1.0 | 10,000 | 端口不通，几乎肯定是节点问题 |
| Timeout | 0.8 | 10,000 | 可能 GFW 干扰，也可能是节点慢 |
| NetworkError | 0.7 | 10,000 | 通用网络错误 |
| UnexpectedEof | 0.4 | EwmaLatencyMs × 1.5 | 连接中途断开，弱惩罚 |
| TlsError | 0.0 | EwmaLatencyMs（不变） | TLS 配置错误，与网络质量无关 — no penalty, no cooldown |
| DnsResolutionFailure | 0.0 | EwmaLatencyMs（不变） | DNS 故障域独立 — no penalty, no cooldown |
| DnsPoisoningSuspected | 0.0 | EwmaLatencyMs（不变） | DNS 归因分离 — no penalty, no cooldown |

### 8.3 Cooldown 规则

**触发条件**：`consecutiveFailures >= 2` AND 当前 cooldown 节点数 < max allowed

**Cooldown 上限**：

| 节点数 | 最大 cooldown 数 |
|--------|-----------------|
| 1 | 0（永不冷却唯一节点） |
| 2 | 1 |
| 3+ | max(1, floor(N/3)) |

**退避时长**：

| 连续失败次数 | cooldown 时长（含 hash jitter [0, 14]s） |
|-------------|--------------------------------------|
| 2 | 30s + [0, 14]s |
| 3 | 60s + [0, 14]s |
| 4 | 120s + [0, 14]s |
| 5 | 240s + [0, 14]s |
| 6+ | 300s（封顶） |

**Cooldown Jitter**：FNV-1a hash-based stable offset（确定性，跨重启稳定，防 recovery burst）。

### 8.4 全节点 Cooldown 兜底

当所有节点都进入 cooldown 时，优先选 RECOVERY_PROBING 中 probe 成功次数最多的节点，其次选 cooldown 剩余时间最短的节点。balancer selector 永不为空。

---

## 9. Recovery Semantics

### 9.1 Recovery Confirmation FSM（四阶段恢复）

**MUST 经历完整的四阶段恢复后才能重新进入 Active Set：**

```
FAILED (cooldown)
  → cooldown 到期
  → RECOVERY_PROBING（MUST 仅 probe traffic, MUST NOT 进入 production selector）
    → MUST 连续 3 次 probe 成功
    → STABILITY_VERIFICATION（MUST 仅 probe traffic, MUST NOT 进入 production selector）
      → MUST 持续 N 分钟 probe 正常（默认 5min，可配置）
      → ACTIVE（允许进入 production selector）
```

**Recovery FSM MUST enforce：**
- 任意阶段 probe 失败 → MUST 重回 FAILED
- 重回 FAILED MUST 使用指数退避延长 cooldown
- 指数退避 MUST cap at 30 分钟（MUST NOT 永久冷冻节点）
- MUST NOT 跳过任何阶段（含用户手动清除 cooldown）

---

## 10. Scoring Formula

### 10.1 Time-Decayed EWMA

```
α(Δt) = 0.05 + 0.25 × e^(−Δt / 60)
```

| 观测间隔 Δt | α 值 | 含义 |
|------------|------|------|
| 0s（刚观测）| 0.30 | 新数据权重 30% |
| 10s | 0.25 | 新数据权重 25% |
| 60s | 0.14 | 新数据权重 14% |
| 5min | 0.06 | 新数据权重 6% |
| 30min+ | 0.05 | 最小值 |

### 10.2 评分公式

```csharp
latNorm  = min(ewmaLatencyMs / 2000, 1.0)
lossNorm = clamp(ewmaLossRate, 0.0, 1.0)
raw      = 1.0 - (latNorm × 0.55 + lossNorm × 0.45)
raw      = max(raw, 0.0)
score    = raw² × 100
score    = max(score, 1.0)
```

- 延迟参考上限：2000ms
- 延迟权重：0.55，丢包权重：0.45
- 平方放大：线性映射下好坏节点区分度不足，平方后差距显著
- 下界：1.0（始终有分数，保底）

### 10.3 Score 作用域

- active-set membership（通过 hysteresis 门槛）
- cooldown decision
- recovery ordering

**不再映射 routing probability**（active-set 内 uniform random）。

---

## 11. Global Instability Freeze

### 11.1 触发条件

```
> 60% active-set 内节点在 15s 窗口内同时发生连续失败
```

### 11.2 冻结行为（持续 60s，可配置）

Freeze 期间 MUST：

```
1. Freeze active-set mutation    — MUST NOT 驱逐节点，MUST NOT 添加节点
2. Freeze cooldown ejection      — MUST NOT 触发新的 cooldown（FailureCollector freeze gate）
3. Suspend reload scheduling     — MUST NOT 调度任何 xray config reload
4. Keep last known stable selector — MUST 维持冻结前最后一组 active-set
```

Freeze 期间 MUST 继续：probe 运行、ScoreLogger 记录、telemetry 标记 `global_freeze` 事件。

### 11.3 解除条件

冻结持续时间到达（默认 60s）后 MUST 自动解除。解除后 MUST：
1. 以解除时的最新分数重新计算 active-set（MUST NOT 回溯冻结前状态）
2. 恢复正常 cooldown 评估
3. Reload Budget 从头计数

### 11.4 Freeze Hysteresis（防 freeze oscillation）

Freeze 解除后进入 120s freeze_cooldown。期间禁止再次触发 freeze。若再次出现大规模异常 → escalate 到 EmergencyDisable。

### 11.5 配置参数

| 参数 | 建议默认值 | 范围 |
|------|-----------|------|
| FreezeTriggerRatio | 0.60 (60%) | 0.30 – 0.80 |
| FreezeTriggerWindowSeconds | 15s | 5s – 30s |
| FreezeDurationSeconds | 60s | 30s – 120s |
| FreezeCooldownSeconds | 120s | 60s – 300s |

---

## 12. Data Structures

### 12.1 NodeState

```csharp
public enum ProxyProtocol { Tcp, Udp }
public enum NodeHealthState { Active = 0, Failed = 1, RecoveryProbing = 2, StabilityVerification = 3 }
public enum TrafficTier { Production, Standby }

public sealed class NodeState
{
    // ── identity（只读）
    public string Tag          { get; init; }
    public string Host         { get; init; }
    public int    Port         { get; init; }
    public ProxyProtocol Protocol { get; init; }
    public string ChildIndexId { get; init; }

    // ── traffic tier（P2: 与 HealthState 正交）
    private TrafficTier _trafficTier = TrafficTier.Standby;

    // ── scoring state（受 _lock 保护）
    private double _score         = 50.0;  // [1.0, 100.0]
    private double _ewmaLatencyMs = 500.0;
    private double _ewmaLossRate  = 0.10;
    private DateTime _lastObserved = DateTime.MinValue;
    private int _consecutiveFailures;
    private DateTime _cooldownUntil = DateTime.MinValue;

    // ── recovery FSM state
    private NodeHealthState _healthState = NodeHealthState.Active;

    // ── DNS cache
    private string? _cachedIp;
    private DateTime _dnsLastResolved;
    private int _dnsCacheConfidence;
    private int _dnsConsecutiveCacheFailures;

    // 属性
    public double Score => _score;
    public bool IsInCooldown => DateTime.UtcNow < _cooldownUntil;
    public NodeHealthState HealthState => _healthState;
    public TrafficTier TrafficTier => _trafficTier;

    // 方法
    public void UpdateScore(double latencyMs, double lossRate, double score, int consecutiveFailures);
    public void SetCooldown(DateTime until);
    public void SetTrafficTier(TrafficTier tier);
    public NodeSnapshot Snapshot();
}
```

### 12.2 AdaptiveConfig

```csharp
public record AdaptiveConfig
{
    public required List<string> ActiveTags { get; init; }
    public required List<string> CooldownTags { get; init; }
    public required IReadOnlyDictionary<string, int> ProbePorts { get; init; }
    public IReadOnlyDictionary<string, double> NodeScores { get; init; }
    public IReadOnlyDictionary<string, string> TagToIndexId { get; init; }
    public long Generation { get; init; }
}
```

### 12.3 ProtocolExtraItem（Adaptive 相关字段）

```csharp
public record ProtocolExtraItem
{
    // ... 其他字段 ...
    public bool? AdaptiveEnabled { get; init; }
    public string? AdaptiveProbeUrl { get; init; }
    public int? AdaptiveProbeIntervalSec { get; init; }
    public int? AdaptiveProbeTimeoutMs { get; init; }
    public double? AdaptiveProbeHeavyFraction { get; init; }
}
```

---

## 13. Module Contracts

### 13.1 模块清单

| 模块 | 文件 | 职责 |
|------|------|------|
| `AdaptiveSchedulerManager` | `AdaptiveSchedulerManager.cs` | 控制面编排器：初始化、启动/停止探活、MonitorActiveSet 循环、紧急旁路 |
| `ScoreCalculator` | `ScoreCalculator.cs` | EWMA 评分：延迟参考上限 2000ms，延迟权重 0.55，丢包权重 0.45，平方放大。**Throughput 禁止进入 Score** |
| `FailureCollector` | `FailureCollector.cs` | 失败事件收集：差异化惩罚 + Freeze gate（freeze 期间仅更新 EWMA，阻止 cooldown） |
| `CooldownFsm` | `CooldownFsm.cs` | 冷却状态机：连续失败 ≥2 触发，FNV-1a hash stable jitter，全局上限 1/3 节点 |
| `ActiveSetManager` | `ActiveSetManager.cs` | Production/Standby tier 管理：TargetProductionSize 公式（clamp 3-6）+ hysteresis（Entry=60/Exit=35）+ **HealthState gate + TrafficTier gate** + vacancy-driven promotion + decision traceability |
| `BootstrapProber` | `BootstrapProber.cs` | 冷启动探活：并行 TCP connect + DNS 缓存解析，2s 超时，全局 3s 截止 |
| `ProbeService` | `ProbeService.cs` | 运行时探活：xray SOCKS5 HTTP 探活，多目标 URL + heavy GET probe |
| `ScoreLogger` | `ScoreLogger.cs` | JSONL telemetry：probe_result / ewma_update / score_snapshot / active_set_change / xray_reload |
| `ReloadPolicyApplier` | `ReloadPolicyApplier.cs` | 自适应 debounce：滑动 1h window（30s/60s/120s 三级）+ severity 分级 |
| `RecoveryConfirmationFsm` | `RecoveryConfirmationFsm.cs` | 四阶段恢复状态机：合法/非法迁移 guard，指数退避上限 30min |
| `GlobalFreezeController` | `GlobalFreezeController.cs` | 全局冻结：>60% active 节点失败 → 冻结 60s + 120s hysteresis |
| `DnsCacheManager` | `DnsCacheManager.cs` | DNS 缓存 confidence 生命周期：300s TTL，N=3 连续失败失效，lazy check-on-use |
| `IClock` | `IClock.cs` | 时间抽象接口 |
| `IAdaptivePolicyApplier` | `IAdaptivePolicyApplier.cs` | Policy applier 接口（ReloadPolicyApplier 实现 Phase 1 fallback） |

### 13.2 BootstrapProber Contract

- ALL code paths 调用 `node.UpdateScore()` — 无路径保留旧分数
- Bootstrap 始终覆盖历史分数（包括历史高分）
- 超时/失败 → score=1.0，不进 cooldown

### 13.3 ScoreCalculator Contract

- Floor=1.0
- LatencyRef=2000ms, LatencyWeight=0.55, LossWeight=0.45, Exponent=2.0
- Throughput 禁止进入评分

### 13.4 ActiveSetManager Contract

- `GetProductionTags()` 返回 Production Pool 节点 tag（Tier A，3-6 节点），进入 xray balancer selector
- `GetStandbyTags()` 返回 Standby Pool 节点 tag（Tier B，probe traffic only）
- eligible 条件：`!IsInCooldown && HealthState == Active`
- Production eligibility 额外要求：`TrafficTier == Production`
- Hysteresis: Entry=60, Exit=35（Production 离开与 Standby 晋升共用同一对门槛）
- TargetProductionSize = clamp(ceil(N × ActiveFraction), MinProductionNodes, MaxProductionNodes)，默认 (0.35, 3, 6)
- Promotion 仅 vacancy-driven：`ProductionCount < TargetProductionSize` 时从 Standby 按 score 降序补位
- **MUST NOT** 因 Standby score 高于 Production score 而主动替换（禁止 score-driven replacement）
- `HasActiveSetChanged()` 检测 Production Pool 成员变化（含 promotion/demotion）
- `Prime()` 在 bootstrap 后调用，防止首次 spurious reload
- `MarkDirty()` 在 freeze 解除后调用，强制重新评估

---

## 14. Telemetry

### 14.1 事件类型

| 事件 | 触发条件 | 关键字段 |
|------|---------|---------|
| `score_snapshot` | 每 30s | 所有节点 score, in_cooldown |
| `probe_result` | 探活成功/失败 | ttfb_ms 或 failure_type |
| `ewma_update` | EWMA 更新 | old/new latency/score, alpha |
| `active_set_change` | top-K 变化 | active_tags, cooldown_tags, scores, added, removed, **change_reasons** |
| `xray_reload` | xray 配置重载 | active_tags, trigger |
| `global_freeze` | 全局冻结触发 | reason, frozen_active_tags |
| `global_freeze_end` | 全局冻结解除 | freeze_duration_s, current_active_tags |
| `quality_metrics` | 每 5min | Shannon 熵, P95 延迟, 均值, 标准差 |

### 14.2 Decision Traceability

每个 `active_set_change` 必须包含 causal trace——每个 added/removed node 的原因（score_crossed_entry / score_below_exit / entered_cooldown / cooldown_cleared / score_ranking）。

### 14.3 保留策略

- Max file size: 50MB
- Retention: 7 days
- Rotation: 达到 50MB → rename 为 `adaptive.{yyyyMMdd}.log`
- Compression: gzip 归档
- Max total storage: 200MB

---

## 15. UI Ownership

### 15.1 分层原则

| 层级 | 归属 | 内容 |
|------|------|------|
| **Global 层** — Scheduler Engine | Settings > Adaptive | engine enable/disable, telemetry retention, log level, debug mode |
| **Per-Group 层** — Adaptive Policy | PolicyGroup 编辑对话框 | enable adaptive, probe URL/interval/timeout, cooldown params, recovery params, freeze thresholds, heavy probe fraction |

### 15.2 初始化条件

Adaptive scheduling 仅当 PolicyGroup 的 `ProtocolExtra.AdaptiveEnabled == true` 时激活。不再有全局 `Enabled` 二次检查。

### 15.3 Manual Override Lock

用户手动切换到指定节点 → 自动系统 relinquish routing authority，锁定 N 分钟（默认 5min）。锁定期间禁止 active-set 变更触发 reload，保留所有 probe + telemetry（数据收集继续，但不决策）。

### 15.4 EmergencyDisableAdaptive

```csharp
public async Task EmergencyDisableAdaptiveAsync()
{
    _adaptiveItem.Enabled = false;
    await StopAsync();
    // 调用者负责重生成并加载默认 xray 配置
}
```

---

## 16. Non-Goals

### 16.1 禁止实施

```
禁止 weighted routing hack（tag duplication 已被 xray 证伪）
禁止 duplicated outbounds / fake weighted selector / synthetic replicas
禁止 DPI / 流量识别（TLS + ECH 使 L7 信息趋近于零）
禁止请求级切换（同一 TCP 连接中途换节点导致协议状态机崩溃）
禁止全局最优计算（所有流量压单节点会压死该节点）
禁止高频主动测速（干扰正常流量，无法代表真实业务质量）
禁止复杂 ML/AI 模型（维护成本远超收益）
禁止 per-request balancing
禁止 runtime probability shaping（xray 无动态 balancer API）
禁止 transparent QUIC migration（Phase 3 才考虑）
禁止 cross-flow fairness optimization（系统不知道哪个连接是视频/WebSocket/SSH）
```

### 16.2 系统做不到

| 限制 | 原因 |
|------|------|
| 真正 weighted routing | xray selector dedup |
| true weighted warmup | active-set 内 uniform random |
| low-weight recovery routing | 无法渐进增加恢复节点权重 |
| 精确故障归因（DNS vs Node） | 仅完成 Step 1（缓存重试），Step 2（备用 DNS resolver）P2 实施 |

---

## 17. Known Runtime Limits

1. **HealthScore = Reachability Score，不是 UX Score**。不是 Media Quality Score。不是 Throughput Score。
2. **probe latency 是复合路径指标**（client → ISP → node → ISP → CDN edge），无法区分 node congestion vs ISP QoS vs CDN routing vs local WiFi
3. **Throughput 信号因果倒置**：用户行为 → throughput，不是 node quality → throughput
4. **Warmup 节点不能进入 production selector**：xray active-set 内 uniform random，warmup 节点会与健康节点等概率承接流量
5. **RuntimePolicyApplier 在 xray 下不可用**：xray 不支持运行时 outbound 变更
6. **Dual time-source**：新 FSM 模块注入 IClock（确定性），legacy 模块直接使用 DateTime.UtcNow（非确定性）。Full unification planned for P2
7. **当前仅 TCP 池**：UDP/QUIC 独立节点池未开始

---

## 18. Invariant Checklist

以下不变式是所有模块实现、测试编写、代码审查、运行时审计的强制性约束。

### I. Active Set Safety

| # | Invariant | 验证方式 |
|---|-----------|---------|
| I1 | **Active Set MUST NOT become empty** | 全节点 cooldown 时 MUST fallback 到 cooldown 剩余最短或 Recovery 进度最高的节点 |
| I2 | **Active Set MUST only contain nodes with HealthState=Active AND !IsInCooldown** | `ActiveSetManager.HasActiveSetChanged()` / `GetActiveTags()` MUST enforce |
| I3 | **Hysteresis MUST prevent oscillation** | Entry=60, Exit=35, 缓冲带 25 分。同一节点在同一次 monitor cycle 内 MUST NOT 同时进入和退出 |

### II. Freeze Integrity

| # | Invariant | 验证方式 |
|---|-----------|---------|
| II1 | **Freeze MUST block all automatic topology mutation** | `MonitorActiveSetAsync` MUST skip active-set check when `FreezeDecision.Type in {TriggerFreeze, BlockMutation}` |
| II2 | **Freeze MUST NOT block probe execution** | `ProbeService` MUST continue during freeze；`ScoreLogger` MUST continue |
| II3 | **Freeze MUST NOT block cooldown state accumulation in FailureCollector** | `FailureCollector` freeze gate：freeze 期间 MUST 更新 EWMA，MUST NOT 递增 consecutiveFailures，MUST NOT 调用 CooldownFsm |
| II4 | **Freeze hysteresis MUST prevent freeze oscillation** | Freeze 解除后 120s 内 MUST NOT 再次触发 freeze。再次大规模异常 MUST escalate to EmergencyDisable |

### III. Recovery Integrity

| # | Invariant | 验证方式 |
|---|-----------|---------|
| III1 | **Cooldown expiry MUST transition FAILED → RECOVERY_PROBING, NOT FAILED → ACTIVE** | `RecoveryConfirmationFsm` MUST enforce state transition legality |
| III2 | **RECOVERY_PROBING nodes MUST only receive probe traffic** | `ActiveSetManager` MUST exclude RECOVERY_PROBING from production selector |
| III3 | **STABILITY_VERIFICATION nodes MUST only receive probe traffic** | `ActiveSetManager` MUST exclude STABILITY_VERIFICATION from production selector |
| III4 | **Recovery MAY NOT skip stages** (including manual cooldown clear) | 用户手动清除 cooldown MUST 走 FAILED → RECOVERY_PROBING 合法路径 |
| III5 | **Exponential backoff MUST cap at 30 minutes** | Recovery FSM cooldown 延长 MUST NOT 永久冷冻节点 |

### IV. Reload Governance

| # | Invariant | 验证方式 |
|---|-----------|---------|
| IV1 | **Reload MUST be debounced** | `ReloadPolicyApplier` MUST enforce minimum interval（正常 30s） |
| IV2 | **Reload frequency MUST remain bounded** | 滑动 1h window ≤ 6 次 → 30s debounce, 7-10 → 60s, >10 → 120s |
| IV3 | **Catastrophic failure（全 eligible 为空）MUST bypass debounce** | 在 `MonitorActiveSetAsync` 或 `ReloadPolicyApplier` 中 MUST 检测并立即 reload |
| IV4 | **Reload MUST check ConfigGeneration before execution** | Generation mismatch MUST discard reload task |
| IV5 | **Reload MUST check Freeze is not active before execution** | `FreezeController.State == FREEZE_ACTIVE` MUST discard reload task |
| IV6 | **Reload MUST check ManualOverride is not active before execution** | `ManualOverride.IsActive` MUST discard reload task |

### V. Mutation Authority

| # | Invariant | 验证方式 |
|---|-----------|---------|
| V1 | **Higher-priority events MUST cancel lower-priority pending actions** | P0 > P1 > P2 > P3 > P4 > P5（见 §7.1 事件优先级表） |
| V2 | **ManualOverride MUST block automatic reload** | ManualOverride 激活期间，任何自动 reload MUST be rejected |
| V3 | **EmergencyDisable MUST stop all adaptive behavior immediately** | `EmergencyDisableAdaptiveAsync()` MUST set Enabled=false, call StopAsync, 不重启 xray |

### VI. Telemetry Integrity

| # | Invariant | 验证方式 |
|---|-----------|---------|
| VI1 | **Every active-set change MUST produce an `active_set_change` JSONL event with causal trace** | `BuildChangeReasons()` MUST include reason for every added/removed node |
| VI2 | **Telemetry MUST NOT block control-plane decisions** | JSONL 写入失败 MUST NOT 阻止 cooldown/reload/recovery |
| VI3 | **Telemetry retention MUST NOT consume unbounded disk** | Max 50MB/file, 7 days retention, 200MB total |

### VII. Scoring Integrity

| # | Invariant | 验证方式 |
|---|-----------|---------|
| VII1 | **Throughput MUST NOT enter HealthScore** | `ScoreCalculator.Compute()` MUST only use EWMA latency + loss rate |
| VII2 | **Bootstrap MUST always overwrite persisted scores** | `BootstrapProber.ProbeOneAsync` ALL code paths MUST call `node.UpdateScore()` |
| VII3 | **Score MUST remain in [1.0, 100.0]** | `ScoreCalculator.Compute()` MUST clamp to this range |
| VII4 | **TlsError and DNS failures MUST NOT penalize score** | `FailureCollector.GetPenalty()` MUST return (0.0, EwmaLatencyMs) for TlsError/DnsResolutionFailure/DnsPoisoningSuspected |

### VIII. Production Pool Integrity（P2 新增）

| # | Invariant | 验证方式 |
|---|-----------|---------|
| VIII1 | **Production Pool size MUST be bounded by [MinProductionNodes, MaxProductionNodes]** | `ActiveSetManager.GetProductionTags()` MUST return 3~6 节点（clamp 参数可配） |
| VIII2 | **Production Pool MUST NOT become empty** | 延续 Invariant I1——全 eligible 为空时 fallback 路径不变 |
| VIII3 | **Production node MUST NOT be replaced due to Standby having higher score** | `ActiveSetManager` MUST NOT 比较 Production score 与 Standby score 决定替换。Demotion 仅因 failure/cooldown/HealthState |
| VIII4 | **TrafficTier.Production ⇒ HealthState == Active && !IsInCooldown** | 设置 TrafficTier=Production 前 MUST 验证 HealthState 和 cooldown 状态 |
| VIII5 | **Standby score MUST only be used for Standby-internal ordering** | Standby → Production 晋升按 score 降序补位，但 Standby score MUST NOT 用于挑战 Production score |
| VIII6 | **Promotion MUST only be vacancy-driven** | 仅当 `ProductionCount < TargetProductionSize` 时触发。MUST NOT 在 pool 已满时因 score 差异触发 promotion |

### IX. Measurement Domain Integrity（P2 新增）

| # | Invariant | 验证方式 |
|---|-----------|---------|
| IX1 | **Production and Standby EWMA MUST NOT be directly compared** | 代码层面：Production 与 Standby 使用相同的 `ScoreCalculator`，但 score 比较仅在 Standby 池内进行（晋升排序） |
| IX2 | **TrafficTier MUST be persisted across restarts** | `ProfileExItem` MUST 存储 TrafficTier。重启后 MUST 恢复，防止所有 Standby 误入 Production |
| IX3 | **Standby 临时补位 MUST be marked for replacement** | 当 score ≥ 60 的 Standby 不足时，放宽至 ≥ 48 的临时补位节点 MUST 标记（Fallback Repair），下一次 vacancy 填补时优先替换 |

### X. Anti-Churn Integrity（v7.6 新增）

| # | Invariant | 验证方式 |
|---|-----------|---------|
| X1 | **Production 节点只因 explicit failure 离开** | 离开条件：score < EffectiveExit + MinTenure 过期 + cooldown + HealthState != Active。MUST NOT 因 relative competition 离开 |
| X2 | **Promotion 仅 vacancy-driven** | 仅当 ProductionCount < TargetProductionSize。MUST NOT 因 Standby 分数更高触发 |
| X3 | **Standby score 与 Production score 不直接比较决定替换** | 禁止 score-driven replacement（延续 VIII3, VIII5） |
| X4 | **EffectiveExit 始终有 floor（25）** | `max(25, min(35, median - 15))` — 防止极差环境下永不 eject |
| X5 | **MinTenure 保持离散 3 档** | 不引入连续自适应函数，保持可解释性 |
| X6 | **ReloadCooldown 全局强制（60s）** | 两次 reload 至少隔 60 秒。Catastrophic bypass（全 eligible 为空）除外 |
| X7 | **多机制 AND 关系，不互相争夺 topology authority** | 每层只控制一个维度：Entry=admission, Fallback=repair, Exit=failure sensitivity, Tenure=time gate, Cooldown=rate limiting |
