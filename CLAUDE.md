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