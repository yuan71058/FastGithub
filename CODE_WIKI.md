# FastGithub Code Wiki

> 项目版本：2.1.5 | 目标框架：.NET 7.0 | 许可证：MIT

---

## 1. 项目概述

FastGithub 是一个本地反向代理工具，用于加速 GitHub 访问。它通过拦截 DNS 查询、伪造 TLS 证书、重写 TCP 端口等手段，将用户对 GitHub 相关域名的请求透明地转发到最快的上游服务器，解决 DNS 污染、连接超速、HTTPS 证书不信任等网络问题。

**核心工作原理：**
1. DNS 拦截 —— 将 GitHub 域名的解析结果劫持为 `127.0.0.1`
2. TCP 端口重写 —— 将标准端口（80/443/22/9418）的本地流量重定向到自定义端口
3. HTTPS 反向代理 —— 用自签 CA 证书进行 MITM TLS 终结，然后用自定义 DNS 解析器连接真实上游服务器
4. IP 测速 —— 持续测量所有解析出的 IP 的连接延迟，自动选择最快线路

---

## 2. 解决方案结构

```
FastGithub.sln (8 个项目)
├── FastGithub/                    主程序入口 (exe)
├── FastGithub.Configuration/     配置管理 (class library)
├── FastGithub.DomainResolve/     DNS 解析引擎 (class library)
├── FastGithub.Http/              自定义 HTTP 客户端 (class library)
├── FastGithub.HttpServer/        Kestrel 反向代理服务器 (class library)
├── FastGithub.PacketIntercept/   数据包拦截层 [仅 Windows] (class library)
├── FastGithub.FlowAnalyze/       流量统计分析 (class library)
└── FastGithub.UI/                WPF 桌面界面 (exe, .NET Framework 4.5)
```

### 项目依赖关系

```
FastGithub (主程序)
├── FastGithub.DomainResolve
├── FastGithub.HttpServer
│   ├── FastGithub.Http
│   │   └── FastGithub.Configuration
│   ├── FastGithub.FlowAnalyze
│   │   └── FastGithub.Configuration
│   └── FastGithub.Configuration
├── FastGithub.PacketIntercept [仅 Windows]
│   └── FastGithub.Configuration
└── FastGithub.Configuration

FastGithub.UI (独立进程，通过 HTTP + UDP 与主程序通信)
```

---

## 3. 各模块职责详解

### 3.1 FastGithub — 主程序入口

**类型：** ASP.NET Core Web 应用 (exe)

负责应用启动、主机配置、中间件管道注册和服务生命周期管理。

#### 关键文件

| 文件 | 职责 |
|------|------|
| `Program.cs` | 入口点。设置工作目录、禁用 QuickEdit、构建 WebApplication |
| `Startup.cs` | 配置主机、Kestrel、依赖注入、中间件管道 |
| `ServiceExtensions.cs` | 单实例运行（全局 Mutex）、Windows 服务/Linux systemd 服务安装 |

#### 启动流程

```
Program.Main()
  → 设置工作目录 + 禁用 QuickEdit
  → Startup.ConfigureHost()        # Serilog、systemd/Windows 服务支持
  → Startup.ConfigureWebHost()     # Kestrel 监听器配置
  → Startup.ConfigureConfiguration() # 加载 appsettings/*.json
  → Startup.ConfigureServices()    # DI 注册所有服务
  → Startup.ConfigureApp()         # 中间件管道
  → Run(singleton: true)           # 全局 Mutex 检查 → 启动托管
```

#### 中间件管道

```
请求 → HttpProxyPacMiddleware (自动代理脚本)
     → RequestLoggingMiddleware (请求日志)
     → HttpReverseProxyMiddleware (YARP 反向代理)
     → /flowStatistics 端点 (流量统计)
```

#### Kestrel 监听器配置

