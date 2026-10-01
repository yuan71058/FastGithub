# FastGithub

![Version](https://img.shields.io/badge/version-2.3.1-blue)
![Platform](https://img.shields.io/badge/platform-Windows%20%7C%20Linux%20%7C%20macOS-informational)
![Framework](https://img.shields.io/badge/.NET-7.0-512BD4)
![UI](https://img.shields.io/badge/UI-WPF%20(net45)-9E9E9E)
![License](https://img.shields.io/badge/license-MIT-green)

GitHub 加速神器：解决 GitHub 打不开、头像与 Releases 无法加载、`git clone` / `pull` / `push` 失败等问题。

无需在浏览器里手动设置代理，Windows 下通过内核级数据包拦截实现**透明加速**，Linux / macOS 下以本地正向代理方式工作。

---

## 目录

- [解决的问题](#解决的问题)
- [工作原理](#工作原理)
- [功能特性](#功能特性)
- [快速开始](#快速开始)
- [端口与路径速查](#端口与路径速查)
- [配置说明](#配置说明)
- [桌面界面](#桌面界面)
- [注意事项（重要）](#注意事项重要)
  - [Windows 驱动加载](#windows-驱动加载-windivert)
  - [CA 证书与流量安全](#ca-证书与流量安全)
  - [系统代理与 hosts 冲突](#系统代理与-hosts-冲突)
- [常见问题](#常见问题)
- [构建与开发](#构建与开发)
- [免责声明与许可证](#免责声明与许可证)
- [更新日志](#更新日志)

---

## 解决的问题

| 症状 | 原因 | FastGithub 的处理 |
|------|------|------------------|
| github.com 打不开、超时 | DNS 污染 / 解析到不可达 IP | 加密 DNS + 多 IP 测速，自动选最快线路 |
| 头像、js/css 加载不出 | raw / assets 域名被干扰 | 域名级代理 + Google CDN 资源替换 |
| Releases 上传下载失败 | 大文件连接被重置 | 连接级隧道转发 + 持续 IP 测速切换 |
| `git clone` 慢或失败 | HTTPS / SSH / Git 协议受阻 | 同时代理 443 / 22 / 9418 |
| 证书不受信任 | 本地自签 CA | 首次运行生成 CA 并自动安装到系统信任库 |

---

## 工作原理

```
应用 / 浏览器
   │
   ├─[Windows] DNS 拦截：WinDivert 捕获 UDP:53，匹配域名 → 伪造 127.0.0.1 响应
   ├─[Windows] TCP 重写：WinDivert 改写 loopback 端口 80/443/22/9418 → 本地监听端口
   │
   ▼
Kestrel 反向代理（TLS 中间人 + YARP 转发 + SSH/Git 隧道）
   │
   ▼
自定义 HttpClient（加密 DNS 解析 → 最快 IP → TLS SNI 处理 → 连接上游）
   │
   ▼
上游服务器（github.com 等，最优 IP）
```

**IP 选路**：后台服务每秒对已知 IP 做 TCP 延迟测量，按延迟排序并剔除不可达 IP，结果持久化到 `dnsendpoints.json`。

**IP 来源优先级**：在线 hosts 源（默认 `https://raw.hellogithub.com/hosts`）覆盖的域名**仅使用 hosts 源 IP**；hosts 源未覆盖的域名才走 DNS：本地 `dnscrypt-proxy`（加密 DNS）→ 重试一次 → `FallbackDns`（223.5.5.5 / 119.29.29.29 / 180.76.76.76）。

**平台差异**

| 能力 | Windows | Linux / macOS |
|------|---------|---------------|
| DNS 劫持 | WinDivert 内核级拦截 | 不使用（依赖系统代理） |
| TCP 端口重写 | WinDivert 内核级改写 | 不使用 |
| 监听方式 | 80 / 443 / 22 / 9418 透明反向代理 | `HttpProxyPort`（默认 38457）正向代理 |
| CA 证书安装 | 自动（Windows 证书存储） | Linux 自动（需 root）；macOS 需手动信任 |
| 额外配置 | 无需设置系统代理 | 需手动设置系统代理 |

---

## 功能特性

- 域名纯净 IP 解析 + IP 测速，自动选择最快线路
- 在线 hosts 源解析：覆盖的域名仅使用 hosts 源 IP，未覆盖的走 DNS 回退
- 域名级 TLS 配置：是否发送 SNI、SNI 模板、忽略证书名不匹配
- Google CDN 资源替换，解决国外站点 js/css 加载失败
- HTTPS / HTTP / SSH / Git 协议全支持
- 开机自启（可选）、启动后最小化（可选）
- 系统托盘常驻：更新 IP / 检测更新 / 设置 / 关闭
- 托盘「更新IP」仅从在线 hosts 源刷新；无法连接任何 IP 时自动触发完整刷新
- 实时流量统计与日志查看

---

## 快速开始

### Windows（推荐）

1. 下载发布包并解压
2. **以管理员身份**运行 `FastGithub.UI.exe`（驱动加载与证书安装需要管理员权限）
3. 托盘右键 →「设置」，可开启开机自启与启动后最小化

Windows 下无需配置任何系统代理，浏览器与 git 直接生效。

### Windows 服务

```bash
fastgithub.exe start   # 安装并启动服务
fastgithub.exe stop    # 停止并卸载服务
```

### Linux

```bash
sudo ./fastgithub
# 设置系统代理或自动代理（PAC）：http://127.0.0.1:38457
```

```bash
sudo ./fastgithub start   # 安装并启动 systemd 服务
sudo ./fastgithub stop    # 停止并删除 systemd 服务
```

### macOS

1. 若提示开发者未验证：`sudo xattr -d com.apple.quarantine *.*`
2. 运行 `fastgithub`，目录内会生成 `cacert/`
3. 双击 `cacert/fastgithub.cer`，在钥匙串中展开「信任」并选择「始终信任」
4. 设置系统代理：`http://127.0.0.1:38457`
5. 详见 [MacOSXConfig.md](MacOSXConfig.md)

### Docker

```bash
docker-compose up -d
```

容器内通过 `http_proxy=http://127.0.0.1:38457` 使用。

---

## 端口与路径速查

| 端口 | 平台 | 说明 |
|------|------|------|
| 443 | Windows | HTTPS 反向代理（被占用则从该值向上递增） |
| 80 | Windows | HTTP 反向代理 |
| 22 | Windows | SSH 反向代理（github.com:22） |
| 9418 | Windows | Git 协议反向代理 |
| 45678 | Windows | **UI 内部通信**（`/flowStatistics`、`/refresh-ip`），不是代理端口 |
| 38457 | Linux / macOS | HTTP 正向代理端口（`HttpProxyPort`），同时作为 UDP 日志默认端口 |
| 5533 起 | 全平台 | 本地 `dnscrypt-proxy` 加密 DNS（自动选取可用端口） |

| 路径 | 说明 |
|------|------|
| `cacert/fastgithub.cer`（Linux 为 `.crt`） | 自签 CA 证书，每机独立 |
| `cacert/fastgithub.key` | CA 私钥，**切勿泄露** |
| `appsettings.json` / `appsettings/*.json` | 主配置与按域名分组的子配置 |
| `logs/log.txt` | 运行日志（按天滚动） |
| `dnsendpoints.json` | 已测速端点的持久化缓存 |
| `%AppData%\WindivertDotnet\v222\x64\` | Windows 驱动释放目录（见下节） |

---

## 配置说明

主配置 `appsettings.json`：

```jsonc
{
  "FastGithub": {
    "HttpProxyPort": 38457,        // 正向代理端口，Linux/macOS 使用
    "FallbackDns": [               // 备用 DNS，必须支持 TCP
      "223.5.5.5:53",
      "119.29.29.29:53",
      "180.76.76.76:53"
    ],
    "DomainConfigs": {
      "*.github.com": {
        "TlsSni": false,           // TLS 握手时是否发送 SNI
        "TlsSniPattern": null,     // SNI 模板：@domain / @ipaddress / @random
        "TlsIgnoreNameMismatch": true,
        "Timeout": null,           // 请求超时，如 "00:02:00"
        "IPAddress": null,         // 强制指定上游 IP
        "Destination": null,       // 请求目的地重写
        "Response": null           // 直接拦截并返回预设响应
      }
    }
  }
}
```

域名匹配支持通配符 `*`（`*.github.com` 匹配除 `.` 外任意字符），精确域名优先于通配符。

`appsettings/` 下的 `appsettings.*.json` 为按站点分组的子配置（github、google、microsoft、fastly、amazonaws 等），**新增或修改后需重启应用才生效**。

---

## 桌面界面

`FastGithub.UI.exe` 为系统托盘应用，左键单击恢复窗口，右键菜单：

| 菜单项 | 说明 |
|--------|------|
| 更新IP(&R) | 请求 `/refresh-ip`，拉取在线 hosts 源并重新测速（不发起 DNS 查询） |
| 检测更新(&U) | 打开 Releases 页面 |
| 设置(&S) | 开机自启、启动后最小化 |
| 关闭应用(&C) | 退出（会同时结束后端 `fastgithub.exe`） |

界面包含三个标签页：实时流量图表、运行日志、SSL 证书问题 FAQ。最小化与关闭按钮均隐藏到托盘，不会真正退出。

---

## 注意事项（重要）

### Windows 驱动加载 (WinDivert)

Windows 上启动 FastGithub 后，会加载驱动：

```
C:\Users\<用户名>\AppData\Roaming\WindivertDotnet\v222\x64\WinDivert64.sys
```

**为什么？**

1. FastGithub 要在不改系统代理的前提下劫持流量，必须在内核层拦截并改写数据包，这依赖开源驱动 **WinDivert**（[reqrypt.org/windivert](https://reqrypt.org/windivert.html)）。
2. 项目通过 NuGet 包 `WindivertDotnet` 使用它，DNS 拦截与 TCP 端口重写各会打开一个 WinDivert 句柄。
3. 该库在首次运行时把内置的驱动文件**释放到** `%AppData%\WindivertDotnet\v222\x64\`，再通过服务控制管理器以内核驱动方式加载。目录名含义：`v222` = WinDivert 2.2.2，`x64` = 进程位数（32 位进程为 `x86`）。

> [!NOTE]
> 驱动文件放在 `AppData` 而非 `System32\drivers`，是该 NuGet 库的部署策略，属正常行为。

**影响范围**：驱动只处理匹配过滤器的数据包——出站的 UDP 53（且域名命中配置）以及本机 loopback 上的 80/443/22/9418 TCP 连接，不会劫持其它流量。

**权限要求**：加载内核驱动需要管理员权限。桌面模式请以管理员身份运行 `FastGithub.UI.exe`；服务模式下由 SYSTEM 账户加载。

**杀软 / EDR 误报**：WinDivert 具备内核级抓包与改包能力，常被安全软件标记为风险驱动甚至拦截加载，导致 FastGithub 启动失败。如确认使用本软件，请将上述目录、`FastGithub.UI.exe`、`fastgithub.exe` 加入白名单。

**卸载与清理**：正常退出时驱动会自动卸载并删除；若进程被强杀导致残留，可手动清理：

```powershell
sc query type= driver | findstr /i windivert   # 查看残留驱动服务
sc delete WinDivert                            # 删除（服务名以查询结果为准）
rd /s /q "$env:AppData\WindivertDotnet"        # 删除释放目录，下次启动会自动重建
```

> [!WARNING]
> 卸载程序或删除发布目录前，请先退出 FastGithub（托盘右键「关闭应用」或 `fastgithub.exe stop`），否则驱动会残留。

### CA 证书与流量安全

- 首次运行会在 `cacert/` 生成**本机会话独立的自签 CA**（约 10 年有效期），服务器证书按域名动态签发（约 1 年）。
- Windows / Linux（root）会自动安装并信任该 CA；macOS 需手动设置「始终信任」。
- 这意味着本机 HTTPS 流量会被本地代理终结再转发，属于设计行为。**请勿将 `cacert/fastgithub.key` 私钥泄露给他人**，也不要把 `cacert` 目录复制到不受信任的机器使用。
- 不使用本软件时，可从系统证书存储中移除名为 `FastGithub` 的 CA。

### 系统代理与 hosts 冲突

- Windows 启动时会把配置的域名追加到注册表 `HKCU\Software\Microsoft\Windows\CurrentVersion\Internet Settings` 的 `ProxyOverride`（退出时移除）。若系统已设置了全局代理，这些域名可能仍走原代理，此时日志会输出「由于系统设置了代理 … 无法加速 …」的提示。
- 启动时若 `hosts` 文件中存在命中配置的域名条目，会被**注释掉**（行首加 `# `）以避免冲突。
- > [!IMPORTANT]
  > 被注释的 hosts 行**不会在退出时自动还原**，需要手动编辑 `C:\Windows\System32\drivers\etc\hosts` 去掉行首的 `# `。

### 其它

- 单实例运行（全局 Mutex `Global\FastGithub`），重复启动会提示已有实例并自动退出。
- 所有监听端口均从默认值开始自动寻找可用端口，被占用时会向上顺延，日志中会打印实际端口。
- Linux 下需要 root 才能自动安装 CA 证书；非 root 运行时只会输出警告，证书需手动安装。

---

## 常见问题

**git 报 SSL 证书错误**

```bash
git config --global http.sslverify false
```

**Firefox 提示证书不安全**

设置 → 隐私与安全 → 证书 → 查看证书 → 证书颁发机构 → 导入 `cacert/fastgithub.cer`，勾选「信任由此证书颁发机构来标识网站」。

**Windows 上启动即退出 / 日志报驱动相关错误**

多为权限不足或安全软件拦截了驱动加载，请以管理员身份运行并加白名单（见上文驱动章节）。

**加速不生效**

- Windows：确认系统代理未覆盖 GitHub 域名（查看 `ProxyOverride` 与日志中的冲突提示）；确认 hosts 中的相关条目已被注释。
- Linux / macOS：确认系统代理已设置为 `http://127.0.0.1:38457`。

**macOS 提示无法打开**

```bash
sudo xattr -d com.apple.quarantine *.*
```

---

## 构建与开发

```bash
dotnet build FastGithub.sln -c Release
```

Windows 一键打包（输出到 `publish/`）：

```bash
build.bat        # 后端单文件 + UI 单文件 + appsettings
publish.cmd      # 额外产出 linux-x64 / linux-arm64 / osx-x64 / osx-arm64
```

解决方案结构：

| 项目 | 说明 |
|------|------|
| `FastGithub` | 主程序入口（ASP.NET Core + Kestrel） |
| `FastGithub.Configuration` | 配置管理、域名匹配、端口分配 |
| `FastGithub.DomainResolve` | DNS 解析、dnscrypt-proxy、IP 测速 |
| `FastGithub.Http` | 自定义 HttpClient（DNS + TLS SNI） |
| `FastGithub.HttpServer` | Kestrel 反向代理、证书服务、中间件 |
| `FastGithub.PacketIntercept` | 数据包拦截（仅 Windows，WinDivert） |
| `FastGithub.FlowAnalyze` | 流量统计 |
| `FastGithub.UI` | WPF 托盘界面（通过 HTTP + UDP 与后端通信） |

更多实现细节见 [CODE_WIKI.md](CODE_WIKI.md)。

主要依赖：`Yarp.ReverseProxy`、`DNS`、`WindivertDotnet`、`Serilog`、`LiveCharts.Wpf`（UI）。

---

## 免责声明与许可证

本软件仅用于学习与交流，不具备「翻墙」能力，请在遵守当地法律法规的前提下使用。使用本软件产生的一切后果由使用者自行承担。

基于 [MIT License](LICENSE) 开源。

---

## 更新日志

### v2.3.1 (2026-10-01)

- 托盘「更新IP」改为**仅从在线 hosts 源**获取 IP，不再发起 DNS 查询
- 在线 hosts 源覆盖的域名只使用 hosts 源 IP，不再混入 DNS 解析结果（hosts 未覆盖的域名仍走 DNS 回退）
- 手动刷新改为先拉取 hosts 源、再测速，新映射立即生效
- 重写 README，补充 WinDivert 驱动加载、CA 证书与系统代理等注意事项

### v2.3.0 (2026-09-18)

- 新增托盘右键「更新IP」，可手动刷新 GitHub 域名解析
- 无法连接任何 IP 时自动触发 IP 更新
- 新增内部 `/refresh-ip` 接口，用于清空缓存并重新解析测速

### v2.2.0 (2026-08-15)

- DNS 回退服务器改为国内公共 DNS（223.5.5.5 / 119.29.29.29 / 180.76.76.76）
- Windows 平台 UI 内部通信端口由 38457 调整为 45678
- 支持在线 hosts 源解析（`https://raw.hellogithub.com/hosts`）

### v2.1.7 (2026-07-17)

- 修复主窗口隐藏时右键托盘打开设置窗口崩溃的问题
- 检查更新指向本项目 Releases 页面
- 单文件发布优化：后端 `PublishSingleFile`，UI 使用 Costura.Fody
- 编译前自动清空 bin / obj

### v2.1.6

- 新增开机自启（可选）
- 新增启动后最小化（可选）
- 系统托盘常驻，右键快捷操作
