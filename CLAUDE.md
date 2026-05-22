# v2rayN 项目信息文档

## 项目概述

v2rayN 是一个基于 .NET 的跨平台 GUI 客户端，支持多种网络代理协议和核心实现。它可以在 Windows、Linux 和 macOS 上运行，支持 Xray、sing-box 等核心。

## 工程结构

```
v2rayN/
├── .github/                # GitHub 相关配置
├── v2rayN/                 # 主项目目录
│   ├── AmazTool/           # Amazon工具相关
│   ├── GlobalHotKeys/      # 全局热键功能
│   ├── ServiceLib/         # 核心服务库
│   │   ├── Manager/        # 管理器类
│   │   ├── Handler/        # 处理器类
│   │   ├── Models/         # 数据模型
│   │   ├── Enums/          # 枚举定义
│   │   ├── Events/         # 事件系统
│   │   ├── Helper/         # 辅助工具类
│   │   └── Common/         # 通用工具类
│   ├── ServiceLib.Tests/   # 单元测试
│   ├── ServiceLib.UdpTest/ # UDP 测试工具
│   ├── v2rayN/             # 主应用程序
│   │   ├── Views/          # UI 视图
│   │   ├── ViewModels/     # 视图模型（MVVM模式）
│   │   ├── Common/         # 应用程序通用代码
│   │   ├── Converters/     # 数据转换器
│   │   ├── Manager/        # 应用特定管理器
│   │   ├── Resources/      # 资源文件
│   │   └── Base/           # 基础类
│   ├── v2rayN.Desktop/     # 桌面应用相关
│   └── ...
├── package-*.sh            # 打包脚本
└── README.md               # 项目说明文档
```

## 模块结构与功能

### 1. 核心服务库 (ServiceLib)

#### 1.1 管理器模块 (Manager)
- **AppManager**: 应用程序主管理器，负责初始化、配置管理和生命周期控制
- **CoreManager**: 核心进程管理器，负责启动、停止和监控各种代理核心进程
- **ProfileExManager**: 扩展配置管理器
- **StatisticsManager**: 统计管理器，跟踪流量使用情况
- **TaskManager**: 任务管理器
- **CoreInfoManager**: 核心信息管理器
- **GroupProfileManager**: 分组配置管理器
- **PacManager**: PAC文件管理器
- **CertPemManager**: 证书PEM文件管理器
- **ClashApiManager**: Clash API管理器
- **WebDavManager**: WebDAV同步管理器

#### 1.2 处理器模块 (Handler)
- **ConfigHandler**: 配置处理器，处理用户界面和核心配置的转换
- **CoreConfigHandler**: 核心配置处理器
- **SysProxyHandler**: 系统代理处理器，配置系统级代理设置
- **SubscriptionHandler**: 订阅处理器，处理远程订阅配置
- **FmtHandler**: 格式处理器，处理不同协议格式的导入导出
- **AutoStartupHandler**: 自动启动处理器
- **ConnectionHandler**: 连接处理器
- **Builder/CoreConfigContextBuilder**: 核心配置上下文构建器

#### 1.3 数据模型模块 (Models)
- **Configs**: 配置相关模型
  - Config: 主配置类
  - CoreBasicItem: 核心基础配置项
  - TunModeItem: TUN模式配置项
  - GUIItem: GUI相关配置项
  - SystemProxyItem: 系统代理配置项
  - SpeedTestItem: 速度测试配置项
  - 等等...
- **CoreConfigs**: 核心配置模型
  - CoreConfigContext: 核心配置上下文
  - CoreInfo: 核心信息
  - SingboxConfig: Singbox配置
- **Profiles**: 节点配置模型
  - ProfileItem: 节点配置项
  - SubItem: 订阅项
  - RoutingItem: 路由项

#### 1.4 枚举模块 (Enums)
- **EConfigType**: 配置类型枚举（VMess, Shadowsocks, VLESS等）
- **ECoreType**: 核心类型枚举（Xray, sing-box, mihomo等）
- **ETransport**: 传输协议枚举（TCP, WebSocket, gRPC等）
- **ESysProxyType**: 系统代理类型枚举
- **ERuleMode**: 路由规则模式枚举
- **其他业务相关枚举**