| 平台 | 监听端口 | 用途 |
|------|---------|------|
| Windows | 443 (HTTPS) | HTTPS 反向代理 |
| Windows | 80 (HTTP) | HTTP 反向代理 |
| Windows | 22 (SSH) | SSH 反向代理 |
| Windows | 9418 (Git) | Git 协议反向代理 |
| Linux/macOS | 38457 (HTTP) | 前向代理模式 |

---

### 3.2 FastGithub.Configuration — 配置管理

**类型：** Class Library

提供域匹配、TLS SNI 配置、端口分配等全局配置能力。

#### 核心类

| 类 | 职责 |
|----|------|
| `FastGithubOptions` | 原始配置 POCO，绑定自 `appsettings.json` |
| `FastGithubConfig` | 运行时配置门面，提供域名模式匹配和热重载 |
| `DomainConfig` | 每域名配置：TLS SNI 开关/模式、IP 覆盖、超时、目标重写、屏蔽响应 |
| `DomainPattern` | 通配符域名匹配（`*` 匹配除 `.` 外任意字符），按特异性排序 |
| `TlsSniPattern` | TLS SNI 模板引擎，支持 `@domain`/`@ipaddress`/`@random` 变量替换 |
| `ResponseConfig` | 预设 HTTP 响应配置（状态码、Content-Type、Body） |
| `GlobalListener` | 静态端口分配器，扫描系统已用端口，从默认值分配可用端口 |
| `TypeConverterBinder` | 为 `IPAddress`/`IPEndPoint` 注册 TypeConverter，支持 JSON 绑定 |
| `LoggerExtensions` | 基于 `FormattableString` 的延迟格式化日志扩展 |

#### 域名匹配优先级

`SortedDictionary<DomainPattern, DomainConfig>` 按特异性降序排列：
- 精确匹配 `github.com` 优先于通配符 `*.github.com`
- 段数更多的模式优先
- 同段数内，从 TLD 向左逐段比较，通配符排在后面

#### 配置文件示例

```json
{
  "FastGithub": {
    "HttpProxyPort": 38457,
    "FallbackDns": [
      "8.8.8.8:53",
      "1.1.1.1:53"
    ],
    "DomainConfigs": {
      "*.github.com": {
        "TlsSni": true,
        "TlsSniPattern": "@domain"
      },
      "*.googleapis.com": {
        "TlsSni": true,
        "IPAddress": "142.250.80.46"
      }
    }
  }
}
```

---

### 3.3 FastGithub.DomainResolve — DNS 解析引擎

**类型：** Class Library

核心职责：解析 GitHub 相关域名、选择最快 IP、通过持续后台测速保持 IP 列表新鲜。

#### 核心类

| 类 | 职责 |
|----|------|
| `DnscryptProxy` | 管理 dnscrypt-proxy 子进程（加密 DNS 代理） |
| `DnsClient` | DNS 查询客户端，先查 dnscrypt-proxy，再回退到备用 DNS |
| `DomainResolver` | 高层域名解析器，带缓存和测速 |
| `IPAddressService` | TCP 连接延迟测量，按延迟排序 IP |
| `PersistenceService` | 将已解析端点持久化到 `dnsendpoints.json` |
| `DomainResolveHostedService` | 后台服务，每秒执行一次 IP 测速循环 |
| `TomlUtil` | 读写 dnscrypt-proxy 的 TOML 配置文件 |

#### DNS 查询优先级

```
1. dnscrypt-proxy (本地加密 DNS，端口 5533)
2. dnscrypt-proxy (重试一次，确保优先)
3. FallbackDns[0] (如 8.8.8.8)
4. FallbackDns[1] (如 1.1.1.1)
...
```

#### IP 测速机制

```
DomainResolveHostedService (每秒触发)
  → DomainResolver.TestSpeedAsync()
    → 对每个已知端点:
      → IPAddressService.GetAddressesAsync()
        → 合并历史 IP + 新解析 IP
        → TCP 连接测量每个 IP (5s 超时)
        → 按延迟排序，过滤不可达 IP
      → 更新内存缓存
```

