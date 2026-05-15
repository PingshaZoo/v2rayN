# Cloudflare 优选 IP 集成 — 设计文档

> 日期：2026-05-15 | 状态：已实现

## 概述

将 CloudflareBestIP 项目的 Cloudflare CDN IP 优选探测功能迁移到 v2rayN，以 C# 重写探测逻辑。

## 用户故事

1. 用户在 OptionSetting 设置界面配置 CF 优选参数（必填项 + 有默认值的可选项），点击保存
2. 用户点击主菜单 "Cloudflare优选" 按钮触发探测
3. 消息栏实时显示探测进度和日志
4. 探测完成后，TOP N 优选 IP 按用户选择的协议模板自动生成为节点，添加到 "CF优选" 分组

## 设计决策

| 决策 | 选择 |
|------|------|
| 菜单名称 | Cloudflare优选 |
| 配置UI | 集成到 OptionSettingWindow 作为新 Tab |
| 实现方式 | C# 重写探测逻辑（socket + SslStream + HttpClient） |
| 节点协议 | 用户下拉选择，首发 VLESS/VMess/Trojan + WS + TLS |
| 节点分组 | 自动创建 "CF优选" 分组（PolicyGroup） |
| 阿里云DNS | 暂不迁移 |
| 运行反馈 | 实时日志输出到消息栏 |
| 数据持久化 | guiNConfig.json（Config 体系，CfBestIpItem 序列化到主配置文件） |

## 实际实现变更记录

以下设计与原始构思有差异，均遵循项目现有模式或为合理简化：

| 差异 | 说明 |
|------|------|
| `CfBestIpItem` 合并到 `ConfigItems.cs` | 项目所有配置项模型（SpeedTestItem、HysteriaItem 等）均在同一文件，保持一致 |
| 节点插入用 `ConfigHandler.AddServerCommon` | 该方法正确处理 IndexId 生成、字段校验、排序、持久化，优于直接 SQLite 操作 |
| 增量批次测速简化 | Python 版"每100个IP触发区域Top5测速"逻辑较复杂，C# 首版速度测试独立于延迟探测，单次执行 |
| 额外支撑文件 | 新增 `Global.cs`（默认URL/CIDR）、`ConfigHandler.cs`（初始化）、`GlobalUsings.cs`（命名空间）、`ResUI.Designer.cs`（资源访问器）、`OptionSettingWindow.xaml.cs`（UI绑定）、`ResUI.zh-Hans.resx`（中文翻译） |
| 新增 UUID / WS Path 配置 | 节点生成所需 UUID 和 WebSocket 路径由用户配置，替代随机 UUID 和硬编码 Path |
| MainWindow.xaml.cs 命令绑定 | 首次实现遗漏 `BindCommand`，已补充 `CloudflareBestIpCmd → menuCloudflareBestIp` |

## 新增文件

```
ServiceLib/Handler/CloudflareBestIP/
  CfBestIpHandler.cs       — 主流程编排（4步流水线）
  CfDataFetcher.cs         — 数据源拉取、CF CIDR 过滤去重（手动位运算CIDR匹配，无外部依赖）
  CfIpProber.cs            — 单 IP TCP+TLS+HTTP 探测（Socket+SslStream，Chrome128指纹）
  CfIpBatchProber.cs       — 多线程批量探测 + 进度回调（自适应并发：Win20/Lin10）
  CfIpScorer.cs            — 评分排序（延迟加权 + 丢包惩罚 + 速度排名）
  CfResultExporter.cs      — 结果 → ProfileItem 节点（VLESS/VMess/Trojan 三种模板）
```

## 修改文件