#### 1.5 事件系统模块 (Events)
- **AppEvents**: 应用程序事件系统
- **EventChannel**: 事件通道机制

#### 1.6 辅助工具模块 (Helper)
- **SqliteHelper**: SQLite数据库操作辅助类
- **HttpClientHelper**: HTTP客户端辅助类
- **DownloaderHelper**: 下载辅助类

#### 1.7 通用工具模块 (Common)
- **Logging**: 日志记录工具
- **Utils**: 通用工具函数集合
- **JsonUtils**: JSON序列化工具
- **FileUtils**: 文件操作工具
- **ProcUtils**: 进程操作工具
- **QRCodeUtils**: QR码工具
- **YamlUtils**: YAML处理工具

### 2. 主应用程序模块 (v2rayN)

#### 2.1 UI层
- **Views**: 用户界面视图组件
- **ViewModels**: 视图模型（遵循MVVM模式）
- **Converters**: 数据绑定转换器

#### 2.2 应用管理层
- **Manager**: 应用特定的管理器类

#### 2.3 资源层
- **Resources**: 图标、图片等资源文件

### 3. 测试模块 (ServiceLib.Tests)
- **CoreConfig**: 核心配置测试
- **Fmt**: 格式处理器测试

## 核心功能特性

### 1. 多协议支持
- VMess
- VLESS
- Shadowsocks
- Trojan
- Hysteria2
- TUIC
- WireGuard
- SOCKS
- HTTP
- NaiveProxy
- AnyTLS

### 2. 多核心支持
- Xray
- sing-box
- mihomo (Clash Meta)
- hysteria/hysteria2
- tuic
- naiveproxy
- juicity
- brook
- overtls
- shadowquic
- mieru

### 3. 系统集成
- Windows 系统代理设置
- Linux/macOS 系统代理设置
- 全局热键支持
- 自动启动配置
- TUN模式网络接口

### 4. 高级功能
- 路由规则配置
- 订阅管理
- 性能测试（速度和延迟）
- 统计信息
- TUN模式支持
- PAC文件生成
- WebDAV配置同步
- Clash UI界面
- 多语言支持

## 技术栈

- **语言**: C#
- **框架**: .NET 10.0
- **UI框架**: WPF (Windows Presentation Foundation)
- **响应式编程**: ReactiveUI
- **数据库**: SQLite
- **打包工具**: MSBuild
- **版本控制**: Git

## 开发环境要求

- .NET 10.0 SDK
- Visual Studio 或 Visual Studio Code
- Git

## 构建和部署

使用提供的 package-*.sh 脚本进行不同平台的打包。

## 内存管理和性能优化

### 对象生命周期管理
- 使用单例模式管理核心组件实例
- 利用Lazy<T>实现延迟初始化
- 通过IDisposable接口正确释放资源

### 数据库优化
- SQLite数据库用于持久化配置
- 异步数据库操作避免阻塞UI线程
- 批量更新提高性能

### 进程管理
- 使用Windows Job Objects管理子进程生命周期
- 进程间通信优化
- 自动重启机制保证稳定性

## 网络协议支持详解

### 传输协议
- TCP/Raw: 原始TCP传输
- WebSocket: WebSocket传输
- gRPC: gRPC传输
- mKCP: 可靠UDP传输
- HTTPUpgrade: HTTP升级传输
- XHTTP: 扩展HTTP传输

### 安全协议
- TLS: 传输层安全
- Reality: 新一代TLS伪装技术
- XTLS: 扩展TLS

### 特色协议
- WireGuard: 现代化VPN协议
- Hysteria2: 自定义QUIC协议
- TUIC: 基于QUIC的代理协议
- NaiveProxy: 基于Chrome网络栈的代理

## 配置管理系统

### 多层级配置
- 用户界面配置: 存储GUI相关设置
- 核心配置: 生成各核心所需的配置文件
- 节点配置: 管理各个代理节点参数
- 路由配置: 定义流量分发规则