#### 缓存策略

| 缓存 | TTL | 用途 |
|------|-----|------|
| DNS 查询结果 | 30s ~ 10min (取自 DNS 记录 TTL) | 避免重复查询 |
| DNS 服务器可用性 | 5 分钟 | 避免频繁检测 |
| IP 连接延迟 (成功) | 5 分钟 | 复用已测结果 |
| IP 连接延迟 (失败-本地) | 1 分钟 | 本地网络问题快速重试 |
| IP 连接延迟 (失败-远端) | 5 分钟 | 远端问题慢速重试 |
| 历史 IP 列表 | 10 分钟 | 保留已知 IP |

---

### 3.4 FastGithub.Http — 自定义 HTTP 客户端

**类型：** Class Library

提供绕过 GFW 的核心能力：自定义 DNS 解析、TLS SNI 欺骗、Handler 生命周期管理。

#### 核心类

| 类 | 职责 |
|----|------|
| `HttpClient` | 面向调用方的 HTTP 客户端，防循环转发，标记 User-Agent |
| `HttpClientFactory` | 按域名+配置创建和缓存 Handler，自动过期管理 |
| `HttpClientHandler` | 核心：DNS 解析 → 原始 TCP 连接 → TLS 握手 (可欺骗 SNI) → 证书验证 |
| `LifetimeHttpHandler` | 带定时器的自销毁 DelegatingHandler |
| `LifetimeHttpHandlerCleaner` | 通过 WeakReference 跟踪，仅在无引用时 Dispose 底层 Handler |
| `LifeTimeKey` | 缓存键 (domain + DomainConfig) |
| `RequestContext` | 请求级数据载体 (IsHttps + TlsSniValue) |

#### 请求处理流程

```
HttpClient.SendAsync()
  → 检查是否已有 FastGithub/1.0 标记 (防循环)
  → 添加 FastGithub/1.0 User-Agent
  → HttpClientHandler.SendAsync()
    → 生成 TlsSniValue (可能是伪造的 SNI)
    → 附加 RequestContext 到请求 Options
    → 将 URI 改写为 http:// (TLS 由 SslStream 手动处理)
    → SocketsHttpHandler.ConnectCallback
      → GetIPEndPointsAsync() (自定义 DNS 解析)
      → ConnectAsync() (尝试每个 IP, 10s 超时)
        → 创建 Socket → NetworkStream
        → 如果 HTTPS: SslStream.AuthenticateAsClient(targetHost=伪造SNI)
        → 自定义证书验证 (支持通配符匹配 + 名称不匹配忽略)
```

#### Handler 生命周期管理

```
HttpClientFactory
  → 创建 LifetimeHttpHandler (首次 10s，后续 100s)
  → 定时器到期 → 替换缓存项，将旧 Handler 入队
  → LifetimeHttpHandlerCleaner (每 10s 轮询)
    → WeakReference.IsAlive == false → 所有引用释放 → Dispose SocketsHttpHandler
```

---

### 3.5 FastGithub.HttpServer — Kestrel 反向代理服务器

**类型：** Class Library

基于 Kestrel + YARP 的反向代理，支持 HTTPS、HTTP、SSH、Git 协议代理。

#### TCP 中间件（连接级）

| 类 | 职责 |
|----|------|
| `HttpProxyMiddleware` | TCP 级解析 HTTP CONNECT / 普通 HTTP 代理请求 |
| `TunnelMiddleware` | CONNECT 隧道双向 TCP 转发 |
| `TcpReverseProxyHandler` | 抽象基类：TCP 反向代理 (ConnectionHandler) |
| `GithubSshReverseProxyHandler` | SSH 代理 (→ github.com:22) |
| `GithubGitReverseProxyHandler` | Git 协议代理 (→ github.com:9418) |

#### TLS 中间件

