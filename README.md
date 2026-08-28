

# FastGithub

![Version](https://img.shields.io/badge/version-2.2.0-blue)
![License](https://img.shields.io/badge/license-MIT-green)

GitHub 加速神器，解决 GitHub 打不开、用户头像无法加载、Releases 无法上传下载、git clone/pull/push 失败等问题。

## 功能特性

- 域名纯净 IP 解析
- IP 测速并选择最快的 IP
- 在线 hosts 源解析（GitHub520），DNS 之外的额外候选 IP
- 域名 TLS 连接自定义配置
- Google CDN 资源替换，解决国外网站无法加载 js/css 的问题
- **开机自启**（可选）
- **启动后最小化**（可选）
- 系统托盘常驻，右键可快速操作

## 部署方式

### Windows (推荐)

1. 下载发布包并解压
2. 双击运行 `FastGithub.UI.exe`
3. 右键托盘图标可进入设置，开启开机自启和启动后最小化

### Windows 服务

```bash
fastgithub.exe start   # 安装并启动服务（需管理员权限）
fastgithub.exe stop    # 停止并卸载服务（需管理员权限）
```

### Linux

```bash
sudo ./fastgithub
# 设置系统代理: http://127.0.0.1:38457
```

### Linux 服务

```bash
sudo ./fastgithub start   # 以 systemd 服务启动
sudo ./fastgithub stop    # 停止并删除服务
```

### macOS

1. 双击运行 `fastgithub`
2. 安装 `cacert/fastgithub.cer` 并设置信任
3. 设置系统代理: `http://127.0.0.1:38457`
4. [详细配置](MacOSXConfig.md)

### Docker

```bash
docker-compose up -d
```

## 代理配置

| 协议 | 地址 | 端口 |
|------|------|------|
| HTTP/HTTPS | 127.0.0.1 | 45678 |

> Windows 平台系统代理端口为 `45678`；Linux/macOS 平台默认仍为 `38457`（对应 `appsettings.json` 中的 `HttpProxyPort`）。

## 常见问题

### SSL 证书错误

```bash
git config --global http.sslverify false
```

### Firefox 安全提示

设置 → 隐私与安全 → 证书 → 查看证书 → 证书颁发机构，导入 `cacert/fastgithub.cer`，勾选"信任由此证书颁发机构来标识网站"。

## 安全说明

FastGithub 为每台主机生成独立的自签名 CA 证书（保存在 `cacert` 文件夹）。请勿将证书私钥泄露给他人。

## 免责声明

本软件仅用于学习交流，不具备"翻墙"功能。请遵守当地法律法规。

## 许可证

[MIT License](LICENSE)

## 更新日志

### v2.2.0 (2026-08-15)
- DNS 回退服务器改为国内公共 DNS（223.5.5.5 / 119.29.29.29 / 180.76.76.76）
- Windows 系统代理端口由 38457 调整为 45678
- 支持在线 hosts 源解析（https://raw.hellogithub.com/hosts），作为 DNS 之外的额外候选 IP

### v2.1.7 (2026-07-17)
- 修复主窗口隐藏时，右键托盘图标打开设置窗口导致程序崩溃的问题
- 检查更新功能指向本项目 releases 页面
- 单文件发布优化，后端使用 PublishSingleFile，UI 使用 Costura.Fody
- 编译前自动清空 bin/obj 目录

### v2.1.6
- 添加开机自启功能（可选）
- 添加启动后最小化功能（可选）
- 系统托盘常驻，右键可快速操作