### 配置迁移
- 自动版本升级和配置迁移
- 向后兼容性保障
- 数据库模式演进

## Cloudflare 优选 IP 集成

> **状态：已实现** | 日期：2026-05-19

### 功能概述

Cloudflare 优选 IP 功能从本地探测 Cloudflare CDN 边缘节点的延迟、丢包率和下载速度，自动选出最优 IP 并生成代理节点。

**解决的问题：**
- 默认 Cloudflare IP 段可能在中国大陆被限速或不可用
- 手动测试和筛选 IP 效率极低
- 需要持续追踪 IP 表现，快速响应网络变化

**用户价值：**
1. 点击菜单"Cloudflare优选"一键运行，全自动完成：拉取数据源 → 批量探测 → 评分排序 → 导出节点 → 加入分组
2. 优选出的高质量 IP 节点自动添加到 [CF优选] 分组中，无需手动配置
3. 探测结果自动 POST 到配置的上报地址，形成历史数据库供后续使用
4. 支持实时日志反馈探测进度和结果

### 核心流程（两阶段流水线）

```
[用户点击"Cloudflare优选"菜单]
        │
        ▼
CfBestIpHandler.RunAsync()
        │
        ├─ Phase 1 ─► CfDataFetcher.FetchHistoricalIpsAsync()   # 拉取历史优结果 (HavePostRes)
        │               ├─ 并发拉取所有 HavePostRes + PostUrls
        │               ├─ 反序列化 JSON，按 IP 去重保留最低 score
        │               └─ 按 score 升序返回 TOP 100
        │
        ├─ Phase 1 ─► RunProbeAndExportAsync()                    # 批量探测 + 早停
        │               ├─ CfIpBatchProber: 并发延迟探测 + 串行测速（SpeedPassStop 早停）
        │               ├─ CfIpScorer: 速度达标按速度降序排名，不达标 9999 垫底
        │               ├─ CfResultExporter: 生成 ProfileItem 节点
        │               └─ 自动添加到 [CF优选] 分组
        │
        ├─ [Phase 1 达标 >= TopN 时跳过 Phase 2]
        │
        ├─ Phase 2 ─► CfDataFetcher.FetchGeneralIpsAsync()       # 拉取常规 IP
        │               ├─ IpSetUrls 正则提取（并发）
        │               ├─ DomainsSetUrl DNS 解析（并发）
        │               └─ Cloudflare CIDR 过滤 + 去重
        │
        ├─ Phase 2 ─► RunProbeAndExportAsync()                    # 补足差额 + 早停
        │               └─ speedPassThreshold = TopN - Phase1达标数
        │
        └─ POST ──► PostResultsAsync()                            # 全量结果上报
                        ├─ 过滤无效节点（无 IP/无 colo/score>9999）
                        ├─ JSON 格式匹配 Python 版 (ip/colo/score/lat/loss/source/speed_kb_s/tcp_ms/tls_ms/ttfb_ms/total_ms)
                        └─ 并发 POST 到所有 PostUrls（最多重试 3 次）
```

### 模块职责

| 模块 | 职责 |
|------|------|
| `CfBestIpHandler.cs` | 主流程编排：Phase 1 历史 IP → Phase 2 常规 IP → POST 上报 |
| `CfDataFetcher.cs` | 数据源拉取：HavePostRes JSON 反序列化、IpSetUrls 正则提取、DomainsSetUrl DNS 解析、CF CIDR 过滤去重 |
| `CfIpProber.cs` | 单 IP 探测引擎：TCP+TLS+HTTP 全链路，分层计时（TCP/TLS/TTFB/Total） |
| `CfIpBatchProber.cs` | 批量探测调度：并发延迟（Windows 20/Linux 10）+ 串行测速（SpeedPassStop 早停） |
| `CfIpScorer.cs` | 两轮评分：延迟+丢包淘汰(>20%) → 速度重排名（达标=1,2,3...，不达标=9999） |
| `CfResultExporter.cs` | TOP 结果转为 ProfileItem 节点列表 |
| `CfIpSource` | IP+sourceUrl 数据，贯穿整个管线追踪来源 |