| 类 | 职责 |
|----|------|
| `TlsInvadeMiddleware` | 检查连接前 2 字节，非 TLS 则设置 FakeTlsConnectionFeature |
| `TlsRestoreMiddleware` | TLS 握手后清理 FakeTlsConnectionFeature |
| `FakeTlsConnectionFeature` | 哨兵对象，绕过 Kestrel HTTPS 中间件 |

**TLS 三明治模式：** Invade → UseHttps (动态证书) → Restore

这使得 HTTPS 端口可以同时处理 TLS 和非 TLS 连接。

#### HTTP 中间件

| 类 | 职责 |
|----|------|
| `HttpProxyPacMiddleware` | 为浏览器提供 PAC 自动代理配置脚本 |
| `RequestLoggingMiddleware` | 记录请求方法、URL、状态码、耗时 |
| `HttpReverseProxyMiddleware` | 核心：使用 YARP `IHttpForwarder` 转发请求到上游 |

#### 证书系统

| 类 | 职责 |
|----|------|
| `CertService` | CA 证书生命周期 + 服务器证书生成和缓存 |
| `CertGenerator` | X509 证书创建（RSA 2048, SHA256） |
| `ICaCertInstaller` | 平台相关 CA 证书安装接口 |
| `CaCertInstallerOfWindows` | Windows 证书存储安装 |
| `CaCertInstallerOfLinuxDebian` | Debian/Ubuntu 证书安装 |
| `CaCertInstallerOfLinuxRedHat` | RHEL/CentOS 证书安装 |
| `CaCertInstallerOfMacOS` | macOS (需手动安装) |

---

### 3.6 FastGithub.PacketIntercept — 数据包拦截层

**类型：** Class Library (仅 Windows)

基于 **WinDivert** 内核级数据包驱动，拦截和修改网络数据包。

#### DNS 拦截

| 类 | 职责 |
|----|------|
| `DnsInterceptor` | 拦截所有出站 UDP:53 DNS 查询，伪造响应指向 127.0.0.1 |
| `DnsInterceptHostedService` | DNS 拦截后台服务编排器 |
| `HostsConflictSolver` | 注释掉 hosts 文件中冲突的 GitHub 域名条目 |
| `ProxyConflictSolver` | 将 GitHub 域名加入系统代理绕过列表 |

**DNS 拦截流程：**
```
应用发起 DNS 查询 (github.com)
  → WinDivert 捕获 UDP:53 数据包
  → DnsInterceptor 解析 DNS 请求
  → 如果匹配配置的域名 → 伪造响应: 127.0.0.1 (TTL 5min)
  → 注入伪造数据包，原始查询被丢弃
  → 刷新系统 DNS 缓存
```

#### TCP 端口重写

| 类 | 重写规则 |
|----|---------|
| `HttpInterceptor` | 端口 80 → `GlobalListener.HttpPort` |
| `HttpsInterceptor` | 端口 443 → `GlobalListener.HttpsPort` |
| `SshInterceptor` | 端口 22 → `GlobalListener.SshPort` |
| `GitInterceptor` | 端口 9418 → `GlobalListener.GitPort` |

**TCP 拦截流程：**
```
应用连接 127.0.0.1:443
  → WinDivert 捕获 loopback TCP 数据包
  → HttpsInterceptor 重写 DstPort: 443 → GlobalListener.HttpsPort
  → 数据包到达 FastGithub 的本地 HTTPS 监听器
  → 响应包: 重写 SrcPort 回 443
```

---

### 3.7 FastGithub.FlowAnalyze — 流量统计

**类型：** Class Library

拦截 Kestrel 传输层 I/O，跟踪字节流动，计算吞吐率。

#### 核心类

| 类 | 职责 |
|----|------|
| `FlowAnalyzer` | 滚动 5 秒窗口的流量统计（ConcurrentQueue + Interlocked） |
| `FlowAnalyzeStream` | Stream 装饰器，每次 Read/Write 回调 OnFlow |
| `FlowAnalyzeDuplexPipe` | IDuplexPipe 装饰器，双向包装 |
| `ListenOptionsExtensions` | Kestrel 中间件：注入传输层流量分析 |
| `FlowStatistics` | DTO：TotalRead/TotalWrite/ReadRate/WriteRate |

