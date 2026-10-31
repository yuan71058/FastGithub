#requires -Version 5.1
<#
.SYNOPSIS
    为 FastGithub 探测当前网络下真正可用的 GitHub 入口 IP，并写回 appsettings 配置。

.DESCRIPTION
    FastGithub 默认只用 DNS / 在线 hosts 源给出的少量 github.com IP。当这些 IP 在当前
    网络被阻断时，请求会连续超时并返回 “找不到任何可成功连接的IP”。缓存里的 IP 会漂移，
    因此需要一个可重复执行的探测器，把当前真正能完成 HTTPS 交互的 IP 写进配置。

    探测分三段，只有全部通过才算可用：
      1. TCP 443 连接（默认 1.5s 超时）
      2. TLS 握手（SNI = github.com）
      3. HTTP HEAD / 返回 2xx/3xx（剔除 “TCP 通但 HTTP 不可用” 的假可用 IP）

    配置写入 appsettings/appsettings.github.json 的 github.com.IPAddress。
    该配置支持热加载（reloadOnChange），无需重启 FastGithub 即可生效。

.PARAMETER Root
    FastGithub 运行目录（含 fastgithub.exe）。省略时自动从运行中的进程推断。

.PARAMETER TcpTimeoutMs
    TCP 连接超时（毫秒）。

.PARAMETER HttpTimeoutMs
    TLS 握手与读取响应的超时（毫秒）。