### 探测计时（对标 Python probe_full_path）

| 计时项 | C# 字段 | Python 字段 | 说明 |
|--------|---------|-------------|------|
| TCP 连接 | `TcpMs` | `tcp_ms` | TCP 三次握手耗时 |
| TLS 握手 | `TlsMs` | `tls_ms` | TLS 协商耗时 |
| 首字节 | `TtfbMs` | `ttfb_ms` | HTTP 请求→第一个字节到达 |
| 总耗时 | `TotalMs` | `total_ms` | TCP 连接到下载完成 |
| 评分延迟 | `AvgLatencyMs` | `lat` | = TcpMs + TtfbMs（去尾均值） |
| 下载速度 | `DownloadSpeedKBs` | `download_speed` | = 下载字节 / (TotalMs - TcpMs - TlsMs - TtfbMs) |

### POST JSON 格式

```json
[{
  "ip": "104.26.12.23",
  "colo": "SEA",
  "score": 1,
  "lat": 589.9,
  "loss": 0,
  "source": "https://pingshaisland.top/api/domains/dell20260518",
  "speed_kb_s": 1113.5,
  "tcp_ms": 219.8,
  "tls_ms": 1655.0,
  "ttfb_ms": 211.9,
  "total_ms": 2245.6
}]
```

### 早停策略

| 阶段 | 策略 |
|------|------|
| Phase 1 | 每批 batchSize(10) 个 IP，批次内 SpeedPassStop 早停；全阶段 speedPassThreshold=10 达标即停 |
| Phase 2 | 仅当 Phase 1 < TopN 时执行；speedPassThreshold = TopN - Phase1 达标数；同样批次早停 |
| POST | 过滤 score>9999 / colo 为 UNKNOWN/NONE/NULL 的无效节点

---

## Adaptive 自适应节点调度

> **状态：已实现 P0 + P1（v7.3）** | 日期：2026-05-22
> **设计文档**: `docs/superpowers/specs/CLAUDE-loadbalance.md` (v7.3) | **审计文档**: 已合并至设计文档

### 功能概述

Adaptive Node Scheduler 是一个自适应节点调度系统，自动监控代理节点的健康状态，动态剔除坏节点并在恢复时重新加入。系统定位为 **Adaptive Active-Set Scheduler** — 核心目标是"坏节点自动消失"，不是"精确概率分流"。

**解决的问题：**
- 代理节点被 GFW 干扰或宕机后，用户需要手动切换节点
- 传统手动测速方式无法持续追踪节点质量变化
- GFW 环境下 DNS 污染与节点故障混淆，导致大量误判（v7.3 P1 修复）
- 机场小包加速干扰探活精度，虚假低延迟误导调度（v7.3 P1 修复）

**用户价值：**
1. 坏节点自动从 active set 中移除，用户无感知
2. 节点恢复后自动重新加入调度（四阶段 Recovery Confirmation FSM）
3. 冷启动时通过 Bootstrap 并行探活 + DNS 缓存获得初始分数
4. 大规模外部冲击时自动冻结控制面（Global Instability Freeze），防止自激震荡
5. 支持一键紧急旁路（EmergencyDisableAdaptive），快速回退到默认配置

### 架构总览

```
┌──────────────────────────────────────┐
│ v2rayN (C# control plane — 不进入数据路径) │
│                                      │
│  ScoreCalculator → FailureCollector   │
│       ↓              ↓               │
│  NodeState[] ← CooldownFsm            │
│       ↓                              │
│  ActiveSetManager (top-K + hysteresis)│
│       ↓                              │
│  GenAdaptiveConfig (active-set       │
│    balancer + probe inbounds)        │
└──────────────┬───────────────────────┘
               │ 生成 xray config.json
               ▼
┌──────────────────────────────────────┐
│ xray-core (data plane, 完全黑盒)      │
│ random balancer: active-set 内       │
│ uniform random 选择                  │
└──────────────────────────────────────┘
```

**架构约束**：C# 不进 Data Plane；调度由 xray random balancer 完成；C# 只维护分数 + 管理 active set + 生成配置。xray v26.3.27 确认对 balancer selector 做 prefix-match + dedup — tag duplication 加权无效。

