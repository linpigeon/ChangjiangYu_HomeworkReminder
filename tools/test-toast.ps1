# 系统通知端到端自测（winnotify）。
#
# 两段验证：
#   1. 跑应用自带的 --toastcheck（生产同款的 AUMID 注册 + 快捷方式 + XML + WinRT 投递路径）
#   2. 用 WinRT 通知历史确认 toast 真的进了通知中心（投递成功≠用户可见，历史里有才算到）
#
# 用法：
#   .\tools\test-toast.ps1                 # 用 bin\Debug 的构建
#   .\tools\test-toast.ps1 -Exe publish\trim\HomeworkReminder.Desktop.exe
#
# 退出码：0 = 通知链路可用且 toast 入历史；非 0 = 失败（原因见输出）。
param(
    [string]$Exe = "HomeworkReminder.Desktop\bin\Debug\net10.0\HomeworkReminder.Desktop.exe"
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$exePath = Join-Path $root $Exe

Write-Host "== Windows 通知端到端自测 ==" -ForegroundColor Cyan
Write-Host "  程序: $exePath"

if (-not (Test-Path $exePath)) {
    Write-Host "  ✗ 找不到程序，请先构建（dotnet build）或传 -Exe 指定路径" -ForegroundColor Red
    exit 2
}

# ---- 第 1 段：应用自带的链路自检 ----
Write-Host ""
Write-Host "-- [1/2] --toastcheck（注册 + 快捷方式 + XML + 投递）--" -ForegroundColor Cyan
$out = & $exePath --toastcheck 2>&1 | Out-String
$code = $LASTEXITCODE
Write-Host $out
Write-Host "  退出码: $code"

if ($code -ne 0) {
    Write-Host "✗ 链路自检失败（$code），见上方失败步骤" -ForegroundColor Red
    exit $code
}

# ---- 第 2 段：通知历史确认 ----
Write-Host "-- [2/2] 通知历史（toast 是否真的进了通知中心）--" -ForegroundColor Cyan
try {
    [Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType=WindowsRuntime] | Out-Null
    [Windows.UI.Notifications.ToastNotification, Windows.UI.Notifications, ContentType=WindowsRuntime] | Out-Null

    $history = [Windows.UI.Notifications.ToastNotificationManager]::History
    $items = $history.GetHistory("HomeworkReminder.Desktop")

    # --toastcheck 测试 toast 的特征：launch URL 里的课堂 id 26111830。
    # 注意历史条目是 WinRT 投影：没有 Shown 时间戳，Payload 也为 null，
    # 内容要从 Content.GetXml() 读（PS5.1 下的实测结论）。
    $hits = 0
    foreach ($t in $items) {
        if ($t.Content.GetXml().Contains("26111830")) { $hits++ }
    }

    Write-Host "  本 AUMID 历史条数: $($items.Count)，含测试 toast 标记: $hits"

    if ($hits -gt 0) {
        Write-Host ""
        Write-Host "结论：通知链路可用，toast 已进入通知中心。PASS" -ForegroundColor Green
        exit 0
    }

    # 投递成功但历史没有：横幅可能弹过但没进操作中心（专注助手/通知设置）。
    Write-Host ""
    Write-Host "结论：投递被平台接受，但通知中心没有记录。" -ForegroundColor Yellow
    Write-Host "      如果刚才屏幕上弹过横幅，链路本身是好的；检查 设置→系统→通知 里本应用是否被关。" -ForegroundColor Yellow
    exit 3
}
catch {
    Write-Host "  ✗ 通知历史读取失败：$($_.Exception.Message)" -ForegroundColor Red
    Write-Host "    （History API 需要 Win10 1703+ 的交互式桌面会话）" -ForegroundColor Yellow
    exit 4
}
