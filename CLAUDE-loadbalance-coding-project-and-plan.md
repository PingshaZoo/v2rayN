# Adaptive Load Balancing — 实现审计与下一阶段计划

基于 `CLAUDE-loadbalance.md`（v2.0 设计文档）对照当前实现（截至 2026-05-21）。

---

## 1. 设计文档 vs 实现对照

### Phase 1 — 核心调度（设计文档标记 2-3 周）

| 模块 | 设计文档 | 实现文件 | 状态 | 备注 |
|------|---------|---------|------|------|
| NodeState（私有锁版） | §4.1 | `NodeState.cs` | ✅ 完全符合 | 含 `ChildIndexId`（设计文档外补充） |
| ScoreCalculator（平方放大） | §6.2 | `ScoreCalculator.cs` | ✅ 完全符合 | 公式、常量与设计文档一致 |
| BootstrapProber（3s 全局超时） | §5.1 | `BootstrapProber.cs` | ✅ 完全符合 | 逻辑与设计文档一致 |
| CooldownFsm（1/3 全局约束） | §5.4 | `CooldownFsm.cs` | ✅ 完全符合 | 公式与设计文档一致 |
| FailureCollector（time-decayed EWMA） | §5.3 | `FailureCollector.cs` | ✅ 完全符合 | `DecayedAlpha` 公式与设计文档一致 |
| ScoreLogger（30s 快照日志） | §8.3 | `ScoreLogger.cs` | ✅ **已启动** | 在 `StartProbesAsync()` 中启动 |
| IAdaptivePolicyApplier + ReloadPolicyApplier | — | `IAdaptivePolicyApplier.cs`, `ReloadPolicyApplier.cs` | ✅ 已实现 | 架构必需，设计文档未描述 |
| AdaptiveDispatcher（WeightedRandom） | §5.6 | — | 🔄 **替换方案** | 见 §3 偏差分析 |

### Phase 2 — 主动测量（设计文档标记 Phase 1 稳定后）

| 模块 | 设计文档 | 实现文件 | 状态 | 备注 |
|------|---------|---------|------|------|
| TTFB 探活（HTTP HEAD via SOCKS5 + HttpClient 池化） | §5.2 | `ProbeService.cs` | ✅ 实现 | 对应 `TtfbProber`，`ProbeUrl` 可配置 |
| Cooldown 恢复探活（到期前提前验证） | §7.1 状态机 | `ProbeService.cs` | ✅ **已实现** | 到期前窗口内提前 TTFB 探活 |
| XrayStatsPoller（吞吐率异常检测，5s 轮询） | §5.5 | — | ❌ 未实现 | 发现高分低吞吐节点 |
| UDP/QUIC 独立节点池 | §7.3 | — | ❌ 未实现 | `BuildNodeStates` 固定 `Tcp` |

### Phase 3 — 可观测性与持久化（设计文档标记 Phase 2 稳定后）

| 模块 | 设计文档 | 实现文件 | 状态 | 备注 |
|------|---------|---------|------|------|
| QoS UI 面板（Score/EWMA TTFB/Cooldown/Active 列） | §9 Phase 3 | `ProfileItemModel.cs`, `ProfilesViewModel.cs`, `ProfilesView.xaml` | ✅ **超前实现** | 含排序支持 |
| 分数持久化 + 启动恢复 | §9 Phase 3 | `ProfileExItem.cs` + `AdaptiveSchedulerManager.cs` | ✅ **已实现** | Bootstrap 前从 ProfileExItem 恢复 |
| QUIC 连接健康检查（30s HEAD 探测） | §9 Phase 3 | — | ❌ 未实现 | 依赖 UDP 独立池 |
| 调度决策审计日志 | §9 Phase 3 | — | ❌ 未实现 | 每次选节点记录候选集 |

### 设计文档外补充的功能

| 功能 | 涉及文件 | 原因 |
|------|---------|------|
| `AdaptiveSchedulerManager` 编排器 | `AdaptiveSchedulerManager.cs` | 设计文档无对应模块，实际需要串联所有子模块 |
| `ActiveSetManager`（top-K + explorer 活性集追踪） | `ActiveSetManager.cs` | tag duplication 方案需要明确 active set 定义 |
| Per-group `AdaptiveEnabled` 开关 | `ProtocolExtraItem.cs`, `AddGroupServerViewModel`, 两个 XAML | UX 修正：避免静默覆盖 PolicyGroupType |
| 子节点流量统计归因 | `StatisticsXrayService.cs`, `StatisticsManager.cs`, `ServerSpeedItem.cs` | 用户需求：各子节点流量可见 |
| `TagToIndexId` 映射 | `AdaptiveConfig.cs`, `AdaptiveSchedulerManager.cs`, `StatisticsManager.cs` | 连接 NodeState 与 ProfileItem |

---

## 2. 当前实现状态汇总

### ✅ 已实现（11 个文件）

