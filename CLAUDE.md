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

## 新功能：Cloudflare 优选 IP 集成

> **状态：设计阶段（未实现）** | 日期：2026-05-15

### 需求概述

将 [CloudflareBestIP](../CloudflareBestIP/) 项目的功能迁移集成到 v2rayN 中，以 C# 重写探测逻辑，完全融入 .NET 生态。

**核心用户故事：**
1. 用户在设置界面配置 Cloudflare 优选参数（有默认值和必填项），点击保存
2. 点击菜单"Cloudflare优选"按钮运行探测
3. 探测完成后，优选出的 IP 节点自动添加到 v2rayN 的"CF优选"分组中

### 已决策的设计方案

| 决策项 | 选择 | 说明 |
|--------|------|------|
| 菜单名称 | **Cloudflare优选** | 主窗口菜单栏新增 |
| 配置UI | **集成到设置窗口** | 作为 OptionSettingWindow 的一个 Tab 页 |
| 实现方式 | **C# 重写探测逻辑** | 方案一：不依赖外部 Python 运行时 |
| 节点协议 | **下拉选择，首发 VLESS+WS+TLS** | 后续扩展其他协议和配置 |
| 节点分组 | **自动创建"CF优选"分组** | 运行后自动创建并添加节点 |
| 阿里云DNS同步 | **暂不需要** | 后续按需添加 |
| 运行反馈 | **实时日志+进度** | 通过消息/日志区域显示探测进度 |
| 数据持久化 | **配置存入 guiNConfig.json** | 复用 Config/ConfigHandler 体系 |

### 目标架构（方案一详述）

```
ServiceLib/
├── Handler/
│   └── CloudflareBestIP/                    # 新增模块（全新子目录）
│       ├── CfBestIpHandler.cs               # 主流程编排
│       ├── CfDataFetcher.cs                 # 数据源拉取（多URL并发、CIDR过滤去重）
│       ├── CfIpProber.cs                    # 单IP探测（TCP+TLS+HTTP，对标Python脚本）
│       ├── CfIpBatchProber.cs               # 多线程批量探测调度（并发控制、增量批次测速）
│       ├── CfIpScorer.cs                    # 评分排序（延迟+丢包+速度综合评分）
│       ├── CfResultExporter.cs              # 将TOP结果转为 ProfileItem 节点列表
│       └── CfBestIpConfig.cs                # 配置模型类
├── Models/Configs/
│   └── Config.cs                            # 修改：新增 CfBestIpItem 属性
│   └── ConfigItems.cs                       # 修改：新增 CfBestIpItem 类定义
├── ViewModels/
│   └── OptionSettingViewModel.cs            # 修改：新增CF优选配置属性
│   └── MainWindowViewModel.cs               # 修改：新增菜单命令
└── Resx/
    └── ResUI.resx / ResUI.en.resx           # 修改：新增中英文字符串

v2rayN/v2rayN/
├── Views/
│   ├── OptionSettingWindow.xaml             # 修改：新增CF优选Tab页
│   └── MainWindow.xaml                      # 修改：新增"Cloudflare优选"菜单项
```

### 模块职责定义

| 模块 | 职责 | 依赖 |
|------|------|------|
| `CfBestIpHandler` | 编排完整流程：拉数据→批量探测→评分→导出节点→添加分组 | DataFetcher, BatchProber, Scorer, Exporter, ProfileExManager |
| `CfDataFetcher` | 从多个 URL 拉取 IP 列表和域名列表，Cloudflare CIDR 过滤去重，按优先级合并 | HttpClient, Config |
| `CfIpProber` | 单个 IP 的 TCP+TLS+HTTP 探测（full模式：trace+延迟+速度；edge模式：仅trace） | Socket, SslStream |
| `CfIpBatchProber` | 线程池并发调度，进度回调，增量批次测速（每N个IP触发区域Top5测速） | IpProber, ThreadPool |
| `CfIpScorer` | 延迟+丢包+速度综合评分，TOP N 排序 | 无外部依赖 |
| `CfResultExporter` | 将优选结果组装为 `ProfileItem` 节点列表（按用户选择的协议模板） | ProfileItem, Config |
| `CfBestIpConfig` | 配置模型：探测参数、数据源URL、源站配置、协议模板等 | 无 |

