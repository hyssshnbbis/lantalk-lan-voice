<#
    Allow-Firewall.ps1 —— 给 LanTalk 放行 Windows 防火墙
    必须用「管理员身份」运行 PowerShell 才能生效。

    正常情况下不需要跑这个脚本：第一次启动 LanTalk 时 Windows 会自己弹出
    「是否允许此应用通过防火墙」的提示，勾选「专用网络」并允许即可。
    只有在没弹出提示、或对方一直收不到你的呼叫时，才用它手工放行。

    用法：
        放行：  powershell -ExecutionPolicy Bypass -File Allow-Firewall.ps1
        撤销：  powershell -ExecutionPolicy Bypass -File Allow-Firewall.ps1 -Remove
        换端口：powershell -ExecutionPolicy Bypass -File Allow-Firewall.ps1 -Port 47900
#>
[CmdletBinding()]
param(
    [int]$Port = 47800,
    [switch]$Remove
)

$ErrorActionPreference = 'Stop'

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
$isAdmin = $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Host "需要管理员权限。请右键点击 PowerShell，选择「以管理员身份运行」，再执行本脚本。" -ForegroundColor Yellow
    exit 1
}

$ruleName = "LanTalk 局域网语音通话 (UDP $Port)"

if ($Remove) {
    $existing = Get-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue
    if ($existing) {
        Remove-NetFirewallRule -DisplayName $ruleName
        Write-Host "已删除防火墙规则：$ruleName" -ForegroundColor Green
    } else {
        Write-Host "没有找到规则：$ruleName"
    }
    exit 0
}

if (Get-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue) {
    Write-Host "规则已存在，无需重复添加：$ruleName" -ForegroundColor Green
    exit 0
}

New-NetFirewallRule `
    -DisplayName $ruleName `
    -Description "允许 LanTalk 通过 UDP $Port 收发语音数据（局域网点对点通话）" `
    -Direction Inbound `
    -Action Allow `
    -Protocol UDP `
    -LocalPort $Port `
    -Profile Any | Out-Null

Write-Host "已放行入站 UDP 端口 $Port（所有网络类型）。" -ForegroundColor Green
Write-Host "如果 LanTalk 里改过端口，请用 -Port 参数指定同样的端口再执行一次。"
Write-Host "撤销：  powershell -ExecutionPolicy Bypass -File Allow-Firewall.ps1 -Remove -Port $Port"