| 文件 | 职责 | 设计文档对应 |
|------|------|------------|
| `NodeState.cs` | 单节点评分状态（私有锁） | §4.1 |
| `ScoreCalculator.cs` | 评分公式（平方放大） | §6.2 |
| `BootstrapProber.cs` | 启动 TCP 探活 | §5.1 |
| `CooldownFsm.cs` | 冷却状态机（1/3 全局约束） | §5.4 |
| `FailureCollector.cs` | 失败事件 + time-decayed EWMA | §5.3 |
| `ProbeService.cs` | TTFB 探测（HttpClient 池化 + cooldown 恢复） | §5.2（TtfbProber） |
| `ScoreLogger.cs` | 30s 快照日志 | §8.3 |
| `ActiveSetManager.cs` | top-K active set 追踪 | 设计文档外 |
| `IAdaptivePolicyApplier.cs` | 策略应用抽象 | 设计文档外 |
| `ReloadPolicyApplier.cs` | reload 实现（trailing debounce, 30s） | 设计文档外 |
| `AdaptiveSchedulerManager.cs` | 控制面编排器 | 设计文档外 |

### ❌ 未实现

- XrayStatsPoller（吞吐率异常检测）
- UDP/QUIC 独立节点池
- QUIC 连接健康检查
- 调度决策审计日志

---

## 3. 实现偏差分析

| 偏差项 | 设计文档要求 | 实际情况 | 偏差原因 | 评估 |
|--------|------------|---------|---------|------|
| 调度方式 | C# `AdaptiveDispatcher.WeightedRandom()` + connection hooks | xray tag duplication + random balancer | C# 不进 data plane，连接路由由 xray 负责 | ✅ 合理，实际更优 |
| 启动序列 | Bootstrap → 调度器接受连接 → 30s EWMA 稳定 | `InitializeNodes` → `BootstrapAsync` → LoadCore → `StartProbesAsync` | 适配 xray config 生成流程 | ✅ 合理 |
| Per-group 开关 | 无（假设全局） | `ProtocolExtraItem.AdaptiveEnabled` | UX 修复：避免 PolicyGroupType 被静默覆盖 | ✅ 合理改进 |
| 统计归因 | 无 | `PerTagProxyTraffic` + `TagToIndexId` | 用户需求：子节点流量可见 | ✅ 合理补充 |
| `ActiveConnections` | `OnConnectionCompletedAsync` 使用 | 已删除 | 设计文档的 C# 调度方案被放弃后无调用者 | ✅ 已清理 |
| Cooldown 恢复 | 到期前 5s TTFB 探活 | **已实现**：窗口提前探活 | — | ✅ 当前已实现 |
| 分数持久化 | 重启后恢复上次分数 | **已实现**：Bootstrap 前读取 ProfileExItem | — | ✅ 当前已实现 |
| ScoreLogger 启动 | 未指定启动方式 | StartProbesAsync 中启动 | — | ✅ 当前已实现 |

---

## 4. 下一阶段优先实现

### P1 — 功能完整性

| # | 任务 | 涉及文件 | 工作量 | 说明 |
|---|------|---------|--------|------|
| 1 | **xray tag duplication 运行时验证** | 独立测试 | 几个小时 | 验证 xray random balancer 是否保留重复 selector 条目。若不保留，需要 fallback 到 multi-tier active set |
| 2 | **ProbeUrl 用户配置 UI 入口** | `SettingsView`, `SettingsViewModel` | ~30 行 | 当前 `AdaptiveSchedulerItem.ProbeUrl` 存在但无 UI 入口 |

### P2 — 功能增强

| # | 任务 | 涉及文件 | 工作量 | 说明 |
|---|------|---------|--------|------|
| 3 | **实现 XrayStatsPoller** | 新文件 | ~80 行 | 5s 轮询 `/debug/vars`，发现高分但 < 1KB/s 节点 → 触发额外 TTFB 探活 |
| 4 | **集成 XrayStatsPoller 到 AdaptiveSchedulerManager** | `AdaptiveSchedulerManager.cs` | ~15 行 | 在 `StartProbesAsync()` 中创建并启动 |

### P3 — 低优先级

| # | 任务 | 说明 |
|---|------|------|
| 5 | UDP/QUIC 独立节点池 | `BuildNodeStates` 支持 `Protocol` 选择，`ProbeService` 支持 UDP 探测 |
| 6 | 调度决策审计日志 | 每次 active set 变化记录候选集快照 |
| 7 | QUIC 连接健康检查 | 30s HEAD 探测 |

---

## 5. 已知技术债务

1. **`NodeState.Protocol` 固定为 `Tcp`** — `BuildNodeStates` 写死 `Protocol = ProxyProtocol.Tcp`，UDP 支持需要额外工作
2. **`ProbeService.DefaultProbeUrl` 硬编码** — `AdaptiveSchedulerItem.ProbeUrl` 存在但未在 UI 暴露配置入口
3. **`PerTagProxyTraffic` 使用 ValueTuple** — `Dictionary<string, (long Up, long Down)>` 不可序列化，若需序列化到配置文件需改为 `record`
4. **`ReloadPolicyApplier` 导致 xray 重启** — Phase 1 合理折衷，长期目标是 `RuntimePolicyApplier`（通过 xray runtime API 动态更新 balancer）

---

## 6. 架构完整性检查

```
设计文档原则                  状态
────────────────────────────────────────────────
C# 不进 Data Plane          ✅ tag duplication 替代 C# 调度
不碰 xray-core 内部          ✅ 只读 stats API + 生成配置
不做请求级切换                ✅ 连接级调度（xray balancer）
不做全局最优计算              ✅ weighted random，非全局排序
不做复杂 ML 模型              ✅ 仅 EWMA + 平方放大
不 fork xray-core            ✅ 零修改 xray

架构边界                    ✅ 全部满足
```
