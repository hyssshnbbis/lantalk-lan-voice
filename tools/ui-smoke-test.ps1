<#
    ui-smoke-test.ps1 —— 界面冒烟测试（两段）
      第一段：一对一 —— 真的把界面拉起来，让另一个进程真的呼叫它，自动点「接听」，
              核对状态变化、通话链路与端到端音频。
      第二段：会议室 —— 界面自动入会，两个无界面实例也进来，核对成员列表与多路混音。
      截图存到 dist\ 下。

    用法：  powershell -ExecutionPolicy Bypass -File tools\ui-smoke-test.ps1
    注意：本文件必须保存为「UTF-8 带 BOM」（Windows PowerShell 5.1 的要求）。
#>
[CmdletBinding()]
param(
    [int]$GuiPort = 47830,
    [int]$CallerPort = 47831,
    [int]$RoomPort = 47890,
    [string]$OutDir = 'dist'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$exe = Join-Path $root 'dist\LanTalk.exe'
if (-not (Test-Path -LiteralPath $exe)) { throw "找不到 $exe，请先运行 build.ps1" }
$outPath = Join-Path $root $OutDir
if (-not (Test-Path -LiteralPath $outPath)) { New-Item -ItemType Directory -Path $outPath | Out-Null }

Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

public class Ui {
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr p, EnumProc cb, IntPtr l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassNameW(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr h, uint msg, IntPtr wp, IntPtr lp);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    public static string Text(IntPtr h) {
        StringBuilder sb = new StringBuilder(512);
        GetWindowTextW(h, sb, 512);
        return sb.ToString();
    }
    public static string Cls(IntPtr h) {
        StringBuilder sb = new StringBuilder(512);
        GetClassNameW(h, sb, 512);
        return sb.ToString();
    }
    public static List<string> AllTexts(IntPtr root) {
        List<string> res = new List<string>();
        EnumChildWindows(root, delegate(IntPtr h, IntPtr l) {
            string t = Text(h);
            if (t.Length > 0) res.Add(t);
            return true;
        }, IntPtr.Zero);
        return res;
    }
    public static IntPtr FindButton(IntPtr root, string caption) {
        IntPtr found = IntPtr.Zero;
        EnumChildWindows(root, delegate(IntPtr h, IntPtr l) {
            if (found != IntPtr.Zero) return true;
            if (Cls(h).IndexOf("BUTTON", StringComparison.OrdinalIgnoreCase) < 0) return true;
            if (Text(h) == caption) { found = h; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }
    public static bool HasText(IntPtr root, string needle) {
        foreach (string t in AllTexts(root)) if (t.IndexOf(needle, StringComparison.Ordinal) >= 0) return true;
        return false;
    }
    public static void Click(IntPtr h) { SendMessage(h, 0x00F5, IntPtr.Zero, IntPtr.Zero); }  // BM_CLICK
}
"@

[void][Ui]::SetProcessDPIAware()

$script:pass = 0
$script:fail = 0
function Check([string]$name, [bool]$ok, [string]$detail) {
    if ($ok) { $script:pass++; Write-Host ("[PASS] " + $name + " :: " + $detail) -ForegroundColor Green }
    else { $script:fail++; Write-Host ("[FAIL] " + $name + " :: " + $detail) -ForegroundColor Red }
}

function Save-Shot([IntPtr]$h, [string]$file) {
    $r = New-Object Ui+RECT
    [void][Ui]::GetWindowRect($h, [ref]$r)
    $w = $r.Right - $r.Left; $ht = $r.Bottom - $r.Top
    $bmp = New-Object System.Drawing.Bitmap $w, $ht
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    # PrintWindow：直接让窗口把自己画出来，被别的窗口挡住也能拿到干净画面
    $hdc = $g.GetHdc()
    [void][Ui]::PrintWindow($h, $hdc, 2)
    $g.ReleaseHdc($hdc)
    $bmp.Save($file, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
    Write-Host ("      截图：" + $file)
}

function Wait-Window($proc, [int]$tries) {
    for ($i = 0; $i -lt $tries; $i++) {
        Start-Sleep -Milliseconds 250
        $proc.Refresh()
        if ($proc.MainWindowHandle -ne [IntPtr]::Zero) { return $proc.MainWindowHandle }
    }
    return [IntPtr]::Zero
}

function Stop-All($list) {
    foreach ($p in $list) {
        if ($p -eq $null) { continue }
        try { if (-not $p.HasExited) { [void]$p.CloseMainWindow(); Start-Sleep -Milliseconds 400 } } catch { }
        try { if (-not $p.HasExited) { $p.Kill() } } catch { }
    }
}

function Get-PatternStat([string]$logPath) {
    if (-not (Test-Path -LiteralPath $logPath)) { return $null }
    $t = Get-Content -LiteralPath $logPath -Raw -Encoding UTF8
    $m = [regex]::Match($t, 'pattern=(\d+)/(\d+)')
    if (-not $m.Success) { return $null }
    return @{ Match = [int]$m.Groups[1].Value; Total = [int]$m.Groups[2].Value }
}

# ============================================================
#  第一段：一对一
# ============================================================
Write-Host "=== 第一段：一对一通话 ===" -ForegroundColor Cyan
$gui = $null; $caller = $null
$callerLog = Join-Path $outPath 'ui-caller.log'
Remove-Item $callerLog -ErrorAction SilentlyContinue

try {
    $gui = Start-Process -FilePath $exe -PassThru -ArgumentList `
        '--null-audio', '--local-port', "$GuiPort", '--name', 'UI-TEST-GUI'
    $h = Wait-Window $gui 80
    if ($h -eq [IntPtr]::Zero) { throw "界面窗口没有出现" }
    [void][Ui]::SetForegroundWindow($h); Start-Sleep -Milliseconds 700
    Check 'gui_launch' $true ("窗口已出现，句柄 " + $h)

    $caller = Start-Process -FilePath $exe -PassThru -ArgumentList `
        '--headless', '--null-audio', '--local-port', "$CallerPort", `
        '--peer', '127.0.0.1', '--peer-port', "$GuiPort", `
        '--auto-call', '--duration', '22', '--log', $callerLog

    $ringing = $false
    for ($i = 0; $i -lt 40; $i++) {
        Start-Sleep -Milliseconds 250
        if ([Ui]::HasText($h, '来电振铃中')) { $ringing = $true; break }
    }
    [void][Ui]::SetForegroundWindow($h); Start-Sleep -Milliseconds 400
    Save-Shot $h (Join-Path $outPath 'ui-ringing.png')
    Check 'gui_incoming_call' $ringing '界面出现「来电振铃中」'

    $answerBtn = [Ui]::FindButton($h, '接听')
    Check 'gui_answer_button' ($answerBtn -ne [IntPtr]::Zero) '找到「接听」按钮'
    if ($answerBtn -eq [IntPtr]::Zero) { throw "找不到接听按钮" }
    [Ui]::Click($answerBtn)

    $inCall = $false
    for ($i = 0; $i -lt 40; $i++) {
        Start-Sleep -Milliseconds 250
        if ([Ui]::HasText($h, '通话中')) { $inCall = $true; break }
    }
    Start-Sleep -Milliseconds 1500
    Save-Shot $h (Join-Path $outPath 'ui-incall.png')
    Check 'gui_answered' $inCall '点「接听」后界面变成「通话中」'

    $caller.WaitForExit()
    Start-Sleep -Milliseconds 500
    $logText = if (Test-Path -LiteralPath $callerLog) { Get-Content -LiteralPath $callerLog -Raw -Encoding UTF8 } else { '' }
    $ps = Get-PatternStat $callerLog
    $pattOk = $false; $pattDetail = '没有找到音频校验结果'
    if ($ps -ne $null) {
        $pattOk = ($ps.Match -eq $ps.Total) -and ($ps.Total -gt 10000)
        $pattDetail = "端到端样本校验 " + $ps.Match + "/" + $ps.Total
    }
    Check 'gui_audio_end_to_end' $pattOk $pattDetail
    Check 'gui_call_established' ($logText -match 'reached_call=True') '主叫侧确认通话已建立'
    $lost = [regex]::Match($logText, 'lost=(\d+)')
    if ($lost.Success) { Check 'gui_no_packet_loss' ([int]$lost.Groups[1].Value -eq 0) ("丢包 = " + $lost.Groups[1].Value) }

    $hangBtn = [Ui]::FindButton($h, '挂断')
    if ($hangBtn -ne [IntPtr]::Zero) {
        [Ui]::Click($hangBtn)
        $idle = $false
        for ($i = 0; $i -lt 20; $i++) {
            Start-Sleep -Milliseconds 250
            if ([Ui]::HasText($h, '空闲')) { $idle = $true; break }
        }
        Check 'gui_hangup' $idle '点「挂断」后界面回到「空闲」'
    }
}
finally { Stop-All @($caller, $gui) }

# ============================================================
#  第二段：多人会议室
# ============================================================
Write-Host ""
Write-Host "=== 第二段：多人会议室（1 个界面 + 2 个无界面，全网状） ===" -ForegroundColor Cyan
$gui2 = $null; $m1 = $null; $m2 = $null
$log1 = Join-Path $outPath 'ui-conf-member1.log'
$log2 = Join-Path $outPath 'ui-conf-member2.log'
Remove-Item $log1, $log2 -ErrorAction SilentlyContinue

try {
    $p1 = $RoomPort + 1
    $p2 = $RoomPort + 2

    # 界面自动入会（房间号 = 自己的监听端口），并手工补上另外两人
    $gui2 = Start-Process -FilePath $exe -PassThru -ArgumentList `
        '--null-audio', '--room', "$RoomPort", '--local-port', "$RoomPort", `
        '--peers', "127.0.0.1:$p1,127.0.0.1:$p2", '--name', 'UI-TEST-ROOM'
    $h2 = Wait-Window $gui2 80
    if ($h2 -eq [IntPtr]::Zero) { throw "会议界面窗口没有出现" }
    [void][Ui]::SetForegroundWindow($h2); Start-Sleep -Milliseconds 800

    Check 'room_gui_joined' ([Ui]::HasText($h2, '会议中')) '界面已进入会议状态'

    $m1 = Start-Process -FilePath $exe -PassThru -ArgumentList `
        '--headless', '--null-audio', '--room', "$RoomPort", '--local-port', "$p1", `
        '--peers', "127.0.0.1:$RoomPort,127.0.0.1:$p2", '--name', '成员甲', `
        '--duration', '16', '--log', $log1
    Start-Sleep -Milliseconds 400
    $m2 = Start-Process -FilePath $exe -PassThru -ArgumentList `
        '--headless', '--null-audio', '--room', "$RoomPort", '--local-port', "$p2", `
        '--peers', "127.0.0.1:$RoomPort,127.0.0.1:$p1", '--name', '成员乙', `
        '--duration', '16', '--log', $log2

    $twoMembers = $false
    for ($i = 0; $i -lt 40; $i++) {
        Start-Sleep -Milliseconds 250
        if ([Ui]::HasText($h2, '成员 2 人')) { $twoMembers = $true; break }
    }
    [void][Ui]::SetForegroundWindow($h2); Start-Sleep -Milliseconds 400
    Save-Shot $h2 (Join-Path $outPath 'ui-conference.png')
    Check 'room_two_members' $twoMembers '界面显示「成员 2 人」'

    $m1.WaitForExit(); $m2.WaitForExit()
    Start-Sleep -Milliseconds 500

    $s1 = Get-PatternStat $log1
    $s2 = Get-PatternStat $log2
    $ok1 = ($s1 -ne $null) -and ($s1.Match -eq $s1.Total) -and ($s1.Total -gt 10000)
    $ok2 = ($s2 -ne $null) -and ($s2.Match -eq $s2.Total) -and ($s2.Total -gt 10000)
    $d1 = if ($s1 -ne $null) { "$($s1.Match)/$($s1.Total)" } else { "无" }
    $d2 = if ($s2 -ne $null) { "$($s2.Match)/$($s2.Total)" } else { "无" }
    Check 'room_member_audio' ($ok1 -and $ok2) ("成员甲 " + $d1 + "，成员乙 " + $d2)

    $t1 = Get-Content -LiteralPath $log1 -Raw -Encoding UTF8
    $ch = [regex]::Match($t1, 'audio_channels=(\d+)')
    $ls = [regex]::Match($t1, 'lost=(\d+)')
    if ($ch.Success) { Check 'room_mesh_channels' ([int]$ch.Groups[1].Value -eq 2) ("每人的远端通道数 = " + $ch.Groups[1].Value) }
    if ($ls.Success) { Check 'room_no_packet_loss' ([int]$ls.Groups[1].Value -eq 0) ("丢包 = " + $ls.Groups[1].Value) }
}
finally { Stop-All @($m1, $m2, $gui2) }

Write-Host ""
Write-Host ("界面冒烟测试：pass=" + $script:pass + " fail=" + $script:fail)
if ($script:fail -eq 0) { Write-Host "=== 界面冒烟测试通过 ===" -ForegroundColor Green; exit 0 }
else { Write-Host "=== 界面冒烟测试失败 ===" -ForegroundColor Red; exit 1 }