```
ServiceLib/Models/Configs/Config.cs                    — 新增 CfBestIpItem 属性
ServiceLib/Models/Configs/ConfigItems.cs               — 新增 CfBestIpItem 类（24 字段）
ServiceLib/Handler/ConfigHandler.cs                    — 初始化 CfBestIpItem 默认值
ServiceLib/Global.cs                                   — 新增 4 组默认常量（URL/CIDR）
ServiceLib/GlobalUsings.cs                             — 新增 CloudflareBestIP 命名空间
ServiceLib/Resx/ResUI.resx                             — 新增英文字符串（21 条）
ServiceLib/Resx/ResUI.zh-Hans.resx                     — 新增中文字符串（21 条）
ServiceLib/Resx/ResUI.Designer.cs                      — 新增 C# 属性访问器（21 个）
ServiceLib/ViewModels/MainWindowViewModel.cs           — 新增 CloudflareBestIpCmd + RunCloudflareBestIpAsync
ServiceLib/ViewModels/OptionSettingViewModel.cs        — 新增 16 个 Cf* 属性 + init + save
v2rayN/Views/MainWindow.xaml                           — 新增 Cloudflare优选 顶部菜单项
v2rayN/Views/OptionSettingWindow.xaml                  — 新增 CF 设置 Tab（14 个控件）
v2rayN/Views/OptionSettingWindow.xaml.cs               — 新增 ItemsSource + 15 条 ReactiveUI 绑定
```

## 数据流（实际实现）

```
用户点击"Cloudflare优选"菜单
  │
  ▼
CfBestIpHandler.RunAsync(config, onUpdate)
  │
  ├─(1) CfDataFetcher.FetchAllIpsAsync()
  │     ├─ 拉取 HavePostRes (Priority-1) — 历史优结果
  │     ├─ 拉取 IpSetUrls (Priority-2) — 第三方IP库（并发）
  │     ├─ 拉取 DomainsSetUrl → DNS解析 (Priority-3)
  │     ├─ 从 Cloudflare API 获取官方 IPv4 CIDR
  │     └─ 去重 + CIDR 过滤 → 返回最终 IP 列表
  │
  ├─(2) CfIpBatchProber.ProbeAllAsync(ips, progressCallback)
  │     ├─ SemaphoreSlim 并发控制（Win:20, Lin/Mac:10）
  │     ├─ CfIpProber.ProbeSingleIpAsync() 两阶段探测：
  │     │   Phase 1 (延迟探测，重复 ProbeRepeat 次)：
  │     │   ├─ TCP 连接 + TLS 握手（SslStream，SNI=源站域名）
  │     │   ├─ GET /cdn-cgi/trace（keep-alive，无 Accept-Encoding）→ 提取 colo
  │     │   └─ GET OriginTestPath（close，含 Accept-Encoding）→ 测延迟
  │     │   Phase 2 (速度测试，独立新连接，单次)：
  │     │   └─ GET OriginSpeedTestPath（close，15s 超时）→ 测下载速度
  │     └─ 进度回调 + 前5个成功IP实时详情 → 消息栏
  │
  ├─(3) CfIpScorer.ScoreAndRank(results)
  │     ├─ TCP/TLS 失败率 > 20% → score = 999999 淘汰
  │     ├─ 延迟 × WeightLatency + 丢包 × Penalty × WeightLoss
  │     ├─ 速度达标 (>= LowestSpeed) → 按速度降序排名 (1,2,3...)
  │     ├─ 速度不达标 → score = 9999 垫底
  │     └─ 返回 score 升序 TOP N
  │
  ├─(4) CfResultExporter.ExportAsProfileItems(topResults)
  │     └─ 按 SelectedProtocol 模板生成 ProfileItem：
  │         ├─ VLESS: address=优选IP, port=443, network=ws, security=tls, sni=源站域名
  │         ├─ VMess: 同上 + alterId=0, vmessSecurity=auto
  │         └─ Trojan: 同上 + 无额外字段
  │
  └─(5) ConfigHandler.AddServerCommon(config, node) × N
        ├─ 自动生成 IndexId (Guid)
        ├─ 字段校验 + 排序
        ├─ 插入 SQLite
        └─ 查找/创建 "CF优选" PolicyGroup → 更新 ChildItems
```

## 探测逻辑（对标 Python 版本）

从 Python 版本对标移植（首版简化增量批次测速）：

