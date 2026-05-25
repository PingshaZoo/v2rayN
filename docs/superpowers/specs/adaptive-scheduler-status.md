# Adaptive Node Scheduler — Engineering Status

**日期**: 2026-05-25 | **对应 Spec 版本**: 1.3

---

## 当前状态总览

| 优先级 | 状态 | 说明 |
|--------|------|------|
| P0（v7.2） | ✅ 完成 | Recovery FSM + Global Freeze |
| P1（v7.3） | ✅ 完成 | DNS 归因分离 + 随机载荷探活 + DNS 缓存生命周期 |
| P2（v7.2-v7.3） | ✅ 完成 | 稳定性增强（StatsPoller/并发上限/多目标探活等） |
| P3.1 | ✅ 完成 | RuntimePolicyApplier 双模策略 |
| P3.2 | ✅ 完成 | 调度质量指标 |
| P3.3 | 🔵 未开始 | UDP/QUIC 独立节点池（受益于 TrafficTier 抽象） |
| P3.4 | 🔵 未开始 | 调度决策审计日志 UI |
| **P0（v7.4）** | ✅ 完成 (2026-05-23) | HealthState gate + FSM 合法路径 + 兜底逻辑 |
| **P1（v7.4）** | ✅ 完成 (2026-05-23) | Reload 基线延长 + UI 合并 + 全局开关删除 + severity 分级 |
| **P2（v7.5）** | ✅ 完成 (2026-05-24) | Bounded Production Pool + Failure-Driven Promotion（UI 配置/P2a-c 子任务待后续） |
| **Anti-Churn（v7.6）** | ✅ 完成 (2026-05-25) | Adaptive Exit + Fallback Repair + MinTenure + ReloadCooldown |

---

## v7.4 P0 — 修复完成 (2026-05-23)

| # | Bug | 修复 | 涉及文件 |
|---|-----|------|---------|
| B1 | 手动清除 cooldown 绕过 Recovery FSM 直接触发 reload | ✅ `eligible` 条件加 `HealthState == Active` gate | `ActiveSetManager.cs:58,178` |
| B2 | RECOVERY_PROBING/STABILITY_VERIFICATION 节点可进入 production selector | ✅ 同上，`GetActiveTags()` + `HasActiveSetChanged()` 均加 gate | `ActiveSetManager.cs:58,178` |
| B3 | 全 eligible 为空兜底逻辑仅考虑 cooldown 剩余时间 | ✅ fallback 优先级: RECOVERY_PROBING (probe success) > STABILITY_VERIFICATION > cooldown | `ActiveSetManager.cs:62-79,184-196` |

---

## v7.4 P1 — 修复完成 (2026-05-23)

| # | 任务 | 修复 | 涉及文件 |
|---|------|------|---------|
| P1-1 | `NormalInterval` 15s → 30s, `minUpdateIntervalMs` 10s → 30s | ✅ `NormalInterval = 30s`, `minUpdateIntervalMs = 30_000` | `ReloadPolicyApplier.cs:35`, `AdaptiveSchedulerManager.cs:327` |
| P1-2 | UI 合并：ProbeUrl/Interval/Timeout/HeavyFraction 移到 PolicyGroup 对话框 | ✅ 后端完成：`ProtocolExtraItem` 加 per-group probe 字段，ViewModel 双向绑定，Scheduler 合并 per-group + global 配置 | `ProtocolExtraItem.cs`, `AddGroupServerViewModel.cs`, `AdaptiveSchedulerManager.cs` |
| P1-3 | 删除全局 `Enabled` 双重检查 | ✅ 移除 `AdaptiveSchedulerItem.Enabled` gate，仅保留 per-group `AdaptiveEnabled` | `MainWindowViewModel.cs:638` |
| P1-4 | Debounce severity 分级（普通 cooldown → 30-60s, 全 eligible 空 → 立即） | ✅ `ApplyImmediateAsync` 绕过 debounce，`IsEligiblePoolEmpty` 检测 catastrophic | `IAdaptivePolicyApplier.cs`, `ReloadPolicyApplier.cs`, `RuntimePolicyApplier.cs`, `ActiveSetManager.cs`, `AdaptiveSchedulerManager.cs` |

---

## v7.5 P2 — 实现完成 (2026-05-24)

### 设计方向：Bounded Production Pool + Failure-Driven Promotion

**定性**：Production Admission Control（限制 exposure），不是 Runtime Traffic Scheduling（优化 routing）。

**核心变更**：