### 核心模块

| 模块 | 文件 | 职责 |
|------|------|------|
| `AdaptiveSchedulerManager` | `AdaptiveSchedulerManager.cs` | 控制面编排器：初始化、启动/停止探活、MonitorActiveSet 循环、紧急旁路、freeze/recovery/DNS 集成 |
| `ScoreCalculator` | `ScoreCalculator.cs` | EWMA 评分：延迟参考上限 2000ms，延迟权重 0.55，丢包权重 0.45，平方放大。**Throughput 禁止进入 Score** |
| `FailureCollector` | `FailureCollector.cs` | 失败事件收集：按 FailureType 差异化惩罚（Refused=1.0, Timeout=0.8, NetworkError=0.7, UnexpectedEof=0.4, TlsError/DNS=no-op）。**v7.3 Freeze gate**：freeze 期间仅更新 EWMA，阻止 cooldown |
| `CooldownFsm` | `CooldownFsm.cs` | 冷却状态机：连续失败 ≥2 触发，FNV-1a hash stable jitter，全局上限 1/3 节点 |
| `ActiveSetManager` | `ActiveSetManager.cs` | 活性集管理：top-K 选择 + hysteresis（Entry=60, Exit=35）+ decision traceability |
| `BootstrapProber` | `BootstrapProber.cs` | 冷启动探活：并行 TCP connect + DNS 缓存解析，2s 超时，全局 3s 截止 |
| `ProbeService` | `ProbeService.cs` | 运行时探活：xray SOCKS5 HTTP 探活，多目标 URL + heavy GET probe（破坏小包加速） |
| `ScoreLogger` | `ScoreLogger.cs` | JSONL telemetry：probe_result / ewma_update / score_snapshot / active_set_change / xray_reload |
| `ReloadPolicyApplier` | `ReloadPolicyApplier.cs` | 自适应 debounce：滑动 1h window（15s/60s/120s 三级） |
| **P0 新增** | | |
| `RecoveryConfirmationFsm` | `RecoveryConfirmationFsm.cs` | 四阶段恢复状态机：ACTIVE→FAILED→RECOVERY_PROBING→STABILITY_VERIFICATION→ACTIVE，指数退避上限 30min |
| `GlobalFreezeController` | `GlobalFreezeController.cs` | 全局冻结：>60% active 节点失败 → 冻结 60s + 120s hysteresis，防自激震荡 |
| `IClock` / `SystemClock` / `FakeClock` | `IClock.cs` | 时间抽象接口，新 FSM 模块注入 IClock 实现确定性测试 |
| **P1 新增** | | |
| `DnsCacheManager` | `DnsCacheManager.cs` | DNS 缓存 confidence 生命周期：300s TTL，N=3 连续失败失效，lazy check-on-use |

*注：P2/P3 新增模块（XrayStatsPoller、RuntimePolicyApplier、SchedulingQualityMetrics、QualityMetricsReporter）未在此列出，详见设计文档。*

### NodeState 关键字段

| 属性 | 说明 |
|------|------|
| `Score` | QoS 分数 [1, 100]，决定 active-set membership，不控制 per-node 流量权重 |
| `EwmaLatencyMs` | time-decayed EWMA 延迟（α 随观测间隔动态调整：0.05 + 0.25 × e^(−Δt/60)） |
| `EwmaLossRate` | time-decayed EWMA 丢包率 |
| `ConsecutiveFailures` | 连续失败计数，≥2 时 CooldownFsm 评估是否进入 cooldown |
| `IsInCooldown` | 是否处于冷却期（cooldown 节点不参与 active set） |
| `HealthState` | v7.2: 健康状态机（Active / Failed / RecoveryProbing / StabilityVerification） |
| `CachedIp` / `DnsCacheConfidence` | v7.3: DNS 缓存 IP + confidence 级别 |

### 启动序列

