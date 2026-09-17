# Capture the app window reliably.
#
# Two lessons baked in here:
#   1. A window can be positioned partly off-screen, so CopyFromScreen of its rect
#      picks up whatever else is on the desktop. Move it to a known spot first.
#   2. PrintWindow(PW_RENDERFULLCONTENT) captures the window's own surface even if
#      occluded, but GPU-composited (Avalonia/Skia) windows may come back blank,
#      so fall back to a screen-region grab with the window raised.
param(
    [string]$ProcessName = 'HomeworkReminder.Desktop',
    [string]$TitleMatch = '作业提醒',
    # 默认输出到仓库内 docs\screenshots（由脚本位置推导，不再写死盘符路径）。
    [string]$OutFile = (Join-Path (Split-Path -Parent $PSScriptRoot) 'docs\screenshots\app.png'),
    [int]$X = 40,
    [int]$Y = 40,
    # 0 表示保持窗口当前尺寸（登录窗口是固定紧凑尺寸，强行改大会失真）。
    [int]$Width = 0,
    [int]$Height = 0,
    [switch]$UsePrintWindow
)

$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
public class WinCap {
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int cmd);
    [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr hWnd, int x, int y, int w, int h, bool repaint);
    [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr hWnd, System.Text.StringBuilder s, int max);
}
"@

Write-Host "DBG TitleMatch codes: $(($TitleMatch.ToCharArray() | ForEach-Object { [int]$_ }) -join ',')"
Get-Process -Name $ProcessName -ErrorAction SilentlyContinue | ForEach-Object { Write-Host "DBG proc $($_.Id) hwnd=$($_.MainWindowHandle) titlecodes=$(($_.MainWindowTitle.ToCharArray() | ForEach-Object { [int]$_ }) -join ',')" }
$proc = Get-Process -Name $ProcessName -ErrorAction SilentlyContinue |
    Where-Object { $_.MainWindowHandle -ne 0 -and $_.MainWindowTitle -like "*$TitleMatch*" } |
    Select-Object -First 1
if (-not $proc) { throw "no '$TitleMatch' window found for process '$ProcessName'" }

$h = $proc.MainWindowHandle
Write-Host "pid=$($proc.Id) title='$($proc.MainWindowTitle)' hwnd=$h"

# Park it at a known on-screen position with a size we control
# (or keep its current size when Width/Height are 0).
[void][WinCap]::ShowWindow($h, 9)   # SW_RESTORE（SW_SHOW 无法还原最小化的窗口）
$r0 = New-Object WinCap+RECT
[void][WinCap]::GetWindowRect($h, [ref]$r0)
if ($Width -le 0) { $Width = $r0.Right - $r0.Left }
if ($Height -le 0) { $Height = $r0.Bottom - $r0.Top }
[void][WinCap]::MoveWindow($h, $X, $Y, $Width, $Height, $true)
[void][WinCap]::BringWindowToTop($h)
[void][WinCap]::SetForegroundWindow($h)
Start-Sleep -Milliseconds 1500

$fg = [WinCap]::GetForegroundWindow()
$sb = New-Object System.Text.StringBuilder 256
[void][WinCap]::GetWindowTextW($fg, $sb, 256)
Write-Host "foreground now: '$($sb.ToString())'"
if ($fg -ne $h) { Write-Warning 'app window is not foreground; capture may include other windows' }

$r = New-Object WinCap+RECT
[void][WinCap]::GetWindowRect($h, [ref]$r)
$w = $r.Right - $r.Left; $ht = $r.Bottom - $r.Top
Write-Host "rect: ${w}x${ht} at ($($r.Left),$($r.Top))"
if ($w -le 0 -or $ht -le 0) { throw "bad size ${w}x${ht}" }

$bmp = New-Object System.Drawing.Bitmap $w, $ht
$g = [System.Drawing.Graphics]::FromImage($bmp)

if ($UsePrintWindow) {
    $hdc = $g.GetHdc()
    # 2 = PW_RENDERFULLCONTENT (needed for composited windows)
    $ok = [WinCap]::PrintWindow($h, $hdc, 2)
    $g.ReleaseHdc($hdc)
    Write-Host "PrintWindow -> $ok"
    if (-not $ok) {
        $g.CopyFromScreen($r.Left, $r.Top, 0, 0, $bmp.Size)
    }
} else {
    $g.CopyFromScreen($r.Left, $r.Top, 0, 0, $bmp.Size)
}

$g.Dispose()
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $OutFile) | Out-Null
$bmp.Save($OutFile, [System.Drawing.Imaging.ImageFormat]::Png)

# Report how many distinct colours we got: an all-black capture means the GPU
# surface was not available and the screenshot is useless.
$colors = @{}
for ($px = 0; $px -lt $w; $px += 17) {
    for ($py = 0; $py -lt $ht; $py += 17) {
        $colors[$bmp.GetPixel($px, $py).ToArgb()] = $true
    }
}
$bmp.Dispose()
Write-Host "distinct sampled colours: $($colors.Count)"
Write-Host "saved: $OutFile"