### 配置项设计（对标 config.py）

**必填项：**
- `OriginSniList` — 源站域名列表（SNI + Host），对应原 `ORIGIN_SNI_LIST`
- `OriginTestPath` — 延迟测试文件路径，对应原 `ORIGIN_TEST_PATH`
- `OriginSpeedTestPath` — 速度测试文件路径，对应原 `ORIGIN_SPEED_TEST_PATH`
- `DomainsSetUrl` — 域名列表 API 地址，对应原 `DOMAINS_SET_URL`
- `PostUrls` — 探测结果上报地址列表，对应原 `POST_URLS`

**有默认值的可选项：**
- `HavePostRes` — 历史优质结果 API 列表（Priority-1）
- `IpSetUrls` — 第三方 IP 数据源 URL 列表（Priority-2）
- `SleepInterval` — 探测间隔（秒），默认 1
- `Timeout` — 单次探测超时（秒），默认 2
- `ProbeRepeat` — 每个 IP 重复探测次数，默认 2
- `ProbeMode` — 探测模式 "full" 或 "edge"，默认 "full"
- `TopN` — 最终输出 TOP N，默认 10
- `LowestSpeed` — 最低速度阈值 KB/s，默认 1000
- `WeightLatency` / `WeightLoss` / `LossPenaltyMs` — 计分权重
- `OriginVerifyCert` — 是否验证 TLS 证书，默认 false
- `CfDefaultIpv4Cidrs` — Cloudflare CIDR 兜底列表
- `SelectedProtocol` — 生成的节点协议类型，默认 VLESS+WS+TLS

### 数据流

```
[用户点击"Cloudflare优选"菜单]
        │
        ▼
CfBestIpHandler.RunAsync()
        │
        ├─(1)─► CfDataFetcher.FetchAllAsync()
        │         ├─ 并发拉取 HavePostRes (Priority-1)
        │         ├─ 并发拉取 IpSetUrls (Priority-2)
        │         ├─ 拉取 DomainsSetUrl → DNS解析 → CF CIDR过滤
        │         └─ 去重合并 → 返回 IP 列表
        │
        ├─(2)─► CfIpBatchProber.ProbeAllAsync(ips, progressCallback)
        │         ├─ 线程池并发探测（每IP多次）
        │         ├─ 实时回调进度到消息栏
        │         └─ 增量批次测速 → 返回探测结果列表
        │
        ├─(3)─► CfIpScorer.ScoreAndRank(results)
        │         └─ 返回 TOP N
        │
        ├─(4)─► CfResultExporter.ExportAsProfileItems(topResults)
        │         └─ 使用用户选择的协议模板生成节点
        │
        └─(5)─► ProfileExManager.AddProfileItems(nodes, groupName: "CF优选")
                  └─ 自动创建分组并添加节点
```

### 探测逻辑要点（对标 Python 版本）

1. **IP优先级**：历史优结果(HavePostRes) → 第三方IP库(IpSetUrls) → 域名解析(DomainsSetUrl)
2. **两种探测模式**：full（TCP+TLS+HTTP全链路） / edge（仅trace取colo）
3. **评分公式**：延迟加权 + 丢包惩罚 — TCP/TLS失败率>20%直接淘汰
4. **增量批次测速**：每100个IP触发一次，三区域各取Top5测速
5. **Cloudflare CIDR过滤**：从CF官方API下载IPv4 CIDR，过滤非官方IP
6. **HTTP指纹**：统一Chrome请求头伪装

### 待决策/待细化

- [ ] OptionSettingWindow 中 CF 优选 Tab 页的具体布局
- [ ] 协议模板扩展（VMess+WS+TLS, Trojan+TLS 等）
- [ ] 探测结果的历史记录/日志查看
- [ ] 是否需要支持 edge 模式（首版只做 full）
- [ ] 并发线程数的默认值（对标 Python: Windows 20, Linux 10, macOS 10）

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