```
T=0ms     InitializeNodes() — 构建 NodeState, 分配探活 SOCKS5 端口
T=0ms     BootstrapAsync() — RestorePersistedScoresAsync → BootstrapProber 并行 TCP 探活
T≤3000ms  Bootstrap 完成，返回初始 AdaptiveConfig（含 probe inbounds）
T=3001ms  LoadCore — 首次 xray 配置加载（含探活入站）
T≈4~5s    xray SOCKS5 ready（重启实测 ~1.1s）
           StartProbesAsync() — ProbeService + ScoreLogger + MonitorActiveSet 启动
T+15~30s  EWMA 逐步替代 Bootstrap 初始值
```

### Hysteresis 迟滞机制

```
进入 active set 门槛 (Entry): score ≥ 60  // 新节点需较高分数
退出 active set 门槛 (Exit):  score < 35   // 已在集合中需大幅下降才退出
缓冲带: 25 分                                // 防止 score 震荡导致频繁 xray reload

Explorer: 每轮额外选 1 个 ≥35 分的未使用节点给予曝光机会，但不获得 sticky 状态
```

### 系统能力边界

**能做到**：自动淘汰坏节点、自动恢复好节点、动态 active set、自适应学习（EWMA）、冷启动保护（Bootstrap）、防止震荡（hysteresis + debounce）、可回放 telemetry（JSONL）、一键紧急旁路

**做不到**：真正 weighted routing（xray selector dedup）、per-request balancing、runtime probability shaping、transparent QUIC migration

### EmergencyDisableAdaptive（紧急旁路）

```csharp
public async Task EmergencyDisableAdaptiveAsync()
{
    _adaptiveItem.Enabled = false;   // 标记禁用
    await StopAsync();               // 停止探活/日志/policy applier
    // 调用者负责重生成并加载默认 xray 配置
}
```

### 测试覆盖

> 全量测试：329 total（326 pass，3 xray integration tests 需要 xray-core）

| 测试文件 | 测试数 | 覆盖内容 |
|---------|--------|---------|
| `FailureCollectorTests.cs` | 9 | FailureType penalty 值，TlsError no-op，惩罚排序 |
| `BootstrapAndScorePersistenceTests.cs` | 5 | Score floor, worst-case, Bootstrap 覆盖历史, 分数过期 |
| `ActiveSetManagerTests.cs` | 12 | Entry/Exit hysteresis, sticky, oscillation 免疫, explorer, cooldown, 全冷却兜底 |
| `EmergencyDisableAdaptiveTests.cs` | 4 | 幂等性, stop 清空 |
| `RecoveryConfirmationFsmTests.cs` | 23 | P0: 四阶段恢复 FSM, 合法/非法迁移, 指数退避, 完整生命周期 |
| `GlobalFreezeControllerTests.cs` | 21 | P0: freeze 触发/阻塞/解除, hysteresis, escalation, 边界 |
| `DnsCacheManagerTests.cs` | 18 | P1: 缓存 CRUD, confidence 生命周期, TTL 到期, 失效, 线程安全 |
| `DnsAttributionTests.cs` | 8 | P1: DNS 故障零惩罚, no-op, GlobalFreeze 隔离, 混合失败 |
| `FreezeGateTests.cs` | 7 | §11.8: freeze 期间 EWMA 更新/consecutiveFailures blocked |
| `XrayTagDuplicationIntegrationTests.cs` | 2 | xray selector dedup 行为契约（需 xray-core） |
| 其他 10 个测试文件 | ~220 | P2/P3: Probe, Telemetry, Stats, Metrics, Runtime, Boundary |

### 设计文档引用

- **设计文档**: [docs/superpowers/specs/CLAUDE-loadbalance.md](docs/superpowers/specs/CLAUDE-loadbalance.md) — v7.3 完整设计 + P0/P1 代码实现记录
- **测试**: [ServiceLib.Tests/AdaptiveNodeScheduler/](v2rayN/ServiceLib.Tests/AdaptiveNodeScheduler/) (19 test files, 329 tests)

---

## 国际化支持

### 多语言
- 简体中文
- 繁体中文
- 英语
- 波斯语
- 法语
- 俄语
- 匈牙利语

### 本地化适配
- 不同操作系统的UI适配
- 键盘布局和快捷键本地化
- 文化习惯适配