| # | 变更 | 状态 | 涉及文件 |
|---|------|------|---------|
| P2-1 | TargetProductionSize 公式 | ✅ `clamp(ceil(N×0.35), 3, 6)` 替代 `max(2, ceil(N×2/3))` | `ActiveSetManager.cs` |
| P2-2 | TrafficTier 抽象 | ✅ `enum TrafficTier { Production, Standby }` + `NodeState.TrafficTier` 字段/属性/持久化 | `NodeState.cs`, `NodeSnapshot` |
| P2-3 | Vacancy-Driven Promotion | ✅ `GetProductionTags()` 仅在 `vacancy > 0` 时从 Standby 晋升。禁止 score-driven replacement | `ActiveSetManager.cs` |
| P2-4 | TrafficTier 持久化 | ✅ `ProfileExItem.AdaptiveTrafficTier` + 恢复逻辑 + `SetAdaptiveData` 写入 | `ProfileExItem.cs`, `AdaptiveSchedulerManager.cs`, `ProfileExManager.cs` |
| P2-5 | Standby fallback | ✅ 标准晋升 (≥60) 不足时放宽至 ≥35 临时补位 | `ActiveSetManager.cs` |
| P2-6 | UI：Production Pool 配置 | ✅ per-group ActiveFraction/Min/Max 可配置 | `ProtocolExtraItem.cs`, `AddGroupServerViewModel.cs`, `AddGroupServerWindow.xaml`, `AddGroupServerWindow.xaml.cs`, `AddGroupServerWindow.axaml`, `AddGroupServerWindow.axaml.cs`, `ActiveSetManager.cs`, `AdaptiveSchedulerManager.cs` |
| P2-7 | UI：节点 TrafficTier 标记 | ✅ 后端：`AdaptiveActiveVal` 显示 "Production"/"Standby"/HealthState | `ProfilesViewModel.cs` |

**关键设计决策**：

- ✅ 复用现有 Entry=60 / Exit=35 hysteresis（Promotion ↔ Demotion 共用）
- ✅ 复用现有 ReloadPolicyApplier debounce（多 vacancy 自动合并）
- ✅ 复用现有 catastrophic bypass（Production Pool 清空 → ApplyImmediateAsync）
- ❌ **不引入 PromotionMargin**（会重新引入 score-driven replacement 和 optimization semantics）
- ❌ **不比较 Production score 与 Standby score**（避免 measurement asymmetry 和 "寻找最快节点"）

**与当前系统兼容性**：

| 现有概念 | P2 映射 |
|---------|--------|
| Active Set (topK) | → Production Pool (3-6 nodes) |
| Explorer (probe only) | → Standby Pool 子集 (score < 60) |
| Recovery nodes | → 不变（仍不入 production） |
| Cooldown nodes | → 不变（仍不入 production） |

---

## v7.6 Anti-Churn — 设计定稿 (2026-05-25)

### 问题

60 节点环境下 Production pool 频繁 reload——固定 Exit=35 在分数聚类时形成"悬崖边"，Fallback promotion 也用 35，新晋升节点零缓冲。

### 设计方向：四层防抖，AND 串联，保持 failure-driven demotion

| # | 变更 | 状态 | 涉及文件 |
|---|------|------|---------|
| AF-1 | FallbackPromotionThreshold 35→48 | ✅ 完成 (2026-05-25) | `ActiveSetManager.cs` |
| AF-2 | Adaptive Exit: `max(25, min(35, median-15))` | ✅ 完成 (2026-05-25) | `ActiveSetManager.cs` |
| AF-3 | MinTenure 3 档（30s/120s/300s），基于 runningScore | ✅ 完成 (2026-05-25) | `ActiveSetManager.cs`, `NodeState.cs` |
| AF-4 | ReloadCooldown 60s hard floor | ✅ 完成 (2026-05-25) | `AdaptiveSchedulerManager.cs` |

**关键设计决策**：

- ✅ 保持 failure-driven demotion — Production 节点只因 explicit failure 离开
- ✅ 保持 vacancy-driven promotion — 不因 Standby 分数更高触发替换
- ✅ Adaptive Exit 是 environment-adaptive failure sensitivity，不是 relative competition
- ❌ 不做 rank-based sticky — 本质是 score-driven replacement
- ❌ 不做 continuous MinTenure — 保持离散 3 档，可解释
- ❌ Production 与 Standby score 不直接比较

**语义区分**：

