# Adaptive Load Balancing — 综合评审报告 v3.0

**版本**: v3.0 — 架构现实收敛版（2026-05-21）  
**基础文档**: `CLAUDE-loadbalance.md`（v4.0 设计文档）  
**进度来源**: `CLAUDE-loadbalance-coding-project-and-plan.md`（截至 2026-05-21）  
**评审立场**: 生产级系统，取最严格标准，但不做过度工程化

---

## 核心架构修正（2026-05-21）

本版本是一次"架构现实收敛（Reality Alignment）"修订。

此前系统设计建立在：
- xray selector 支持重复 tag 加权
- `[A,A,A,B] => 75% / 25%`

这一假设上。

经过集成测试（`XrayTagDuplicationIntegrationTests`）与 xray 源码确认（`app/router/balancing.go` `SelectOutbounds()`），现已明确：
- xray selector 本质是 prefix match
- 匹配结果会做 dedupe
- repeated tag weighting 无效
- xray random balancer 实际行为为 active-set 内 uniform random

因此系统正式从 `Weighted Adaptive Scheduler` 收敛为 `Adaptive Active-Set Scheduler`。

这是架构收敛，不是失败。

系统核心目标调整为：
- 动态剔除坏节点
- 自动恢复节点
- 降低用户手工切节点频率

而不是：
- 精确概率负载均衡

---

## 当前系统真实状态

| 模块 | 状态 |
|---|---|
| Bootstrap probing | 有效 |
| EWMA smoothing | 有效 |
| FailureCollector | 有效 |
| Cooldown FSM | 有效 |
| Active Set Management | 有效 |
| Hysteresis | 有效 |
| Score persistence | 有效 |
| JSONL telemetry | 有效 |
| Weighted routing (tag duplication) | 已移除（架构假设不成立） |

---

## 系统能力边界

当前系统**能做到**：

- 自动淘汰坏节点（cooldown + active set 驱逐）
- 自动恢复好节点（cooldown 到期 + recovery probing）
- 动态 active set（基于 score + hysteresis 进出管理）
- 自适应学习（time-decayed EWMA，观测越久远影响越小）
- 冷启动保护（Bootstrap 并行探活，覆盖过期历史分数）
- 防止震荡（hysteresis 缓冲带 + debounce 防抖）
- 可回放 telemetry（JSONL 独立日志，每事件一行）
- 一键紧急旁路（EmergencyDisableAdaptive，恢复默认配置）

当前系统**做不到**：

- 真正 weighted routing（xray selector dedup 使 tag 重复无效）
- per-request balancing（调度粒度为连接级，非请求级）
- runtime probability shaping（xray 无动态 balancer API）
- transparent QUIC migration（QUIC 连接语义与 TCP 完全不同）
- 全局最优计算（所有流量压单节点会压死该节点）

---

## 关于 OpenAI 评审的整体判断

OpenAI 评审的总体结论是正确的。在以下方面它提供了 Claude v1.0 审计没有覆盖到的真正增量价值：

**采纳 OpenAI 新增的观点**：
- active set hysteresis（迟滞机制）— 这是 v1.0 审计的真实遗漏，影响系统稳定性
- adaptive.log 明确用 JSONL 格式 — 比"机器可读"更具体
- adaptive feature flag（一键旁路）— v1.0 审计没有单独强调
- debounce 从 30s 降到 10~15s — 比"评估是否降低"更可执行
- entropy ≥ 0.5 改为观测指标而非验收标准 — v1.0 表述过于绝对
- 单节点禁用 weighted balancing 而非整个 adaptive framework — v1.0 表述过于绝对

**不采纳或需要补充说明的 OpenAI 观点**：
- "probe bias 是 Claude 没指出的核心问题"：实际上设计文档 §1.2 已明确承认 TTFB 是延迟的"代理指标"，不是真实业务 RTT，这是已知约束，不是遗漏
- "top-K 不需要数学公式化"：同意不要过度严格，但 K 的选取逻辑必须有文档，否则 ActiveSetManager 是黑盒，这两者并不矛盾
- RuntimePolicyApplier 优先级：OpenAI 和 Claude v1.0 一致放在 P3，无分歧

---

## 目录