| 特性 | Python 版本 | C# 版本 |
|------|------------|---------|
| IP 优先级 | 三级：HavePostRes → IpSetUrls → Domains | ✅ 相同 |
| 探测模式 | full / edge | ✅ full（后续可加 edge） |
| 评分公式 | 延迟加权 + 丢包惩罚 + 速度排名 | ✅ 相同 |
| TCP/TLS 淘汰 | 失败率 >20% 淘汰 | ✅ 相同 |
| CF CIDR 过滤 | 官方 API + 兜底列表 | ✅ 相同 |
| HTTP 指纹 | Chrome 128 请求头 | ✅ Chrome 128 UA + 标准头 |
| 去尾均值 | ≥3样本去尾 | ✅ CalculateTrimmedMean |
| DNS 解析链 | DoH → dig → nslookup | System.Net.Dns（简化） |
| 增量批次测速 | 每100个IP触发区域Top5 | 首版简化：延迟探测后单独速度测试 |
| colo 缓存 | 同IP只取一次 | ✅ 首次取得后传入跳过 trace |
| HTTP 指纹 | Chrome 128 双模式（trace 无 Accept-Encoding / 下载含 Accept-Encoding） | ✅ 独立 `_traceHeaders` / `_downloadHeaders` |
| 连接管理 | trace=keep-alive, 文件=close | ✅ 同 Python |

## 配置项对照（config.py ↔ CfBestIpItem）

| config.py | CfBestIpItem | 类型 | 默认值 | 必填 |
|-----------|-------------|------|--------|------|
| POST_URLS | PostUrls | List\<string\> | Global 默认 | - |
| DOMAINS_SET_URL | DomainsSetUrl | string | - | ✅ |
| HAVE_POST_RES | HavePostRes | List\<string\> | Global 默认 | - |
| IP_SET_URLS | IpSetUrls | List\<string\> | Global 默认 | - |
| SLEEP_INTERVAL | SleepInterval | int | 1 | - |
| TIMEOUT | Timeout | int | 2 | - |
| PROBE_REPEAT | ProbeRepeat | int | 2 | - |
| PROG_INTERVAL | ProgInterval | int | 10 | - |
| TOP_N | TopN | int | 10 | - |
| LOWEST_SPEED | LowestSpeed | int | 1000 | - |
| WEIGHT_LATENCY | WeightLatency | double | 1 | - |
| WEIGHT_LOSS | WeightLoss | double | 1 | - |
| LOSS_PENALTY_MS | LossPenaltyMs | double | 3000 | - |
| PROBE_MODE | ProbeMode | string | "full" | - |
| ORIGIN_SNI_LIST | OriginSniList | List\<string\> | - | ✅ |
| ORIGIN_TEST_PATH | OriginTestPath | string | - | ✅ |
| ORIGIN_SPEED_TEST_PATH | OriginSpeedTestPath | string | - | ✅ |
| ORIGIN_VERIFY_CERT | OriginVerifyCert | bool | false | - |
| CF_DEFAULT_IPV4_CIDRS | CfDefaultIpv4Cidrs | List\<string\> | Global 默认 | - |
| (新增) | SelectedProtocol | string | "vless" | - |
| (新增) | Uuid | string | - | ✅ |
| (新增) | WsPath | string | "/" | - |

## 已知问题与修复记录

### 2026-05-15：探测逻辑修复

| 问题 | 根因 | 修复 |
|------|------|------|
| colo 始终为 `?` | trace 请求包含 `Accept-Encoding: gzip`，CF 压缩响应后 `ExtractColo` 无法解析 | trace 使用独立 `_traceHeaders`（不含 Accept-Encoding） |
| latency 异常低（~0.2ms） | trace 请求 `Connection: close` 导致服务器关闭连接，后续 test 请求失败，latency 回退为 TCP 握手时间 | trace 改用 `Connection: keep-alive` |
| speed 始终为 0 | 同上，连接已死 + TLS 在已知 colo 后被跳过 → 纯 TCP 发 HTTP 到 TLS 端口失败 | 速度测试独立新连接 + TLS 始终执行 |
| 菜单点击无效 | `MainWindow.xaml.cs` 遗漏 `BindCommand(CloudflareBestIpCmd, menuCloudflareBestIp)` | 补充绑定 |
| UUID 随机 / Path 硬编码 | `CfResultExporter` 用 `Guid.NewGuid()` 和 `Path = "/"` | 新增 Uuid / WsPath 配置字段，统一由用户设置 |
