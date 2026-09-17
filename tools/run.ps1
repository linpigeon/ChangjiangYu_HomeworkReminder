# Launch the app from a built output, pointing its data directory inside the
# workspace. The sandbox forbids writing to %LOCALAPPDATA%, and the app also
# needs the 雨课堂 session imported there before it can sync.
param(
    [string]$Configuration = 'Debug',
    [switch]$SkipBuild,
    # 探针导出的 cookie 文件（Playwright 格式）。优先级：本参数 >
    # HR_PROBE_COOKIES 环境变量 > 仓库内 .appdata\session-cookies.json >
    # 旧探针目录（仅在本机存在时兜底，保持原默认行为）。
    [string]$ProbeCookies = $env:HR_PROBE_COOKIES
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

if (-not $SkipBuild) {
    & (Join-Path $PSScriptRoot 'build.ps1') -Configuration $Configuration
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

$dataDir = Join-Path $root '.appdata'
New-Item -ItemType Directory -Force -Path $dataDir | Out-Null
$env:HOMEWORKREMINDER_DATA_DIR = $dataDir

# Seed the login state from the probe session if the app has none yet.
$sessionFile = Join-Path $dataDir 'session.json'
if (-not (Test-Path $sessionFile)) {
    if (-not $ProbeCookies) {
        # 仓库内默认位置优先；本机旧探针目录作为兜底（存在才用）。
        $candidates = @(
            (Join-Path $dataDir 'session-cookies.json'),
            'D:\DS_Workpalce\ykt-probe\session-cookies.json'
        )
        $ProbeCookies = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
    }
    if ($ProbeCookies -and (Test-Path $ProbeCookies)) {
        $env:HR_SESSION_OUT = $sessionFile
        node (Join-Path $PSScriptRoot 'import-session.mjs') $ProbeCookies $sessionFile
    }
}

$exe = Get-ChildItem (Join-Path $root "HomeworkReminder.Desktop\bin\$Configuration") -Recurse -Filter 'HomeworkReminder.Desktop.exe' |
    Select-Object -First 1
if (-not $exe) { throw 'built executable not found' }

Write-Host "== launch ==" -ForegroundColor Cyan
Write-Host "  exe      : $($exe.FullName)"
Write-Host "  data dir : $dataDir"
& $exe.FullName