| 常量 | 值 | 语义 |
|------|-----|------|
| EntryThreshold | 60 | Normal Admission — "优秀准入" |
| FallbackPromotionThreshold | 48 | Fallback Repair — "够用修复"（degraded mode） |
| EffectiveExitThreshold | max(25, min(35, median-15)) | Adaptive Exit — "环境自适应 failure sensitivity" |

### 子任务（从原 P2 降级）

| # | 任务 | 状态 |
|---|------|------|
| P2a | Runtime Evidence 收集（reload frequency / churn / freeze rate 观测） | 待实施 |
| P2b | ReloadCoordinator（统一 reload 调度入口） | 待规划 |
| P2c | IClock 统一迁移（legacy DateTime.UtcNow → IClock） | 待规划 |

---

## 测试覆盖

| 状态 | 数量 |
|------|------|
| 总测试数 | 372 |
| 通过 | 369 |
| 需 xray-core | 3 |

### 测试文件清单

| 测试文件 | 数量 | 覆盖 |
|---------|------|------|
| `FailureCollectorTests.cs` | 9 | FailureType penalty, TlsError no-op |
| `BootstrapAndScorePersistenceTests.cs` | 5 | Score floor, worst-case, 覆盖历史, 过期 |
| `ActiveSetManagerTests.cs` | 24 | Entry/Exit hysteresis, sticky, oscillation, cooldown, HealthState gate (P0), IsEligiblePoolEmpty, fallback priority (P1), vacancy-driven promotion (P2) |
| `ProductionPoolTests.cs` | 22 | TargetProductionSize 公式, TrafficTier gate, vacancy-driven promotion, score-driven replacement禁制, Standby fallback (P2) |
| `ReloadPolicyApplierTests.cs` | 3 | Debounce, ApplyImmediateAsync bypass, reload budget (P1) |
| `EmergencyDisableAdaptiveTests.cs` | 4 | 幂等性, 清空 |
| `XrayTagDuplicationIntegrationTests.cs` | 2 | selector dedup 行为契约（需 xray） |
| `ScoreLoggerJsonlTests.cs` | 6 | JSONL 格式, 事件类型 |
| `TopKFormulaTests.cs` | 4 | N=1~20 top-K 边界 |
| `XrayVersionCheckerTests.cs` | 14 | 版本解析, 比较 |
| `ScoreExpirationTests.cs` | 5 | 4h 过期, 持久化 |
| `XrayStatsPollerTests.cs` | 12 | 异常检测, 边界, 生命周期 |
| `BoundaryNodeCountTests.cs` | 22 | Cooldown 边界, 全冷却兜底 |
| `PerTagProxyTrafficTests.cs` | 6 | 线程安全 |
| `ProbeConcurrencyTests.cs` | 19 | SemaphoreSlim gate |
| `ReplayableTelemetryTests.cs` | 9 | probe_result, ewma_update, 完整链路 |
| `MultiTargetProbeTests.cs` | 8 | 多 URL, 平均 TTFB |
| `RuntimePolicyApplierTests.cs` | 11 | API 可用/不可用, fallback |
| `SchedulingQualityMetricsTests.cs` | 15 | 熵, P95, 均值, 标准差 |
| `RecoveryConfirmationFsmTests.cs` | 23 | 四阶段恢复, 合法/非法迁移, 退避 |
| `GlobalFreezeControllerTests.cs` | 21 | freeze 触发/阻塞/解除, hysteresis |
| `DnsCacheManagerTests.cs` | 18 | 缓存 CRUD, confidence 生命周期, TTL |
| `DnsAttributionTests.cs` | 8 | DNS 零惩罚, no-op, GlobalFreeze 隔离 |
| `FreezeGateTests.cs` | 7 | freeze 期间 EWMA/cooldown blocked |

---

## 已实现模块