#### 流量数据格式

```json
{
  "TotalRead": 1234567,
  "TotalWrite": 890123,
  "ReadRate": 12345.6,
  "WriteRate": 8901.2
}
```

---

### 3.8 FastGithub.UI — WPF 桌面界面

**类型：** WPF 应用 (.NET Framework 4.5)

系统托盘应用，提供三个功能标签页：

| 标签页 | 功能 |
|--------|------|
| 流量 | 实时上下行速率图表 (LiveCharts) |
| 日志 | UDP 接收后端日志 (最多 100 条) |
| 问答 | SSL 证书问题 FAQ (嵌入 HTML) |

#### 通信机制

```
FastGithub.UI
  │ 启动 fastgithub.exe (子进程)
  │
  ├─ HTTP 轮询 → http://localhost/flowStatistics (每秒)
  │   → 获取 FlowStatistics JSON
  │   → LiveCharts 绘制实时图表 (60 秒滚动窗口)
  │
  └─ UDP 接收 → localhost:动态端口
      → 接收 UdpLog JSON 数据报
      → 显示到日志列表
```

#### 托盘行为

- 最小化和关闭按钮均隐藏到托盘
- 左键点击托盘图标恢复窗口
- 右键菜单：检查更新 / 关闭应用

---

## 4. 整体数据流

```
┌─────────────────────────────────────────────────────────────────┐
│                        用户应用 / 浏览器                          │
└────────────────────────────────┬────────────────────────────────┘
                                 │
                                 ▼
┌─────────────────────────────────────────────────────────────────┐
│  [PacketIntercept - DNS]                                         │
│  WinDivert 拦截 UDP:53                                          │
│  → 匹配的域名返回 127.0.0.1                                      │
└────────────────────────────────┬────────────────────────────────┘
                                 │
                                 ▼
┌─────────────────────────────────────────────────────────────────┐
│  [PacketIntercept - TCP] (仅 Windows)                           │
│  WinDivert 重写 loopback TCP 端口                               │
│  → 80/443/22/9418 → FastGithub 本地监听端口                      │
└────────────────────────────────┬────────────────────────────────┘
                                 │
                                 ▼
┌─────────────────────────────────────────────────────────────────┐
│  Kestrel 监听器                                                  │
│                                                                  │
│  ┌─ TLS 三明治 ──────────────────────────────────────────────┐  │
│  │ TlsInvadeMiddleware → UseHttps (动态证书) → TlsRestore    │  │
│  └────────────────────────────────────────────────────────────┘  │
│                                                                  │
│  ┌─ HTTP 中间件 ─────────────────────────────────────────────┐  │
│  │ HttpProxyPacMiddleware (PAC 脚本)                         │  │
│  │ RequestLoggingMiddleware (请求日志)                        │  │
│  │ HttpReverseProxyMiddleware (YARP 转发)                    │  │
│  └────────────────────────────────────────────────────────────┘  │
│                                                                  │
│  ┌─ TCP 中间件 ──────────────────────────────────────────────┐  │
│  │ TunnelMiddleware (CONNECT 隧道)                           │  │
│  │ GithubSshReverseProxyHandler (SSH)                        │  │
│  │ GithubGitReverseProxyHandler (Git)                        │  │
│  └────────────────────────────────────────────────────────────┘  │
└────────────────────────────────┬────────────────────────────────┘
                                 │
                                 ▼
┌─────────────────────────────────────────────────────────────────┐
│  FastGithub.Http (自定义 HTTP 客户端)                           │
│  → DNS 解析 (DomainResolver → DnsClient → DnscryptProxy)       │
│  → TLS SNI 欺骗 (可能使用伪造 SNI)                              │
│  → SslStream 连接到上游                                         │
└────────────────────────────────┬────────────────────────────────┘
                                 │
                                 ▼
┌─────────────────────────────────────────────────────────────────┐
│  上游服务器 (github.com, 最快 IP)                                │
└─────────────────────────────────────────────────────────────────┘
```

