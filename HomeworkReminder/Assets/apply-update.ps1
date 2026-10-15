<#
  更新替换器（由主程序释放到临时目录后启动）。

  职责只有一件：**在父进程退出后，把目标 exe 换成新版本，然后重启它。**

  为什么必须是独立进程：Windows 上运行中的 exe 不能覆盖、不能删除，
  但**可以改名**（已实测）。所以父进程先启动本脚本、再自行退出，
  等锁释放后这里再替换；直接覆盖失败时用「改名让位」兜底。

  安全铁律：**本脚本只允许修改一个文件——目标 exe。**
  不删目录、不动 exe 旁边的 *.WebView2（那是登录态缓存）、不碰 %LOCALAPPDATA%。

  用 Windows PowerShell 5.1 兼容语法书写（不依赖 PowerShell 7）。
#>
[CmdletBinding()]
param(
  [Parameter(Mandatory = $true)][string]$TargetExe,
  [Parameter(Mandatory = $true)][string]$NewExe,
  [Parameter(Mandatory = $true)][int]$ParentPid,
  [string]$ExpectedSha256 = '',
  # 重启时要带上的参数，**每行一个**地写在这个文件里，而不是走命令行。
  #
  # 为什么不用 string[] 参数：PowerShell 从命令行绑定数组时，
  # `-RestartArgs a b` 只把 a 绑给参数、b 被当成无法识别的位置参数，
  # 脚本会直接报错退出（实测）。而应用侧无论用 ArgumentList 还是空格拼接，
  # 都绕不开这个绑定规则。用文件传递彻底规避，且不受参数里的空格/引号/& 影响。
  [string]$ArgsFile = '',
  [switch]$Restart
)

$ErrorActionPreference = 'Stop'
$logPath = Join-Path $env:TEMP 'HomeworkReminder-update.log'

function Write-Log {
  param([string]$Message)
  $line = '[{0}] {1}' -f (Get-Date -Format 'HH:mm:ss'), $Message
  try { Add-Content -LiteralPath $logPath -Value $line -Encoding UTF8 } catch { }
  Write-Host $line
}

function Get-Sha256 {
  param([string]$Path)
  if (-not (Test-Path -LiteralPath $Path)) { return $null }
  return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToUpperInvariant()
}

