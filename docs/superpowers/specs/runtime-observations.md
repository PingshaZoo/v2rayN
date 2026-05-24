# Runtime Observations

**日期**: 2026-05-24（P2 设计定稿后更新）

> **目的**：记录 Production Stabilization Phase 的真实 runtime telemetry，驱动 evidence-based evolution。v7.5 P2（Bounded Production Pool）实施后，以下指标用于验证 tiering 设计的实际效果。

---

## 待收集指标

### 核心稳定性指标（继承 v7.4）

| 指标 | 说明 | 为什么重要 |
|------|------|-----------|
| **reload frequency** | 每小时 reload 次数，区分正常时段 vs 晚高峰 | 当前最大 UX 风险来源。直接衡量 ReloadPolicyApplier 的 debounce 策略是否合理 |
| **average production-pool lifetime** | Production Pool 从一次变更到下一次变更的平均时长 | 衡量系统稳定性的核心指标。频繁变更 = 用户感知到的连接中断 |
| **cooldown churn** | 节点进入/退出 cooldown 的频率和原因 | 判断 FailureCollector 的惩罚策略是否过于激进或过于保守 |
| **freeze trigger rate** | 全局冻结触发频率 | 过高 = 节点池整体不稳定；过低 = freeze 阈值可能设置偏高 |
| **recovery oscillation** | 节点恢复后是否频繁再次失败 | 衡量 Recovery FSM 的 STABILITY_VERIFICATION 阶段（5min）是否足够 |
| **evening stability** | 晚高峰时段（18:00-23:00）的 Production Pool churn 是否显著升高 | GFW/ISP 晚高峰干扰的实际影响程度 |
| **false cooldown ratio** | 进入 cooldown 后首次 probe 即成功的比例 | 过高 = cooldown 触发过于敏感（可能是 probe transient failure 而非真实节点故障） |
| **debounce utilization** | ReloadPolicyApplier 的实际 debounce 级别分布（30s vs 60s vs 120s） | 判断 reload budget 阈值设定是否合理 |

### Tiering 专项指标（v7.5 P2 新增）

| 指标 | 说明 | 为什么重要 |
|------|------|-----------|
| **production pool size distribution** | 实际 Production Pool 大小的分布（是否稳定在 TargetProductionSize 附近） | 验证 ActiveFraction=0.35 是否合理，pool 是否频繁缩胀 |
| **promotion count** | 每小时 Standby → Production 晋升次数 | P2 的关键新增 reload 来源。必须 < 2 次/小时 |
| **promotion trigger reason** | 晋升原因分布：cooldown 驱逐 vs HealthState 变更 vs score < Exit | 确认 promotion 是否仅由 failure 驱动 |
| **standby pool health** | Standby 节点的 score 分布（均值、P95、最小值） | 确认 Standby 池中有足够的高质量候选节点 |
| **measurement gap (probe vs production)** | 同一节点在 Standby 期间（probe-only）vs Production 期间（real traffic）的 EWMA 差异 | 量化 measurement asymmetry 的实际影响 |
| **production tenure** | 节点在 Production Pool 中的平均停留时长 | 衡量 Production Pool 的稳定性。过短 = oscillation |
| **tiering effectiveness** | Production Pool 节点 vs Standby Pool 节点的 probe latency 差异 | 验证 tiering 是否真正把更好的节点放在了 Production |
| **standby insufficiency events** | Standby 不足（score ≥ 60 节点不够补位）事件频率 | 验证 MinProductionNodes=3 是否在极端情况下可维持 |

---

## 数据来源

以上指标可从 `guiLogs/adaptive.log`（JSONL telemetry）中提取。

---

## 记录

| 日期 | 观测 |
|------|------|
| 2026-05-24 | P2 设计定稿：Bounded Production Pool + Failure-Driven Promotion。上述 tiering 指标待 v7.5 实施后开始收集。 |
