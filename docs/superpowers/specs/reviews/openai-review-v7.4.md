# OpenAI External Review — v7.4

**日期**: 2026-05-23
**来源**: OpenAI
**主题**: Adaptive Node Scheduler 架构评估与生命周期治理方向

---

## 总体评估

当前系统已经进入**"架构现实主义"阶段**而非"功能堆叠"阶段。DeepSeek 的分析同时覆盖了 architecture semantics、runtime constraints 和 actual code path，这是关键的进步。项目现在最需要的是**设计哲学与真实代码约束之间的闭环**。

核心判断：**当前问题不是缺少机制，而是现有机制的"稳定性权重"不够高。**

---

## 共同根源：Mutation Authority 碎片化

频繁重启和 UI 配置重叠这两个问题，暴露出一个共同根源：

```
AdaptiveScheduler 已经开始侵入 v2rayN 原本的配置生命周期，
但 mutation authority 还没有彻底统一。
```

**当前 mutation source 全景**（每个 source 独立触发 reload，彼此不知道对方存在）：

| Mutation Source | 触发路径 | 是否感知其他 source |
|-----------------|---------|-------------------|
| Cooldown 进入/退出 | `FailureCollector` → `CooldownFsm` → `HasActiveSetChanged` → reload | ❌ 不知 ManualOverride、UI edit |
| Recovery FSM 推进 | `MonitorActiveSetAsync` → recovery promoted → reload | ❌ 不知 freeze 状态 |
| 手动清除 cooldown | UI 直接修改 `_cooldownUntil` → eligible 池变化 → reload | ❌ 绕过 Recovery FSM |
| PolicyGroup 编辑 | UI 修改 `ProtocolExtra` → `SaveServerAsync` → 外部调用 `LoadCore` | ❌ 不知 adaptive 正在运行 |
| Settings 修改 | `OptionSettingViewModel` → `Config.AdaptiveSchedulerItem` 变更 | ❌ 不知哪个 group 正在使用 |
| GlobalFreeze | `FreezeController.Evaluate` → BlockMutation → reload skipped | ⚠️ freeze 期间 cooldown 仍在推进 |
| ManualOverride | 用户手动切节点 → 锁定 5min | ⚠️ 优先级最高但无显式 authority transfer |

---

## Config-Level State vs Runtime-Level State

```
cooldown 本质上应该是 "routing eligibility change"（运行时资格变更）
但因为 xray 没有 runtime API，被迫用 "topology rebuild"（拓扑重建）来模拟
→ 所有 eligibility change 都变成了 config rewrite + restart
```

这意味着 reload unavoidable，但 reload frequency 可控。

---

## DeepSeek P0/P1/P2 分层评估

### P0 — Semantic Correctness Repair ✅ 完全同意

| 修改 | 评价 |
|------|------|
| `HasActiveSetChanged` 加 `HealthState` gate | 正确。修 semantic correctness，阻止 RECOVERY_PROBING/STABILITY_VERIFICATION 节点进入 production selector。会立刻减少"无意义 reload" |
| 手动清除 cooldown 走合法 FSM 路径 | 正确。当前 bypass Recovery FSM 直接进入 active set，与合法迁移冲突。收益非常大 |

### P1 — Reload Aggressiveness Tuning ⚠️ 方向对，参数需谨慎

**关键警告**：debounce 本质是在交易"更少 reload"与"更慢 failure reaction"。**Reload suppression 不能变成 failure blindness**。建议：
- 普通 cooldown（单个节点进/出）：允许 30-60s batching
- 全节点故障（eligible 池为空）：立即 mutation，不等待 debounce
- Freeze 期间：cooldown 入队但不 apply，等 freeze 解除后一次性评估

### P2 — ReloadCoordinator ✅ 方向正确，但不现在做

Centralized mutation authority 是正确方向，但现在立刻做 risk 太高。当前还缺 runtime telemetry data。

---

## 代码策略与文档哲学脱节

**这是当前最重要的发现。**

设计文档明确声明"稳定性 > 响应性 > 最优性"，但代码参数隐含"响应性优先"：

| 参数 | 当前值 | 隐含哲学 |
|------|--------|---------|
| `minUpdateIntervalMs` | 10s | 响应性优先 |
| `NormalInterval` | 15s | 响应性优先 |
| `checkIntervalMs` | 5s | 响应性优先 |

不是缺 feature，而是 **feature 参数隐含了错误的系统哲学**。

---

## Adaptive 不应降级为 Selector Type

- **UX 层面**：Adaptive 表现为 PolicyGroup 的一个 mode — 合理
- **Architecture 层面**：AdaptiveScheduler 是 control-plane subsystem，不是 selector strategy
- **禁止**：把 AdaptiveScheduler 硬塞进 `PolicyGroupType` 枚举

**UI 正确分层**：

| 层级 | 归属 | 内容 |
|------|------|------|
| **Global 层** | Settings > Adaptive | engine enable/disable, telemetry retention, log level, debug mode |
| **Per-Group 层** | PolicyGroup 编辑对话框 | enable adaptive, probe URL/interval/timeout, cooldown, recovery, freeze, heavy probe |

---

## 下一步行动建议

### Phase 1 — 现在立刻（Semantic Correctness Repair）
- `ActiveSetManager` 加 `HealthState` gate
- 手动清除 cooldown 走合法 FSM 路径
- 全 eligible 为空时的兜底逻辑调整

### Phase 2 — 接下来（Reload Lifecycle Tuning + UI Cleanup）
- `NormalInterval` 15s → 30s
- `minUpdateIntervalMs` 10s → 30s
- UI 合并到 PolicyGroup 对话框
- 删除全局 `Enabled` 双重检查
- 为 catastrophic failure 保留快速响应路径

### Phase 3 — 最重要（Runtime Evidence Collection）
**不写新代码，先观察。** 当前真正缺的不是理论，而是 runtime evidence。

---

## 绝对禁止事项

```
禁止重写 scheduler
禁止做 AI/RL score
禁止做复杂 QoS inference
禁止做 runtime IPC abstraction
禁止做 distributed authority graph
禁止继续加 scheduler feature
```

当前 bottleneck 是 **reload lifecycle**，不是调度算法不够高级。

---

## 阶段转换信号

| 旧阶段 | 新阶段 |
|--------|--------|
| 如何计算更准确的 score | 如何减少不必要的 reload |
| 如何增加更多 mutation source | 如何统一 mutation authority |
| 如何更快响应节点变化 | 如何更稳定地维持 active-set |
| 功能堆叠 | 架构现实主义 |

**现在最正确的方向不是"更聪明"，而是"更克制"。**