$exitCode = 0
try {
  Write-Log "更新器启动：目标=$TargetExe"
  Write-Log "            新文件=$NewExe 父进程=$ParentPid"

  if (-not (Test-Path -LiteralPath $TargetExe)) { throw "目标程序不存在：$TargetExe" }
  if (-not (Test-Path -LiteralPath $NewExe)) { throw "新版本文件不存在：$NewExe" }

  # 进入替换前先确认新文件本身是完整的（校验放在最前面，避免把坏文件换上去）。
  if ($ExpectedSha256) {
    $actualNew = Get-Sha256 -Path $NewExe
    if ($actualNew -ne $ExpectedSha256.ToUpperInvariant()) {
      throw "新文件哈希不符：期望 $ExpectedSha256，实际 $actualNew"
    }
    Write-Log '新文件哈希校验通过'
  } else {
    Write-Log '警告：未提供哈希，跳过新文件校验（依赖主程序侧的解包校验）'
  }

  # ---- 等父进程退出 ----
  $deadline = (Get-Date).AddSeconds(30)
  while ((Get-Date) -lt $deadline) {
    $alive = $null
    try { $alive = Get-Process -Id $ParentPid -ErrorAction Stop } catch { $alive = $null }
    if ($null -eq $alive) { break }
    Start-Sleep -Milliseconds 250
  }
  $still = $null
  try { $still = Get-Process -Id $ParentPid -ErrorAction Stop } catch { $still = $null }
  if ($null -ne $still) {
    Write-Log '父进程 30 秒内未退出，强制结束它'
    try { Stop-Process -Id $ParentPid -Force -ErrorAction Stop } catch { }
    Start-Sleep -Milliseconds 1500
  }
  Write-Log '父进程已退出，开始替换'

  # ---- 替换（覆盖失败时用改名让位兜底）----
  $oldPath = "$TargetExe.old"
  $swapped = $false
  for ($i = 1; $i -le 20; $i++) {
    try {
      Move-Item -LiteralPath $NewExe -Destination $TargetExe -Force -ErrorAction Stop
      $swapped = $true
      break
    } catch {
      # 覆盖写不进去：把旧文件改名让位（**改名在文件被占用时也是允许的**），再放新文件。
      try {
        if (Test-Path -LiteralPath $oldPath) { Remove-Item -LiteralPath $oldPath -Force -ErrorAction SilentlyContinue }
        Move-Item -LiteralPath $TargetExe -Destination $oldPath -Force -ErrorAction Stop
        Move-Item -LiteralPath $NewExe -Destination $TargetExe -Force -ErrorAction Stop
        $swapped = $true
        Write-Log "第 $i 次尝试：以「改名让位」方式完成替换"
        break
      } catch {
        Start-Sleep -Milliseconds 500
      }
    }
  }

  if (-not $swapped) {
    # 20 次（约 10 秒）都没换成：尽量把现场恢复成「旧版本可用」。
    if ((Test-Path -LiteralPath $oldPath) -and -not (Test-Path -LiteralPath $TargetExe)) {
      try { Move-Item -LiteralPath $oldPath -Destination $TargetExe -Force -ErrorAction Stop } catch { }
    }
    throw '替换失败：文件可能被其他程序占用（杀毒软件/资源管理器预览）。请关闭后手动替换。'
  }

  # ---- 校验替换结果 ----
  if ($ExpectedSha256) {
    $actual = Get-Sha256 -Path $TargetExe
    if ($actual -ne $ExpectedSha256.ToUpperInvariant()) {
      # 换上去的文件不对：回滚到旧版本，保证用户至少还能用。
      if (Test-Path -LiteralPath $oldPath) {
        try {
          Move-Item -LiteralPath $TargetExe -Destination "$TargetExe.bad" -Force -ErrorAction Stop
          Move-Item -LiteralPath $oldPath -Destination $TargetExe -Force -ErrorAction Stop
          Write-Log '替换后校验失败，已回滚到旧版本'
        } catch {
          Write-Log '替换后校验失败，且回滚失败——请手动重新安装'
        }
      }
      throw "替换后校验失败：期望 $ExpectedSha256，实际 $actual"
    }
    Write-Log '替换后哈希校验通过'
  }

  # 清掉旧版本备份（回滚已不可能，但此刻新版本已验证可用）
  if (Test-Path -LiteralPath $oldPath) {
    try { Remove-Item -LiteralPath $oldPath -Force -ErrorAction Stop; Write-Log '已清理旧版本备份' }
    catch { Write-Log '旧版本备份删除失败（可忽略，下次启动会再试）' }
  }

  # ---- 重启 ----
  if ($Restart) {
    # 从文件读回要转发的参数（每行一个）。读不到就当没有参数，不要因此失败。
    $restartArgs = @()
    if ($ArgsFile -and (Test-Path -LiteralPath $ArgsFile)) {
      try {
        $restartArgs = @(Get-Content -LiteralPath $ArgsFile -Encoding UTF8 |
          Where-Object { $_.Length -gt 0 })
        Write-Log "将转发 $($restartArgs.Count) 个参数"
      } catch {
        Write-Log "读取参数文件失败（将不带参数重启）：$($_.Exception.Message)"
        $restartArgs = @()
      }
    }

    if ($restartArgs.Count -eq 0) {
      Start-Process -FilePath $TargetExe -WorkingDirectory (Split-Path -Parent $TargetExe)
    } else {
      # 每个参数单独加引号并转义内部引号，避免含空格的路径被拆开。
      $argString = ($restartArgs | ForEach-Object { '"' + ($_ -replace '"', '\"') + '"' }) -join ' '
      Start-Process -FilePath $TargetExe -ArgumentList $argString -WorkingDirectory (Split-Path -Parent $TargetExe)
    }
    Write-Log '已重启新版本'
  } else {
    Write-Log '已替换（未要求重启）'
  }

  Write-Log '更新完成'
}
catch {
  $exitCode = 1
  Write-Log "更新失败：$($_.Exception.Message)"
}

# ---- 清理本次下载目录 ----
# 只删「本次更新自己的下载目录」，且**只删它自己**：
# 传进来的是下载目录路径，不是安装目录——绝不在这里碰目标 exe 所在目录。
if ($NewExe) {
  try {
    $downloadDir = Split-Path -Parent $NewExe
    $tempRoot = [System.IO.Path]::GetTempPath().TrimEnd('\')
    # 双保险，避免递归删错目录：
    #   ① 必须在临时目录下；
    #   ② 路径里必须含「\HomeworkReminder\」这一段（即 %TEMP%\HomeworkReminder\update）。
    # 注意不能只比较叶子目录名：下载目录的叶子是 "update"，不是 "HomeworkReminder"
    # ——演练时正是这里写错，导致清理被静默跳过。
    $looksOurs = $downloadDir -match '\\HomeworkReminder\\[^\\]+$'
    if ($downloadDir -and
        $downloadDir.StartsWith($tempRoot, [System.StringComparison]::OrdinalIgnoreCase) -and
        $looksOurs) {
      Remove-Item -LiteralPath $downloadDir -Recurse -Force -ErrorAction SilentlyContinue
      Write-Log "已清理下载目录：$downloadDir"
    } else {
      Write-Log "跳过下载目录清理（不像本程序的下载目录）：$downloadDir"
    }
  } catch { }
}

exit $exitCode