| 模块 | 文件 | 版本 |
|------|------|------|
| `AdaptiveSchedulerManager` | `Handler/AdaptiveNodeScheduler/AdaptiveSchedulerManager.cs` | v7.6 (v7.5 + ReloadCooldown 60s) |
| `ScoreCalculator` | `Handler/AdaptiveNodeScheduler/ScoreCalculator.cs` | v7.0 |
| `FailureCollector` | `Handler/AdaptiveNodeScheduler/FailureCollector.cs` | v7.3 |
| `CooldownFsm` | `Handler/AdaptiveNodeScheduler/CooldownFsm.cs` | v7.1 |
| `ActiveSetManager` | `Handler/AdaptiveNodeScheduler/ActiveSetManager.cs` | v7.6 (v7.5 + FallbackRepair + AdaptiveExit + MinTenure) |
| `BootstrapProber` | `Handler/AdaptiveNodeScheduler/BootstrapProber.cs` | v7.3 |
| `ProbeService` | `Handler/AdaptiveNodeScheduler/ProbeService.cs` | v7.3 |
| `ScoreLogger` | `Handler/AdaptiveNodeScheduler/ScoreLogger.cs` | v7.1 |
| `ReloadPolicyApplier` | `Handler/AdaptiveNodeScheduler/ReloadPolicyApplier.cs` | v7.4 (30s baseline, ApplyImmediateAsync) |
| `RuntimePolicyApplier` | `Handler/AdaptiveNodeScheduler/RuntimePolicyApplier.cs` | v7.4 (ApplyImmediateAsync) |
| `RecoveryConfirmationFsm` | `Handler/AdaptiveNodeScheduler/RecoveryConfirmationFsm.cs` | v7.2 |
| `GlobalFreezeController` | `Handler/AdaptiveNodeScheduler/GlobalFreezeController.cs` | v7.2 |
| `DnsCacheManager` | `Handler/AdaptiveNodeScheduler/DnsCacheManager.cs` | v7.3 |
| `SchedulingQualityMetrics` | `Handler/AdaptiveNodeScheduler/SchedulingQualityMetrics.cs` | P3.2 |
| `IClock` / `SystemClock` / `FakeClock` | `Handler/AdaptiveNodeScheduler/IClock.cs` | v7.2 |
| `XrayStatsPoller` | `Handler/AdaptiveNodeScheduler/XrayStatsPoller.cs` | P2.1 |
| `TrafficTier` (enum) | `Handler/AdaptiveNodeScheduler/NodeState.cs` | v7.5 (P2 新增) |

---

## 已知风险

| 风险 | 说明 | 缓解 |
|------|------|------|
| Dual time-source | 新 FSM 模块注入 IClock，legacy 模块直接 DateTime.UtcNow | P2c 统一迁移 |
| Freeze 期间 cooldown 历史积累 | §11.8 split-brain：freeze 阻止消费但未阻止生产 | v7.3 freeze gate 已缓解 |
| 备用 DNS resolver 未实现 | DNS 缓存失效后仍使用可能被污染的同一 DNS 源 | P2c 实施 |
| RuntimePolicyApplier 在 xray 不可用 | xray 无动态 outbound API | 文档已标注 |
| Production pool 缩小后故障冲击 | 6 节点 pool 挂 2 个 = 33% capacity 消失，冲击比 14 节点大 | vacancy-driven promotion + ApplyImmediateAsync 快速补位 |
| 小节点池 Tiering 意义有限 | ≤5 节点时 Production Pool ≈ Standby Pool 无实质差异 | MinProductionNodes=3 保证下限，小池自然退化到全入 production |
| ~~手动清除 cooldown / Recovery 节点入 production~~ | v7.4 HealthState gate 已修复 | — |
| ~~Reload responsiveness 过度激进~~ | v7.4 P1 延长至 30s | — |
| ~~全 eligible 为空时 debounce 延迟 reload~~ | v7.4 P1 ApplyImmediateAsync 已添加 | — |
| 60+ 节点下固定 Exit=35 导致 churn | 大量节点 hovering around 35，一次慢探测触发 demotion→promotion→reload 链 | v7.6 Adaptive Exit + Fallback Repair + MinTenure + ReloadCooldown 四层防抖 |
| EffectiveExit floor 可能过高（25）或过低 | Floor=25 是理论值，实际效果需 runtime 验证 | Runtime telemetry 观测 EffectiveExit 实际取值分布 |
| MinTenure 3 档的阈值（55/40）未经数据校准 | runningScore 分布在不同网络环境下可能差异很大 | Runtime telemetry 观测 runningScore 分布，必要时调整阈值 |

---

## Runtime Evidence（待收集）

| 指标 | 当前值 | 目标值 |
|------|--------|--------|
| reload frequency（正常） | 待观测 | < 4 次/小时 |
| reload frequency（晚高峰） | 待观测 | 不显著升高 |
| average production-pool lifetime | 待观测 | — |
| promotion count (per hour) | 待观测 | < 2 |
| production pool size distribution | 待观测 | 3-6 |
| cooldown churn | 待观测 | — |
| freeze trigger rate | 待观测 | 低频 |
| recovery oscillation | 待观测 | — |
| evening stability | 待观测 | 不明显恶化 |

> **当前真正缺的不是理论，而是 runtime evidence。** 在收集到足够数据前，不做大规模架构重构。
