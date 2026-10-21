<#
    build-setup.ps1 —— 编译安装程序（单文件安装 exe）
    先调 build.ps1 编译出 dist\LanTalk.exe，再把它和 README、防火墙脚本一起作为
    内嵌资源打进安装程序，最后产出 dist\LanTalk-Setup-1.1.0.exe。

    同样只用 Windows 自带的 csc.exe，不需要联网、不需要任何安装器制作工具（Inno/NSIS 都不需要）。

    用法：  powershell -ExecutionPolicy Bypass -File build-setup.ps1
    注意：本文件必须保存为「UTF-8 带 BOM」。
#>
[CmdletBinding()]
param(
    [string]$Version = '1.1.0'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path

# ---------- 1. 先编译主程序 ----------
Write-Host "=== 1/3 编译主程序 ==="
& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'build.ps1')
if ($LASTEXITCODE -ne 0) { throw "主程序编译失败。" }

$appExe = Join-Path $root 'dist\LanTalk.exe'
if (-not (Test-Path -LiteralPath $appExe)) { throw "找不到 $appExe" }

# ---------- 2. 找编译器 ----------
$candidates = @(
    (Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'),
    (Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe')
)
$csc = $null
foreach ($c in $candidates) { if (Test-Path -LiteralPath $c) { $csc = $c; break } }
if (-not $csc) { throw "找不到 Windows 自带的 C# 编译器 csc.exe。" }

# ---------- 3. 编译安装程序 ----------
Write-Host ""
Write-Host "=== 2/3 编译安装程序 ==="
$setupSrc = Join-Path $root 'src\Setup'
$sources = @(Get-ChildItem -LiteralPath $setupSrc -Filter *.cs -File | Sort-Object Name | ForEach-Object { $_.FullName })
# 版本信息与主程序共用同一份
$sources += (Join-Path $root 'src\AssemblyInfo.cs')

$outExe = Join-Path $root ("dist\LanTalk-Setup-" + $Version + ".exe")

$cscArgs = @(
    '/nologo'
    '/target:winexe'
    '/platform:anycpu'
    '/codepage:65001'
    '/optimize+'
    '/r:System.dll'
    '/r:System.Drawing.dll'
    '/r:System.Windows.Forms.dll'
)

$ico = Join-Path $root 'assets\LanTalk.ico'
if (Test-Path -LiteralPath $ico) {
    $cscArgs += "/win32icon:$ico"
}
$manifest = Join-Path $root 'assets\app.manifest'
if (Test-Path -LiteralPath $manifest) { $cscArgs += "/win32manifest:$manifest" }

# 内嵌载荷：名字统一用 Payload.<文件名>，代码里按这个名字取
$payloads = @(
    @{ File = $appExe;                       Name = 'Payload.LanTalk.exe' },
    @{ File = (Join-Path $root 'README.md'); Name = 'Payload.README.md' },
    @{ File = (Join-Path $root 'Allow-Firewall.ps1'); Name = 'Payload.Allow-Firewall.ps1' }
)
foreach ($p in $payloads) {
    if (-not (Test-Path -LiteralPath $p.File)) { throw ("缺少载荷文件：" + $p.File) }
    $cscArgs += ("/resource:" + $p.File + "," + $p.Name)
    Write-Host ("嵌入载荷：" + (Split-Path -Leaf $p.File) + "  ->  " + $p.Name)
}

$cscArgs += "/out:$outExe"
$cscArgs += $sources

Write-Host ("安装程序源文件：" + $sources.Count + " 个")
& $csc @cscArgs
if ($LASTEXITCODE -ne 0) { throw "安装程序编译失败，退出码 $LASTEXITCODE" }

$fi = Get-Item -LiteralPath $outExe
Write-Host ""
Write-Host "=== 3/3 完成 ==="
Write-Host ("安装程序：" + $fi.FullName)
Write-Host ("体积：" + [math]::Round($fi.Length / 1KB, 1) + " KB")
Write-Host ""
Write-Host "双击即可安装。命令行静默安装示例："
Write-Host ("  " + $fi.Name + " --silent --dir `"%LOCALAPPDATA%\Programs\LanTalk`"")
Write-Host ("静默卸载：  " + $fi.Name + " --uninstall --silent")