1. [总体判断](#1-总体判断)
2. [架构层面的根本性风险](#2-架构层面的根本性风险)
3. [已实现模块的具体问题](#3-已实现模块的具体问题)
4. [OpenAI 新增的关键问题](#4-openai-新增的关键问题)
5. [进度文档本身的问题](#5-进度文档本身的问题)
6. [设计文档需要补充或修订的地方](#6-设计文档需要补充或修订的地方)
7. [修订后的完整行动计划](#7-修订后的完整行动计划)
8. [验收标准完整版](#8-验收标准完整版)

---

## 1. 总体判断

**结论：核心机制已经到位，架构路线正确，存在一个 P0 级未验证假设正在支撑整个系统，以及若干需要在上线前修复的工程问题。**

**值得肯定的地方（两份评审一致认可）**：
- 从"测速切换"升级到"持续被动学习"，方向正确
- 建立了 cooldown / EWMA / active set / adaptive policy 完整体系
- 接受"xray-core = data plane，v2rayN C# = control plane"的架构边界，这是项目最重要的成熟点
- 放弃了"C# 完全接管连接"的错误路线
- UI 和持久化超前实现，工程执行力强，技术债务记录清晰无遮掩

**严重风险**：整个系统的核心调度机制（tag duplication 控制 xray weighted selection）**从未被验证过**。进度文档把它列为 P1.1，但已有大量代码围绕这个假设展开。如果 xray 不按重复次数计权，所有调度逻辑都是假的，用户实际上还是在用 xray 默认 round-robin，只是界面上显示了一个看起来很聪明的分数表。

---

## 2. 架构层面的根本性风险

### 2.1 【P0·已解决】tag duplication 假设已验证为不成立

**验证结果（2026-05-21）**

集成测试 `XrayTagDuplicationIntegrationTests` 对 xray v26.3.27 进行了双 observer 验证：
- `[A, A, A, B]` selector, N=1000 请求
- 结果：A ≈ 50%（不是预期的 75%）
- xray 源码确认：`SelectOutbounds()` 对 candidate outbound tags 做 prefix-match + dedup

**结论**：tag duplication weighting **从未生效**。selector `[A, A, A, B]` 被去重为 `[A, B]`，random balancer 在去重后的 candidates 中 uniform random 选择。

**已执行的修复**：
1. 代码：移除 `V2rayAdaptiveService` 中的 tag duplication 逻辑，改为 active-set unique tags
2. 测试：`XrayTagDuplicationIntegrationTests` 保留为 xray selector 行为契约检测器（若 xray 未来改变语义，测试会报警）
3. 文档：设计文档 `CLAUDE-loadbalance.md` 更新为 v4.0，系统重定位为 Adaptive Active-Set Scheduler
4. 架构：整个系统从 "Weighted Adaptive Scheduler" 收敛为 "Adaptive Active-Set Scheduler"

**未来的风险**：xray 版本升级可能改变 selector 语义。集成测试作为永久回归检测器保留在 CI 中。

---

### 2.2 【P1·已解决】ReloadPolicyApplier debounce 已降低，xray 重启耗时已实测

**已执行（2026-05-21）**：

1. ✅ debounce 从 30s 降至 15s（`ReloadPolicyApplier.MinReloadInterval`）
2. ✅ xray 重启耗时已实测：~1.1s（Windows 10, xray v26.3.27, `XrayRestart_ShouldCompleteWithinFiveSeconds`）
3. ✅ 调度响应延迟上界已写入设计文档 §7.2

**调度响应延迟上界（更新后）**：

```
节点质量恶化
  → EWMA 反映：T₁ (5~10s)
  → active set 更新（MonitorActiveSet 检查间隔）：5s
  → debounce 等待：15s（原 30s）
  → xray 重启：~1.1s（实测）
总计上限：~22~27s（原 ~37~42s，缩短约 40%）
```

**连接中断代价**：xray 重启仍会断开所有当前连接（HTTP/2 streams、WebSocket、视频流）。根本解决需要 RuntimePolicyApplier（P3），通过 xray runtime API 动态更新 balancer 实现零中断切换。当前 debounce 15s 是在"响应速度"和"避免频繁重启"之间的合理折衷。

---

### 2.3 【P1·中等】ActiveSetManager top-K 逻辑是黑盒，缺乏设计文档

**现象**

进度文档标注 `ActiveSetManager` 为"设计文档外补充"，职责是"top-K + explorer 活性集追踪"，但没有任何文档说明 K 怎么定、explorer 节点怎么选。

**OpenAI 的观点**：不需要数学最优化，"Top 50% + 1 explorer"就够了。这个判断方向正确——不必过度严格，但 K 的选取逻辑必须被文档化，否则 ActiveSetManager 是黑盒，出问题时无法排查。

**必须补充到设计文档**：

```
K = max(3, ceil(total_nodes × 0.5))   # 至少 3 个，最多占总节点一半
explorer_count = max(1, floor(K × 0.15))  # 约 15%，至少 1 个
explorer 来源 = 分数最低的非 cooldown 节点（给低分节点保持曝光机会）
```

不要过度数学化，但必须文档化。

---

### 2.4 【P1·中等】分数持久化与 Bootstrap 的交互语义未被验证

**现象**

进度文档标注"分数持久化已实现：Bootstrap 前从 ProfileExItem 恢复"，但未明确说明有历史分数时 Bootstrap 是否仍然执行并覆盖历史。

**典型风险场景**：

```
节点 A 上次关机时分数 90（历史最佳）
关机 12 小时后，A 实际已被 GFW 封锁
启动时恢复历史分数 90 → 大量流量命中死节点
```

两份评审（Claude + OpenAI）一致认为：**Bootstrap 必须始终执行，Bootstrap 结果必须覆盖历史分数**。

**必须做的事**：

1. 代码审查确认：有历史分数时，Bootstrap 仍然执行且结果覆盖历史分数
2. 增加分数过期机制：历史分数超过 4 小时强制回退到初始值 50，不信任过期数据
3. 写 unit test 覆盖："恢复历史高分 90 → Bootstrap TCP connect 失败 → 分数降至 1.0"

---

## 3. 已实现模块的具体问题

### 3.1 【P0】FailureCollector：FailureType 无差异化惩罚

两份评审一致认为这是 P0 问题。

**当前问题**：所有 FailureType 都用 `lossRate = 1.0` 更新 EWMA，丢失了语义信息：

- `TlsError`：TLS 配置错误，与节点网络质量完全无关，不应惩罚 EWMA，应触发配置告警
- `ConnectionRefused`：端口不通，几乎肯定是节点问题，应强惩罚
- `Timeout`：可能是 GFW 干扰，也可能是节点慢，中等惩罚
- `UnexpectedEof`：连接中途断开，弱惩罚

**修复**：

```csharp
(double penaltyLoss, double penaltyLatencyMs) GetPenalty(FailureType type,
                                                          NodeState node) =>
    type switch {
        FailureType.Refused       => (1.0,   10_000),
        FailureType.Timeout       => (0.8,   10_000),
        FailureType.NetworkError  => (0.7,   10_000),
        FailureType.UnexpectedEof => (0.4,   node.EwmaLatencyMs * 1.5),
        // TLS 错误：不惩罚 EWMA，触发独立告警，延迟保持不变
        FailureType.TlsError      => (0.0,   node.EwmaLatencyMs),
        _                         => (0.5,   10_000),
    };

// TlsError 独立告警路径
if (type == FailureType.TlsError)
    _alertService.RaiseTlsConfigError(node.Tag);
```

---

### 3.2 【P1】ProbeService：探活策略未明确文档化

设计文档 §5.2 说"运行中完全依赖被动观测，不产生额外流量"，但进度文档说 `ProbeService` 已实现了 cooldown 恢复探活。两者之间的边界不清晰：

- 正常运行时，ProbeService 是否做周期性主动探活？间隔多少？
- 探活是对全部节点还是只对 active set？
- 探活并发上限是多少？50 节点同时探活会产生大量并发 HTTP 请求

**必须明确并写入文档**：

```
探活触发条件（推荐策略）：
  1. Bootstrap 阶段：并行探活所有节点，一次性
  2. Cooldown 恢复：到期前 5s 触发一次 TTFB 探活
  3. 周期性补充（可选）：低分节点（score < 20）每 2 分钟探一次
  4. 正常节点：不主动探活，依赖被动观测

探活并发上限：max(3, ceil(N / 5))，N 为节点总数
```

---

### 3.3 【P1】ScoreLogger：输出目标和格式未定义

`ScoreLogger` 使用 `ILogger`，但 v2rayN 是 Windows GUI 应用，`ILogger` 的 sink 配置决定了日志是否真的被写出去。如果 sink 未配置，ScoreLogger 成为空转任务，出问题时完全无法诊断。

**OpenAI 明确建议使用 JSONL 格式**（而非纯文本），以支持未来的自动分析、issue 回放和 telemetry，这是比"机器可读"更具体的要求，采纳。

**必须做的事**：

1. 配置独立的 `adaptive.log` 文件（不与 xray 日志混合）
2. 格式使用 JSONL，每行一个 JSON 事件：

```json
{"time":"2026-05-21T14:30:00Z","type":"score_snapshot","node":"HK-A","score":87.3,"latencyMs":95,"lossRate":0.01,"cooldown":false}
{"time":"2026-05-21T14:30:05Z","type":"cooldown_enter","node":"US-B","score":12.4,"latencyMs":1820,"lossRate":0.42,"consecutiveFails":3}
{"time":"2026-05-21T14:35:00Z","type":"active_set_change","active":["HK-A","JP-C"],"explorer":["SG-D"],"cooldown":["US-B"]}
```

3. 在 v2rayN 主界面提供"查看 Adaptive 日志"入口（至少是打开文件的按钮）

---

### 3.4 【P1】AdaptiveSchedulerManager：生命周期管理未文档化

**三个未解决的生命周期问题**：

1. **静态单例模式**：代码中出现 `AdaptiveSchedulerManager.Instance.IsRunning`，静态单例阻止依赖注入和单元测试
2. **Profile 切换时**：用户从 GroupA（启用 adaptive）切换到 GroupB（不启用），节点状态是否清空？探活任务是否停止重启？
3. **软件退出时**：探活 HttpClient、后台 Task 的 CancellationToken 是否被正确传播？

**必须做的事**：

1. 移除静态单例，改为 DI 注入（或至少明确记录静态单例的使用范围）
2. 文档化 profile 切换的处理流程（状态清空 → 探活任务重启 → Bootstrap 重新执行）
3. 确认 `IAsyncDisposable` 实现，确保退出时资源正确释放

---

### 3.5 【P2】PerTagProxyTraffic：线程安全问题

`Dictionary<string, (long Up, long Down)>` 在多线程场景下不是线程安全的。`StatisticsXrayService` 和 `ProbeService` 如果并发访问，有数据竞争风险。

**修复**：改为 `ConcurrentDictionary<string, NodeTrafficSnapshot>`，并将 ValueTuple 改为可序列化的 `record`：

```csharp
public record NodeTrafficSnapshot(long UpKbps, long DownKbps, DateTime UpdatedAt);
private readonly ConcurrentDictionary<string, NodeTrafficSnapshot> _traffic = new();
```

---

## 4. OpenAI 新增的关键问题

本节收录 OpenAI 评审中 Claude v1.0 审计未覆盖的真正增量内容。

### 4.1 【P0·新增】Active Set Hysteresis（迟滞机制）

**这是 OpenAI 最有价值的新增观点**，Claude v1.0 审计确实遗漏了这一点。

**问题**：如果 active set 的进出门槛相同（比如 score > 50 进入，score < 50 退出），节点分数在 50 附近震荡时会产生频繁的进出 active set，每次 active set 变化都触发 xray reload，用户体验抖动严重。

**解决方案：迟滞（Hysteresis）**

进入 active set 的门槛高于退出门槛，形成缓冲区：

```csharp
public sealed class ActiveSetManager {
    // 进入 active set 需要更高分数（避免低质量节点轻易进入）
    private const double EntryThreshold = 60.0;
    // 退出 active set 只在分数明显更低时（避免频繁抖动）
    private const double ExitThreshold  = 35.0;

    // 节点当前在 active set 中：只有 score < 35 才退出
    // 节点当前不在 active set 中：需要 score > 60 才进入
    bool ShouldBeActive(NodeState node, bool currentlyActive) =>
        currentlyActive
            ? node.Score >= ExitThreshold    // 已在集合中，维持到 35
            : node.Score >= EntryThreshold;  // 不在集合中，进入需要 60
}
```

**为什么这很重要**：

没有 hysteresis 的系统，一个 score 在 45~55 之间波动的节点会导致 active set 每隔几分钟就变化一次，每次变化都触发 xray reload，用户感受到的是频繁的连接中断，而不是"系统自己会挑节点"。

**建议参数**：Entry=60, Exit=35（缓冲带 25 分）。可以是可配置的。

---

### 4.2 【P0·新增】Adaptive Feature Flag（一键旁路回退）

**OpenAI 的观点**：adaptive system 长期一定会出现边缘问题，必须有"可快速旁路"的机制。

**Claude v1.0 审计没有单独强调这一点**，但确实重要。任何足够复杂的功能都需要一个快速关闭路径，避免出问题时用户只能卸载软件。

**必须实现**：

```csharp
// 全局紧急旁路，不需要重启软件
public void EmergencyDisableAdaptive() {
    _config.AdaptiveSchedulerItem.Enabled = false;
    // 立即停止所有探活任务
    _probeCts?.Cancel();
    // 恢复 xray 默认配置（round-robin 或用户原始配置）
    _ = _policyApplier.RestoreDefaultConfigAsync();
    _logger.LogWarning("Adaptive scheduling emergency-disabled by user.");
}
```

UI 上建议：在主界面或系统托盘菜单提供"关闭 Adaptive（紧急）"选项，一键执行，无需进入设置页面。

---

### 4.3 【P2·新增】Replayable Telemetry（事件可回放）

**OpenAI 的观点**：未来一定会遇到"为什么昨晚疯狂切节点"的问题，需要事件回放能力。

这与 §3.3 的 JSONL 日志格式要求合并处理。JSONL 的每一行是一个完整事件（score snapshot、cooldown 进出、active set 变化），本身就是可回放的 telemetry。

关键是**事件要足够完整**，能够重现调度决策链：

```json
{"time":"...","type":"probe_result","node":"HK-A","ttfbMs":95,"success":true,"probeUrl":"http://cp.cloudflare.com/"}
{"time":"...","type":"ewma_update","node":"HK-A","oldLatency":120,"newLatency":105,"alpha":0.28,"ageSec":4.2}
{"time":"...","type":"score_update","node":"HK-A","oldScore":82.1,"newScore":87.3}
{"time":"...","type":"active_set_change","added":["HK-A"],"removed":[],"reason":"score_crossed_entry_threshold"}
{"time":"...","type":"xray_reload","trigger":"active_set_change","debounceMs":12000,"durationMs":1840}
```

这样的日志允许开发者在事后完整重现调度系统在某个时间段内的所有决策。

---

### 4.4 关于"Probe Bias"的补充说明

**OpenAI 把 probe bias 列为"Claude 没指出的核心问题"，需要澄清**。

设计文档 §1.2 已明确承认 TTFB 是延迟的"代理指标"（proxy metric），不是真实业务 RTT，这是已知的系统性约束，不是遗漏。`cp.cloudflare.com` 的探活结果与 YouTube chunk、HTTP/2 多路复用、WebSocket 的真实延迟存在偏差，这在设计阶段就被接受了，因为在 xray 不暴露 socket 级指标的架构约束下，TTFB via HEAD 是唯一可行的延迟观测手段。

但 OpenAI 的提醒有一个实用价值：**探活 URL 的选择应该尽量接近用户真实业务场景**。

建议：
- 提供多个备选探活目标（`http://cp.cloudflare.com/`、`http://connectivitycheck.gstatic.com/generate_204`）
- 允许用户自定义（已在技术债务列表中，优先处理 ProbeUrl UI 入口）
- 探活结果应该是多目标的平均值，减少单一目标的偶发抖动对 EWMA 的影响

---

## 5. 进度文档本身的问题

### 5.1 "✅ 合理，实际更优"与"必验"的矛盾标注

进度文档 §3 对 tag duplication 偏差的评估是"✅ 合理，实际更优"，同时 §4.D.1 标注"必验"。这两个判断不能同时为真——已评估为合理的事情不需要"必验"；需要"必验"的事情不应该被标注为"✅ 合理"。

建议修改评估状态为：
**"⚠️ 待验证：行为符合预期，但依赖 xray 内部实现细节，需要集成测试通过后才能转为 ✅"**

### 5.2 实现偏差表格缺少影响分级

偏差分析表格没有"影响等级"列，读者无法快速判断哪个偏差是可忽略的工程折衷，哪个是需要优先修复的风险。建议加一列：影响等级（🔴 阻断 / 🟡 需跟进 / 🟢 已接受）。

### 5.3 P1.1 验证任务缺乏明确验证方法

"用流量/日志验证选择概率是否按重复次数倍增"没有给出具体方法。没有明确通过标准的验证任务在实际执行中容易被随意通过。

**必须明确**：
- 工具：构造特定 xray 配置，用 curl/wrk 发 N=1000 请求
- 判定阈值：A 命中率在 70%~80% 之间为通过（95% 置信区间）
- 自动化：这个测试必须可以在 CI 中重复执行，不是一次性人工验证

---

## 6. 设计文档需要补充或修订的地方

### 6.1 架构图必须反映实际实现

设计文档 §3.2 的架构图描述的是"C# AdaptiveDispatcher 做 weighted random"，但实际实现是"xray balancer + tag duplication"。架构图与实现完全不符，必须更新：

```
C# 控制面
  ScoreCalculator → FailureCollector → ActiveSetManager
                                           ↓
                                  GenAdaptiveConfig
                                  （生成带重复 tag 的 balancer selector）
                                           ↓
xray-core（data plane，按重复 tag 做 random，实现加权选择）
                                           ↓
                                    outbound nodes
```

### 6.2 启动序列需要加入实际时间线

```
T=0ms      加载节点配置
           → 有历史分数：恢复到 NodeState（标记为"待验证"状态）
           → 无历史分数：所有节点 Score=50（初始值）
T=0ms      并行 Bootstrap 探活（TCP connect，2s 超时，全局 3s 强制结束）
           → Bootstrap 结果**始终覆盖**历史分数（包括历史高分）
T≤3000ms   Bootstrap 完成
T=3001ms   GenAdaptiveConfig：生成含 tag duplication 的 xray 配置
T=3001ms+  xray 重启（实测耗时 T₃，建议 < 5s）
T≈6~8s     调度生效，流量开始按加权分布
T+30s      第一批 EWMA 数据基本稳定（被动观测开始替代 Bootstrap 初始值）
```

### 6.3 新增：Active Set Hysteresis 参数

```
active set 进入门槛：score > 60
active set 退出门槛：score < 35
缓冲带：25 分（防止震荡触发频繁 reload）
```

### 6.4 新增：Adaptive Feature Flag 规范

```
全局开关：AdaptiveSchedulerItem.Enabled（已有）
紧急旁路：EmergencyDisableAdaptive()，立即停止探活 + 恢复默认配置，无需重启
UI 位置：主界面/托盘菜单，一键可达
```

### 6.5 节点规模上限建议（新增）

| 节点数 | 建议 |
|--------|------|
| < 3 | 禁用 weighted balancing，保留 adaptive health tracking |
| 3~20 | 核心场景，完整功能 |
| 20~50 | 探活并发需要上限控制，建议 max(3, ceil(N/5)) |
| > 50 | 给出警告，建议按地区/用途拆分为多个 group |

---

## 7. 修订后的完整行动计划

> **架构约束**：禁止继续在 weighted routing 上叠 hack。Phase 1 聚焦稳定 adaptive control loop（active set + cooldown + hysteresis + uniform random）。Phase 2 增强 telemetry 和异常检测。Phase 3 仅在真实用户场景证明必要时才评估 runtime policy API / external balancer / true weighted routing。

### P0 — 立即执行（1~3 天，阻断上线的问题）

| # | 任务 | 具体内容 | 验收条件 |
|---|------|---------|---------|
| ~~0.1~~ | **验证 xray tag duplication 行为** ✅ 已完成 | 集成测试 + 源码确认：xray v26.3.27 去重 selector，duplication 无效。系统重定位为 Active-Set Scheduler | 测试保留为行为契约检测器 |
| ~~0.2~~ | **FailureType 差异化惩罚** ✅ 已完成 | `FailureCollector.GetPenalty()`: Refused(1.0), Timeout(0.8), NetworkError(0.7), UnexpectedEof(0.4), TlsError(0.0, no-op early return, skips cooldown) | unit test: `FailureCollectorTests.cs` (9 tests) |
| ~~0.3~~ | **Bootstrap 覆盖历史分数验证** ✅ 已完成 | 代码审查确认：`BootstrapProber.ProbeOneAsync` ALL code paths call `node.UpdateScore()` — 无路径保留旧分数。ScoreCalculator worst-case (5000ms, 1.0) → 1.0 | unit test: `BootstrapAndScorePersistenceTests.cs` (5 tests) |
| ~~0.4~~ | **Active Set Hysteresis** ✅ 已完成 | `ActiveSetManager`: Entry=60, Exit=35, `_currentActiveSet` tracks prior selection. Sticky nodes stay until score < 35; new nodes need >= 60 | unit test: `ActiveSetManagerTests.cs` (10 tests) |
| ~~0.5~~ | **Adaptive Feature Flag 紧急旁路** ✅ 已完成 | `EmergencyDisableAdaptiveAsync()`: sets Enabled=false, calls StopAsync (probes + logging + policy applier disposed) | unit test: `EmergencyDisableAdaptiveTests.cs` (4 tests) |

### P1 — 近期执行（3~7 天，功能完整性）

| # | 任务 | 具体内容 | 验收条件 |
|---|------|---------|---------|
| ~~1.1~~ | **debounce 从 30s 降至 15s** ✅ 已完成 | `ReloadPolicyApplier.MinReloadInterval` = 15s；xray 重启实测 ~1.1s；延迟上界 ~22~27s 已写入设计文档 §7.2 | 调度响应延迟缩短约 40% |
| 1.2 | **ScoreLogger → adaptive.log（JSONL）** | 独立日志文件；JSONL 格式含 score_snapshot、cooldown 事件、active_set_change | 日志文件可找到；格式可直接用 jq 解析；主界面有打开入口 |
| 1.3 | **ActiveSetManager top-K 逻辑文档化** | K 计算公式、explorer 比例、explorer 选取策略写入设计文档 | 设计文档新增章节；代码注释与文档一致 |
| 1.4 | **AdaptiveSchedulerManager 生命周期** | 移除/封装静态单例；文档化 profile 切换处理流程；确认 `IAsyncDisposable` | 无 ObjectDisposedException；切换 group 时探活任务正确重启 |
| 1.5 | **xray 版本兼容性检查** | 启动时验证版本，selector 行为变更检测（集成测试已在 CI 中保留） | 行为变更时告警，不静默失效 |
| 1.6 | **ProbeUrl 暴露到 Settings UI** | 输入框读写 `AdaptiveSchedulerItem.ProbeUrl`，修改后重启 ProbeService | 用户可配置 ProbeUrl |
| 1.7 | **分数过期机制** | 历史分数超过 4h 强制回退到 50 | unit test：加载 5h 前历史分数，验证被重置 |

### P2 — 中期执行（1~2 周，稳固性 + 可观测性增强）

| # | 任务 | 具体内容 | 验收条件 |
|---|------|---------|---------|
| 2.1 | **XrayStatsPoller** | 5s 轮询 `/debug/vars`；高分低吞吐（< 1KB/s）触发补探活 | 高分节点吞吐归零后 10s 内触发探活 |
| 2.2 | **边界情况：1/2 节点处理** | 1 节点禁用 adaptive active-set（uniform random 无意义）；2 节点允许最多 1 个 cooldown | unit test 覆盖 1、2、3 节点场景 |
| 2.3 | **PerTagProxyTraffic 线程安全** | 改为 `ConcurrentDictionary<string, NodeTrafficSnapshot>`（record） | 无数据竞争；类型可序列化 |
| 2.4 | **ProbeService 并发上限 + 压力测试** | 探活并发上限 max(3, ceil(N/5))；50 节点场景压力测试 | 资源占用有文档上界 |
| 2.5 | **Replayable Telemetry 完整事件** | JSONL 事件包含 probe_result、ewma_update、xray_reload 等完整链路 | 能从日志重现任意时间段的调度决策链 |
| 2.6 | **探活多目标支持** | 支持配置多个 ProbeUrl；结果取平均；减少单一目标偶发抖动 | 配置 2 个探活目标时，两者都超时才判定失败 |

### P3 — 长期执行（仅在真实用户场景证明必要时启动）

| # | 任务 | 说明 |
|---|------|------|
| 3.1 | RuntimePolicyApplier | 通过 xray runtime API 实现零中断切换，替代 ReloadPolicyApplier（依赖 xray API 支持） |
| 3.2 | 调度质量指标（熵、P95 延迟） | 每 5 分钟计算并写入日志，作为观测指标（不作为验收标准） |
| 3.3 | UDP/QUIC 独立节点池 | 依赖 RuntimePolicyApplier 完成后实现 |
| 3.4 | 调度决策审计日志 UI | Telemetry 查看器，内嵌到 v2rayN 设置页 |
| 3.5 | 外部 balancer / true weighted routing | **禁止在 P1/P2 阶段实施**。仅在真实用户反馈证明 active-set uniform random 不足时评估 |

---

## 8. 验收标准完整版

### 8.1 核心调度行为（必须全部通过才能上线）

| 测试场景 | 预期行为 | 验证方法 |
|---------|---------|---------|
| 全部节点 cooldown | 选 cooldown 剩余最短节点，不崩溃 | unit test |
| 节点连续 2 次失败 | 进入 cooldown，其他节点接管 | unit test |
| cooldown 节点数达到 1/3 | 第 1/3+1 个失败节点降权而非 cooldown | unit test |
| Bootstrap 发现死节点 | Score=1.0，不自动进 cooldown | unit test |
| 历史分数 90 + Bootstrap 失败 | 分数覆盖为 1.0 | unit test |
| TlsError 失败 | EWMA 不更新，触发独立告警 | unit test |
| ~~tag duplication 加权~~ xray selector 去重行为 | `[A×3, B×1]` selector，1000 请求 A≈50%（证实去重） | integration test（CI 可重复，作为行为契约检测器） |
| active-set 内均匀分配 | active-set 内各节点流量接近均匀 | integration test |
| score 在 45~55 抖动 | active set 不频繁变化（hysteresis 生效） | unit test |
| 紧急旁路触发 | xray 恢复默认配置，adaptive 停止，不崩溃 | integration test |

### 8.2 用户体验指标（可量化，调度质量观测用，不作为阻断条件）

| 指标 | 目标值 | 测量方法 |
|------|--------|---------|
| 节点质量变化响应时间 | ≤ 25s（含 debounce 15s + xray 重启 ~1.1s，实测） | 实测：人为降低节点质量，观察切换时间 |
| 好节点 vs 差节点选中概率比 | ≥ 3:1（score 差 50 分时） | 从 adaptive.log 计算 |
| 冷启动后首次请求成功率 | ≥ 95%（Bootstrap 完成后） | 实测：重启 10 次，记录首次请求结果 |
| active set reload 频率 | < 4 次/小时（正常网络环境） | 从 adaptive.log 统计 xray_reload 事件 |

---

## 附录：优先级总表

| 级别 | # | 问题 | 影响 | 来源 |
|------|---|------|------|------|
| ✅ 已解决 | 0.1 | tag duplication 行为已验证：xray 去重，duplication 无效 | 整个 weighted scheduling 假设不成立 → 系统已重定位为 Active-Set Scheduler | Claude + OpenAI |
| ✅ 已解决 | 0.2 | FailureType 差异化惩罚已实现 | `GetPenalty()` + TlsError no-op early return; 9 unit tests | Claude + OpenAI |
| ✅ 已解决 | 0.3 | Bootstrap 覆盖历史分数已验证 | 代码审查 + ScoreCalculator 测试; 5 unit tests | Claude + OpenAI |
| ✅ 已解决 | 0.4 | Active Set Hysteresis 已实现 | Entry=60, Exit=35, `_currentActiveSet` tracking; 10 unit tests | OpenAI（新增） |
| ✅ 已解决 | 0.5 | EmergencyDisableAdaptiveAsync 已实现 | Sets Enabled=false + StopAsync; 4 unit tests | OpenAI（新增） |
| ✅ 已解决 | 1.1 | ReloadPolicyApplier debounce 30s→15s + xray 重启实测 ~1.1s | 调度响应延迟从 ~40s 降至 ~25s | Claude + OpenAI |
| 🟡 P1 | 1.2 | ScoreLogger 日志输出未定义（需 JSONL） | 出问题时无法诊断，无法回放 | Claude + OpenAI |
| 🟡 P1 | 1.3 | ActiveSetManager top-K 逻辑无文档 | 核心调度决策是黑盒 | Claude |
| 🟡 P1 | 1.4 | AdaptiveSchedulerManager 生命周期不清晰 | profile 切换时资源泄漏风险 | Claude |
| 🟡 P1 | 1.5 | xray 版本兼容性检查缺失 | 上游更新可能静默改变 selector 行为 | Claude |
| 🟡 P1 | 1.6 | ProbeUrl 无 UI 入口 | 用户无法配置，探活目标可能不可靠 | Claude |
| 🟡 P1 | 1.7 | 分数过期机制缺失 | 长时间关机后历史分数无效 | Claude + OpenAI |
| 🟢 P2 | 2.3 | PerTagProxyTraffic 线程安全 | 低概率数据竞争 | Claude |
| 🟢 P2 | 2.2 | 边界情况（1/2 节点）未处理 | 小概率 bug | Claude |
| 🟢 P2 | 2.5 | Telemetry 事件不完整 | 无法回放调度决策链 | OpenAI（新增） |
| 🔵 P3 | 3.1 | RuntimePolicyApplier 缺失 | 切换时有连接中断（可接受的当前折衷） | Claude + OpenAI |
| 🔵 P3 | 3.2 | 调度质量指标（熵、P95）缺失 | 无法量化"adaptive 是否有效" | Claude（P3） |
| 🔵 P3 | 3.5 | 外部 balancer / true weighted routing | 当前禁止实施，仅当 active-set uniform random 被证明不足时评估 | Claude + OpenAI |
| 📋 架构规则 | — | **禁止继续在 weighted routing 上叠 hack** | 架构假设已被证伪，任何新的加权方案必须基于独立验证 | Claude + OpenAI |