.PARAMETER DryRun
    只探测并打印结果，不写配置文件。

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\Update-FastGithubIp.ps1

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\Update-FastGithubIp.ps1 -Root E:\2026\fastgithub_win-x64 -DryRun
#>
[CmdletBinding()]
param(
    [string]$Root = '',
    [int]$TcpTimeoutMs = 1500,
    [int]$HttpTimeoutMs = 3000,
    [int]$ThrottleMs = 1500,
    [int]$MaxCandidates = 4,
    [switch]$DryRun
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

# GitHub.com 官方入口常用网段。仅作为候选种子，是否可用必须经过三段探测。
#
# 注意：候选集刻意保持较小，且探测之间留有间隔。实测在短时间内对二十余个 IP
# 并发探测，会触发网络侧对该源 IP 的临时阻断，反而让原本可用的 IP 全部超时。
# 因此这里只保留历史高命中率的网段，并按顺序串行探测。
$seedIps = @(
    '20.205.243.166',
    '20.26.156.215',
    '20.29.134.23',
    '20.233.83.145',
    '20.26.156.216',
    '20.205.243.165',
    '140.82.113.3',
    '4.237.22.38'
)

$targetDomain = 'github.com'

# PowerShell 的 ScriptBlock 不能作为 TLS 证书回调：
# SslStream 的异步回调在无 Runspace 的线程上执行，会抛
# “此线程中没有可用于运行脚本的运行空间”。这里编译一个静态委托来替代。
if (-not ('FastGithubProbeCertificatePolicy' -as [type])) {
    Add-Type -TypeDefinition @"
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

public static class FastGithubProbeCertificatePolicy
{
    public static bool Validate(object sender, X509Certificate certificate, X509Chain chain, SslPolicyErrors errors)
    {
        return true;
    }

    public static readonly RemoteCertificateValidationCallback Callback =
        new RemoteCertificateValidationCallback(Validate);
}
"@
}

$trustAll = [FastGithubProbeCertificatePolicy]::Callback

function Test-GitHubEndpoint {
    param(
        [Parameter(Mandatory)][string]$IP,
        [Parameter(Mandatory)][string]$HostName,
        [int]$TcpTimeoutMs = 1500,
        [int]$HttpTimeoutMs = 3000
    )

    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    $client = $null
    $ssl = $null
    $reader = $null

    try {
        $client = New-Object System.Net.Sockets.TcpClient

        $connect = $client.BeginConnect($IP, 443, $null, $null)
        if (-not $connect.AsyncWaitHandle.WaitOne($TcpTimeoutMs)) {
            return $null
        }
        $client.EndConnect($connect)

        $client.ReceiveTimeout = $HttpTimeoutMs
        $client.SendTimeout = $HttpTimeoutMs

        $ssl = New-Object System.Net.Security.SslStream($client.GetStream(), $false, $script:trustAll)
        $ssl.ReadTimeout = $HttpTimeoutMs
        $ssl.WriteTimeout = $HttpTimeoutMs

        $auth = $ssl.BeginAuthenticateAsClient($HostName, $null, [System.Security.Authentication.SslProtocols]::Tls12, $false, $null, $null)
        if (-not $auth.AsyncWaitHandle.WaitOne($HttpTimeoutMs)) {
            return $null
        }
        $ssl.EndAuthenticateAsClient($auth)

        $request = "HEAD / HTTP/1.1`r`nHost: $HostName`r`nUser-Agent: FastGithub-IPProbe`r`nAccept: */*`r`nConnection: close`r`n`r`n"
        $payload = [System.Text.Encoding]::ASCII.GetBytes($request)
        $ssl.Write($payload, 0, $payload.Length)
        $ssl.Flush()

        $reader = New-Object System.IO.StreamReader($ssl, [System.Text.Encoding]::ASCII)
        $statusLine = $reader.ReadLine()
        if ([string]::IsNullOrWhiteSpace($statusLine)) {
            return $null
        }

        if ($statusLine -notmatch '^HTTP/\d\.\d\s+(\d{3})') {
            return $null
        }

        $status = [int]$Matches[1]
        # 只承认正常的业务响应；400/404 说明该 IP 不服务 github.com 的 Host
        if ($status -lt 200 -or $status -ge 400) {
            return $null
        }

        $watch.Stop()
        return [pscustomobject]@{
            IP     = $IP
            Status = $status
            Ms     = [int]$watch.ElapsedMilliseconds
        }
    }
    catch {
        Write-Verbose ("探测 {0} 失败：{1}" -f $IP, $_.Exception.Message)
        return $null
    }
    finally {
        if ($reader) { $reader.Dispose() }
        if ($ssl) { $ssl.Dispose() }
        if ($client) { $client.Dispose() }
    }
}

function Resolve-FastGithubRoot {
    param([string]$Hint)

    if ($Hint -and (Test-Path $Hint)) {
        return (Resolve-Path $Hint).Path
    }

    $process = Get-Process -Name 'fastgithub' -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($process -and $process.Path) {
        return (Split-Path $process.Path -Parent)
    }

    $cim = Get-CimInstance Win32_Process -Filter "Name = 'fastgithub.exe'" -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($cim -and $cim.ExecutablePath) {
        return (Split-Path $cim.ExecutablePath -Parent)
    }

    throw '未找到 fastgithub 运行目录，请用 -Root 指定。'
}

function Read-ConfiguredIp {
    param([string]$ConfigPath)

    if (-not (Test-Path $ConfigPath)) {
        return $null
    }

    $json = [System.IO.File]::ReadAllText($ConfigPath)
    $config = $json | ConvertFrom-Json

    $domainConfigs = $config.FastGithub.DomainConfigs
    if ($null -eq $domainConfigs) {
        return $null
    }

    $domainProperty = $domainConfigs.PSObject.Properties[$targetDomain]
    if ($null -eq $domainProperty) {
        return $null
    }

    $ipProperty = $domainProperty.Value.PSObject.Properties['IPAddress']
    if ($null -eq $ipProperty) {
        return $null
    }

    return $ipProperty.Value
}

function Set-ConfiguredIp {
    param(
        [string]$ConfigPath,
        [string]$IP
    )

    $json = [System.IO.File]::ReadAllText($ConfigPath)
    $config = $json | ConvertFrom-Json

    $domainConfigs = $config.FastGithub.DomainConfigs
    $domainProperty = $domainConfigs.PSObject.Properties[$targetDomain]
    if ($null -eq $domainProperty) {
        $domainConfig = New-Object psobject
        $domainConfigs | Add-Member -MemberType NoteProperty -Name $targetDomain -Value $domainConfig
    }
    else {
        $domainConfig = $domainProperty.Value
    }

    if ($domainConfig.PSObject.Properties['IPAddress']) {
        $domainConfig.IPAddress = $IP
    }
    else {
        $domainConfig | Add-Member -MemberType NoteProperty -Name 'IPAddress' -Value $IP
    }

    $output = $config | ConvertTo-Json -Depth 12
    $utf8Bom = New-Object System.Text.UTF8Encoding($true)
    [System.IO.File]::WriteAllText($ConfigPath, $output, $utf8Bom)
}

# ---------------------------------------------------------------------------
# 主流程
# ---------------------------------------------------------------------------
$runRoot = Resolve-FastGithubRoot -Hint $Root
$configPath = Join-Path $runRoot 'appsettings\appsettings.github.json'

if (-not (Test-Path $configPath)) {
    throw "未找到配置文件：$configPath"
}

Write-Host "FastGithub 运行目录：$runRoot"
Write-Host "探测目标：$targetDomain (TCP ${TcpTimeoutMs}ms / HTTP ${HttpTimeoutMs}ms)"
Write-Host ''

$current = Read-ConfiguredIp -ConfigPath $configPath

# 优先复测当前生效 IP，其余按候选顺序补测
$probeList = New-Object System.Collections.ArrayList
if (-not [string]::IsNullOrWhiteSpace($current)) {
    [void]$probeList.Add($current)
}
foreach ($ip in $seedIps) {
    if ($ip -ne $current) {
        [void]$probeList.Add($ip)
    }
}

$results = New-Object System.Collections.ArrayList
$probeCount = [Math]::Min($probeList.Count, $MaxCandidates)
for ($index = 0; $index -lt $probeCount; $index++) {
    $ip = $probeList[$index]
    $probe = Test-GitHubEndpoint -IP $ip -HostName $targetDomain -TcpTimeoutMs $TcpTimeoutMs -HttpTimeoutMs $HttpTimeoutMs
    if ($null -ne $probe) {
        [void]$results.Add($probe)
        Write-Host ("  OK   {0,-16} HTTP {1}  {2} ms" -f $probe.IP, $probe.Status, $probe.Ms) -ForegroundColor Green
    }
    else {
        Write-Host ("  FAIL {0,-16}" -f $ip) -ForegroundColor DarkGray
    }

    # 最后一个不再等待
    if ($index -lt ($probeCount - 1)) {
        Start-Sleep -Milliseconds $ThrottleMs
    }
}

if ($results.Count -eq 0) {
    Write-Warning '没有任何候选 IP 通过三段探测。请检查本机网络，或稍后重试。'
    exit 1
}

$best = $results | Sort-Object -Property Ms | Select-Object -First 1
$current = Read-ConfiguredIp -ConfigPath $configPath

Write-Host ''
Write-Host ("可用 IP {0} 个，最优：{1} ({2} ms)" -f $results.Count, $best.IP, $best.Ms)
Write-Host ("当前配置：{0}" -f $(if ([string]::IsNullOrWhiteSpace($current)) { '(未设置)' } else { $current }))

if ($DryRun) {
    Write-Host 'DryRun：不写入配置。' -ForegroundColor Yellow
    exit 0
}

if ($current -eq $best.IP) {
    Write-Host '配置已是最优 IP，无需修改。' -ForegroundColor Green
    exit 0
}

Set-ConfiguredIp -ConfigPath $configPath -IP $best.IP
Write-Host ("已写入 {0} -> {1}（热加载生效，无需重启）" -f $configPath, $best.IP) -ForegroundColor Green
