# Document Architecture Review — v7.5

**日期**: 2026-05-23
**来源**: OpenAI 外部评审
**主题**: CLAUDE-loadbalance.md 信息层级塌陷与文档重组方案

---

## 核心诊断

当前 `CLAUDE-loadbalance.md` 同时承担了以下角色：
- 产品设计文档
- 系统架构文档
- Runtime 约束说明
- 审计报告
- 评审记录
- 实现状态追踪
- 未来路线图
- 哲学讨论
- 历史版本合并记录

**这是最大的问题**——信息层级塌陷。尤其评审内容（OpenAI/DeepSeek/Claude）已侵入主 specification，导致 spec 与 discussion 边界消失，Claude Code 难以判断哪些是 authoritative invariant、哪些是 reasoning history。

---

## 重组方案

### A. Core Specification（唯一权威）

**文件**: `adaptive-scheduler-spec.md`

只保留 authoritative content：
- System Philosophy
- Runtime Constraints
- Control Plane vs Data Plane
- State Machine（含合法/非法迁移）
- Active Set Lifecycle
- Reload Lifecycle
- Mutation Authority（事件优先级 + 冲突解决）
- Failure Handling
- Recovery Semantics
- Scoring Formula
- Global Instability Freeze
- Data Structures
- Module Contracts
- Telemetry
- UI Ownership
- Non-Goals
- Known Runtime Limits

**禁止出现在 spec 中**：谁提出的、历史争论、推理过程、评审 attribution、版本历史长篇叙述

### B. Engineering Status（实现状态）

**文件**: `adaptive-scheduler-status.md`

- 当前 P0/P1/P2 状态
- 已实现模块清单
- 测试覆盖
- 已知风险
- Runtime evidence 占位
- TODO

### C. Architecture Review Archive（评审归档）

**目录**: `reviews/`

- `openai-review-v7.4.md` — v7.4 系统架构评审（Mutation Authority、Config vs Runtime State）
- `document-architecture-review-v7.5.md` — 本文档（文档架构评审）
- 历史评审：内容从 CLAUDE-loadbalance.md 的 §2、§3、§14、§15、§18、§22 提取

**原则**：这里才放谁说了什么、为什么这样改、reasoning history、架构讨论、分歧

### D. Runtime Evidence（运行时观测）

**文件**: `runtime-observations.md`

- reload frequency
- cooldown churn
- evening instability
- active-set lifetime
- freeze rate
- recovery oscillation
- false cooldown ratio

**原则**：这是 P2 决策的依据，不是靠讨论而是靠数据

---

## 原文档处理

`CLAUDE-loadbalance.md` 作为**历史归档**保留，不再作为 active spec。顶部添加指向新文件的索引链接。

---

## 关键约束

1. **Spec 与 Discussion 彻底分离**：spec 定义系统，review 记录讨论历史，二者不混
2. **Status 与 Spec 分离**：spec 描述"应该是什么"，status 描述"现在是什么状态"
3. **Evidence 独立**：runtime observations 独立于 spec 和 status，作为决策输入
4. **禁止在 spec 中出现**："这是 XXX 提出的"、"XXX 漏掉了"、"XXX 修复了"——这些全部移到 reviews/
