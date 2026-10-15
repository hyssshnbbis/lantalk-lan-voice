<#
    build.ps1 —— 编译 LanTalk
    只用 Windows 自带的 .NET Framework C# 编译器（csc.exe），不需要联网、不需要装 Visual Studio、
    也不需要任何 NuGet 包。产物是单个 dist\LanTalk.exe，拷到别的 Windows 电脑上直接就能跑。

    用法：  powershell -ExecutionPolicy Bypass -File build.ps1

    注意：本文件必须保存为「UTF-8 带 BOM」。Windows PowerShell 5.1 会把无 BOM 的 UTF-8
          当成 GBK 读，下面的中文注释和提示会变成乱码并导致语法错误。
#>
[CmdletBinding()]
param(
    [switch]$DebugBuild
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$srcDir = Join-Path $root 'src'
$distDir = Join-Path $root 'dist'
$outExe = Join-Path $distDir 'LanTalk.exe'

# ---------- 找编译器 ----------
$candidates = @(
    (Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'),
    (Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'),
    (Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v3.5\csc.exe'),
    (Join-Path $env:WINDIR 'Microsoft.NET\Framework\v3.5\csc.exe')
)
$csc = $null
foreach ($c in $candidates) { if (Test-Path -LiteralPath $c) { $csc = $c; break } }
if (-not $csc) { throw "找不到 Windows 自带的 C# 编译器 csc.exe，无法编译。" }

Write-Host "编译器：$csc"

# ---------- 源文件 ----------
$sources = @(Get-ChildItem -LiteralPath $srcDir -Filter *.cs -File | Sort-Object Name | ForEach-Object { $_.FullName })
if ($sources.Count -eq 0) { throw "在 $srcDir 里没有找到任何 .cs 文件。" }
Write-Host ("源文件：" + $sources.Count + " 个")

if (-not (Test-Path -LiteralPath $distDir)) { New-Item -ItemType Directory -Path $distDir | Out-Null }

# ---------- 编译 ----------
# /codepage:65001 很关键：源码是 UTF-8（无 BOM），不加这个参数界面上的中文会变成乱码
$cscArgs = @(
    '/nologo'
    '/target:winexe'
    '/platform:anycpu'
    '/codepage:65001'
    '/warn:4'
    '/r:System.dll'
    '/r:System.Drawing.dll'
    '/r:System.Windows.Forms.dll'
)
if ($DebugBuild) { $cscArgs += @('/debug+', '/define:DEBUG') } else { $cscArgs += '/optimize+' }

$ico = Join-Path $root 'assets\LanTalk.ico'
if (Test-Path -LiteralPath $ico) {
    $cscArgs += "/win32icon:$ico"
}

$manifest = Join-Path $root 'assets\app.manifest'
if (Test-Path -LiteralPath $manifest) { $cscArgs += "/win32manifest:$manifest" }

$cscArgs += "/out:$outExe"
$cscArgs += $sources

& $csc @cscArgs
if ($LASTEXITCODE -ne 0) { throw "编译失败，退出码 $LASTEXITCODE" }

$fi = Get-Item -LiteralPath $outExe
Write-Host ""
Write-Host ("编译完成：" + $fi.FullName)
Write-Host ("体积：" + [math]::Round($fi.Length / 1KB, 1) + " KB")
Write-Host ""
Write-Host "下一步：把 dist\LanTalk.exe 拷到两台电脑上，双击运行即可。"
Write-Host "若在命令行做自检：  .\dist\LanTalk.exe --selftest"
