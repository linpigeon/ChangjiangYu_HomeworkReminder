# Build helper for this workspace.
#
# Two sandbox constraints are baked in here rather than left to memory:
#   1. NUGET_PACKAGES must point at the workspace-local package folder, because
#      the machine-wide default (D:\DevCache\nuget) is outside the writable
#      boundary and .NET cannot reach nuget.org to fetch anything missing.
#   2. Avalonia's build telemetry task writes to
#      %LOCALAPPDATA%\AvaloniaUI\BuildServices\buildtasks.log, which is outside
#      the writable boundary; without opting out, MSBuild fails with MSB4018.
param(
    [string]$Project = 'HomeworkReminder.Desktop\HomeworkReminder.Desktop.csproj',
    [string]$Configuration = 'Debug',
    [switch]$Run,
    [switch]$Clean
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

$env:NUGET_PACKAGES = Join-Path $root '.nuget-packages'
$env:AVALONIA_TELEMETRY_OPTOUT = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

# 本沙箱会剥离 ProgramFiles 系列环境变量，而 NuGet 的 machine-wide 配置定位
# （NuGetEnvironment.GetFolderPath → SpecialFolder.ProgramFilesX86）依赖它们，
# 缺失时 restore 报 "Value cannot be null. (Parameter 'path1')"。补回标准默认值。
if (-not $env:ProgramFiles) { $env:ProgramFiles = 'C:\Program Files' }
if (-not ${env:ProgramFiles(x86)}) { ${env:ProgramFiles(x86)} = 'C:\Program Files (x86)' }
if (-not $env:ProgramData) { $env:ProgramData = 'C:\ProgramData' }

if ($Clean) {
    foreach ($p in @('HomeworkReminder', 'HomeworkReminder.Desktop')) {
        Remove-Item (Join-Path $root "$p\obj"), (Join-Path $root "$p\bin") -Recurse -Force -ErrorAction SilentlyContinue
    }
}

# 正在运行的应用实例会锁住输出目录里的 dll，导致 MSB3027「文件被另一进程占用」。
# 这在「改完代码立刻重建」时几乎必然发生，所以默认自动结束旧实例。
$running = Get-Process HomeworkReminder.Desktop -ErrorAction SilentlyContinue
if ($running) {
    Write-Host "== stopping $($running.Count) running instance(s) ==" -ForegroundColor Yellow
    $running | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 800
}

Write-Host "== restore ==" -ForegroundColor Cyan
dotnet restore $Project -v minimal
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host "== build ==" -ForegroundColor Cyan
dotnet build $Project -c $Configuration -v minimal --no-restore
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

if ($Run) {
    $exe = Get-ChildItem (Join-Path $root "HomeworkReminder.Desktop\bin\$Configuration") -Recurse -Filter 'HomeworkReminder.Desktop.exe' |
        Select-Object -First 1
    if (-not $exe) { Write-Error 'built executable not found'; exit 1 }
    Write-Host "== run: $($exe.FullName) ==" -ForegroundColor Cyan
    & $exe.FullName
}