---

## 5. 运行方式

### 5.1 Windows 桌面模式

```bash
# 双击运行
FastGithub.UI.exe

# 或命令行
fastgithub.exe
```

### 5.2 Windows 服务模式

```bash
fastgithub.exe start    # 安装并启动 Windows 服务
fastgithub.exe stop     # 卸载并删除 Windows 服务
```

### 5.3 Linux 终端模式

```bash
sudo ./fastgithub
# 设置系统代理为 http://127.0.0.1:38457
```

### 5.4 Linux 服务模式

```bash
sudo ./fastgithub start    # 安装 systemd 服务
sudo ./fastgithub stop     # 卸载 systemd 服务
```

### 5.5 Docker 部署

```bash
docker-compose up -d
```

### 5.6 构建与发布

```bash
# 构建
dotnet build FastGithub.sln -c Release

# 发布 (单文件)
dotnet publish FastGithub/FastGithub.csproj -c Release -r win-x64 --self-contained
```

---

## 6. 依赖项

### NuGet 包 (主程序)

| 包名 | 版本 | 用途 |
|------|------|------|
| `Yarp.ReverseProxy` | 1.1.1 | HTTP 反向代理核心 |
| `DNS` | 7.0.0 | DNS 协议解析 |
| `Tommy` | - | TOML 配置读写 |
| `WindivertDotnet` | - | WinDivert 驱动封装 (仅 Windows) |
| `Serilog.*` | - | 结构化日志 |
| `Microsoft.Extensions.Hosting.Systemd` | 7.0.0-rc | Linux systemd 服务支持 |
| `Microsoft.Extensions.Hosting.WindowsServices` | 7.0.0-rc | Windows 服务支持 |

### NuGet 包 (UI)

| 包名 | 版本 | 用途 |
|------|------|------|
| `LiveCharts.Wpf` | 0.9.7 | 实时图表控件 |
| `Newtonsoft.Json` | 13.0.1 | JSON 反序列化 |

---

## 7. 关键设计决策

### 7.1 TLS SNI 欺骗

请求在 Socket 层以 HTTP 发送，TLS 由 `SslStream` 手动处理。`TargetHost` 使用 `TlsSniPattern` 生成的伪造值（如 `github.com` → `github.com.cdnprovider.com`），绕过基于 SNI 的审查。

### 7.2 Handler 自动过期

`HttpClientFactory` 为每个域名维护 `LifetimeHttpHandler`。首次创建的 Handler 10 秒后过期（快速 DNS 重评估），之后的 Handler 100 秒过期。过期后通过 `WeakReference` 跟踪确保所有引用释放后才真正 Dispose 底层 `SocketsHttpHandler`。

### 7.3 Windows vs Linux 差异

| 特性 | Windows | Linux |
|------|---------|-------|
| DNS 拦截 | WinDivert 内核级 | dnscrypt-proxy |
| TCP 重写 | WinDivert 内核级 | 系统代理配置 |
| 默认监听端口 | 80/443 | 38457 |
| 证书安装 | 自动 (证书存储) | 自动 (update-ca-certificates) |

### 7.4 全局端口管理

`GlobalListener` 在静态构造函数中扫描系统所有已用 TCP/UDP 端口，然后从默认值（Windows: 80/443, Linux: 3880/38443）向上递增分配可用端口。

---

## 8. 安全说明

- 每台主机生成独立的自签 CA 证书（10 年有效期），保存在 `cacert/` 目录
- 服务器证书按域名动态生成（1 年有效期）
- 证书私钥不应泄露给他人
- DNS 查询通过 dnscrypt-proxy 加密
- 代理过程完全在国内完成，不涉及额外流量加密
