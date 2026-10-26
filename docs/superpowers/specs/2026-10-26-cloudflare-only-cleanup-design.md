# Cloudflare Best IP 保留与 Adaptive Scheduler 清理设计

## 目标

以当前官方最新 `upstream/master`（`57f17d59`）为基线，保留此前实现的 Cloudflare Best IP 测速功能，同时彻底移除此前提交的负载均衡与 Adaptive Node Scheduler 功能。

## 范围

保留并移植：

- Cloudflare Best IP 的数据获取、IP 探测、评分、批量探测和结果导出逻辑。
- Cloudflare 测速相关的配置、设置界面、主窗口入口和资源文本。
- Cloudflare 功能运行所需的初始化、基础模型和配置持久化改动。
- Cloudflare 功能对应的设计/使用文档；仅保留与 Cloudflare 功能直接相关的打包脚本或说明。

移除且不移植：

- Adaptive Node Scheduler 全部运行时代码、配置模型、统计/调度接入和核心服务改动。
- 负载均衡菜单、设置项、状态显示、调度相关资源文本和测试。
- Adaptive Scheduler 的设计文档、状态记录、评审记录和专项测试。
- 仅服务于 Adaptive Scheduler 的打包、工作流或说明文件。

## 实施方案

1. 从官方最新 `upstream/master` 创建本工作分支。
2. 按依赖顺序移植 Cloudflare 初始提交 `1579a9a8` 和完善提交 `b4690dcc`，不移植 Adaptive Scheduler 及负载均衡提交。
3. 处理官方最新代码中的冲突：优先保留官方版本；仅将 Cloudflare 功能所需的最小变更合入共享文件。
4. 全局搜索并清理残留的 Adaptive/负载均衡符号、资源键、配置字段、UI 绑定和文档引用；Cloudflare 相关引用必须保留。
5. 运行现有测试和构建校验，重点验证 Cloudflare 配置读写、入口可见性、测速处理链路，以及官方基线未被意外改写。

## 验收标准

- 当前分支基于 `upstream/master` 最新提交，并包含 Cloudflare Best IP 功能。
- 编译通过，相关现有测试通过；若环境限制导致某项无法运行，记录具体原因。
- 代码中不再存在 Adaptive Node Scheduler 的生产代码、配置项、界面、测试和专项文档。
- 代码中不再存在负载均衡入口或其专用资源；Cloudflare 测速入口和必要配置仍可用。
- Git 差异只包含官方最新基线与 Cloudflare 功能所需内容，不包含旧负载均衡实现。

## 提交策略

设计文档单独提交；功能移植、残留清理和验证修复按可回滚的逻辑阶段提交，并在最终结果中报告提交记录与验证结果。
