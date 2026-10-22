# build-update.ps1 —— 打「更新包」
#
# 更新包 = 只含 LanTalk.exe 的 zip，供程序内「设置 → 检查更新 → 拖进去」就地替换。
# 它和完整包分工不同：
#   更新包  dist\LanTalk-<版本>-update.zip   小，拖进去秒级替换，不装快捷方式不写注册表
#   完整包  dist\LanTalk-Setup-<版本>.exe    安装程序，全新安装用
#
# 用法：
#   .\build-update.ps1                     # 用 build.ps1 里同样的版本号
#   .\build-update.ps1 -Version 1.2.1      # 指定版本
#   .\build-update.ps1 -Check              # 只做检查，不产出文件
#
# 注意：本脚本不会自己先编译主程序。要出包请先跑 .\build.ps1，
# 否则打进去的是 dist 里上一次的 exe。

param(
    [string]$Version = '1.3.2',
    [switch]$Check
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$dist = Join-Path $root 'dist'
$exe = Join-Path $dist 'LanTalk.exe'
$zip = Join-Path $dist ("LanTalk-" + $Version + "-update.zip")

Write-Host "更新包版本：$Version"

if (-not (Test-Path -LiteralPath $exe)) {
    throw "找不到 $exe，请先运行 .\build.ps1 编译主程序。"
}

$exeInfo = Get-Item -LiteralPath $exe
$fileVer = $exeInfo.VersionInfo.FileVersion
Write-Host ("主程序：" + $exeInfo.Name + "  " + [math]::Round($exeInfo.Length / 1KB, 1) + " KB  文件版本 " + $fileVer)

# 版本一致性检查：exe 的版本必须和要发的更新包版本一致，
# 否则用户装上去看到的还是旧版本号 —— 这个坑要在这里拦住。
if ($fileVer -and -not $fileVer.StartsWith($Version)) {
    Write-Warning ("版本不一致：exe 是 " + $fileVer + "，更新包标的是 " + $Version + "。")
    Write-Warning "如果这是故意的（比如只改打包号），可以忽略；否则请先改版本号并重新编译。"
}

if ($Check) {
    Write-Host "只做检查，未产出文件（-Check）。"
    exit 0
}

# 暂存目录里只放一个 exe，包内路径就是平铺的 LanTalk.exe
$staging = Join-Path $dist ('_upd_' + [Guid]::NewGuid().ToString('N').Substring(0, 6))
New-Item -ItemType Directory -Force -Path $staging | Out-Null
try {
    Copy-Item -LiteralPath $exe -Destination (Join-Path $staging 'LanTalk.exe') -Force

    if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }
    Compress-Archive -Path (Join-Path $staging 'LanTalk.exe') -DestinationPath $zip -CompressionLevel Optimal

    $zi = Get-Item -LiteralPath $zip
    $ratio = [math]::Round(100.0 * $zi.Length / $exeInfo.Length, 0)
    Write-Host ("更新包：" + $zi.FullName)
    Write-Host ("体积：" + [math]::Round($zi.Length / 1KB, 1) + " KB（原 exe 的 " + $ratio + "%）")

    # 自检：包内必须恰好有一个 LanTalk.exe
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    # 要销毁的是 ZipArchive 本身；Entries 是集合，没有 Dispose
    $arc = [System.IO.Compression.ZipFile]::OpenRead($zip)
    try {
        $names = @($arc.Entries | ForEach-Object { $_.FullName })
        if ($names.Count -ne 1 -or $names[0] -ne 'LanTalk.exe') {
            throw ("包内容不对，应只有 LanTalk.exe，实际：" + ($names -join ', '))
        }
        Write-Host ("包内条目：" + $names[0] + "  " + $arc.Entries[0].Length + " 字节  ✓")
    }
    finally { $arc.Dispose() }
}
finally {
    Remove-Item -Recurse -Force $staging -ErrorAction SilentlyContinue
}

Write-Host ""
Write-Host "下一步（由人决定，不自动执行）："
Write-Host ("  1. .\build-setup.ps1        生成完整包 dist\LanTalk-Setup-" + $Version + ".exe")
Write-Host ("  2. 把 " + (Split-Path $zip -Leaf) + " 和完整包一起上传到 Release")
