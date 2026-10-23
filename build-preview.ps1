<#
    build-preview.ps1 —— 编译「测试版」客户端

    和正式版共用同一份源码，差别只有两点：
      1) 用 /define:PREVIEW 编译，数据目录变成 %LOCALAPPDATA%\LanTalkPreview，
         不会污染正式版的资料、聊天记录和对端缓存
      2) 产物是 dist\preview\LanTalk-preview.exe，不覆盖 dist\LanTalk.exe

    这样可以两个版本同时装、同时跑，互相看得见（协议一致），
    方便拿真人通话对比降噪效果。

    用法：  powershell -ExecutionPolicy Bypass -File build-preview.ps1

    注意：本文件必须保存为「UTF-8 带 BOM」。
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$srcDir = Join-Path $root 'src'
$outDir = Join-Path $root 'dist\preview'
$outExe = Join-Path $outDir 'LanTalk-preview.exe'

$candidates = @(
    (Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'),
    (Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe')
)
$csc = $null
foreach ($c in $candidates) { if (Test-Path -LiteralPath $c) { $csc = $c; break } }
if (-not $csc) { throw "找不到 csc.exe，无法编译。" }

$sources = @(Get-ChildItem -LiteralPath $srcDir -Filter *.cs -File | Sort-Object Name | ForEach-Object { $_.FullName })
if ($sources.Count -eq 0) { throw "在 $srcDir 里没有找到任何 .cs 文件。" }

if (-not (Test-Path -LiteralPath $outDir)) { New-Item -ItemType Directory -Path $outDir | Out-Null }

$cscArgs = @(
    '/nologo'
    '/target:winexe'
    '/platform:anycpu'
    '/codepage:65001'
    '/optimize+'
    '/warn:4'
    '/define:PREVIEW'
    '/r:System.dll'
    '/r:System.Drawing.dll'
    '/r:System.Windows.Forms.dll'
)

$ico = Join-Path $root 'assets\LanTalk.ico'
if (Test-Path -LiteralPath $ico) { $cscArgs += "/win32icon:$ico" }

$manifest = Join-Path $root 'assets\app.manifest'
if (Test-Path -LiteralPath $manifest) { $cscArgs += "/win32manifest:$manifest" }

$cscArgs += "/out:$outExe"
$cscArgs += $sources

& $csc @cscArgs
if ($LASTEXITCODE -ne 0) { throw "编译失败，退出码 $LASTEXITCODE" }

$fi = Get-Item -LiteralPath $outExe
Write-Host ""
Write-Host ("测试版编译完成：" + $fi.FullName)
Write-Host ("体积：" + [math]::Round($fi.Length / 1KB, 1) + " KB")
Write-Host ("数据目录：%LOCALAPPDATA%\LanTalkPreview（与正式版隔离）")
Write-Host ""
Write-Host "自检：  .\dist\preview\LanTalk-preview.exe --selftest"
