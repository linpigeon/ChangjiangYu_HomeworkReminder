# 发布桌面端为可分发程序。
#
# 只发布 HomeworkReminder.Desktop（桌面头）。Android / iOS / Browser 三个头
# 各有自己的发布流程，不在这里处理。
#
# 用法：
#   .\tools\publish.ps1                       # 自包含单文件（默认，推荐分发）
#   .\tools\publish.ps1 -Mode fdd             # 框架依赖（体积最小，需目标机装运行时）
#   .\tools\publish.ps1 -Mode sc              # 自包含多文件
#   .\tools\publish.ps1 -Mode single -Open    # 发布后打开输出目录
param(
    # fdd = 框架依赖；sc = 自包含多文件；single = 自包含单文件；trim = 单文件+裁剪（体积最小，启动最快）
    [ValidateSet('fdd', 'sc', 'single', 'trim')]
    [string]$Mode = 'single',

    [string]$Runtime = 'win-x64',
    [string]$Configuration = 'Release',
    [switch]$NoPdb,
    [switch]$Open
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

# 受限环境下的还原依赖：包源指向工作区缓存（详见 README「开发环境说明」）。
$env:NUGET_PACKAGES = Join-Path $root '.nuget-packages'
$env:AVALONIA_TELEMETRY_OPTOUT = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

# 正在运行的实例会锁住输出目录。
$running = Get-Process HomeworkReminder.Desktop -ErrorAction SilentlyContinue
if ($running) {
    Write-Host "== stopping $($running.Count) running instance(s) ==" -ForegroundColor Yellow
    $running | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 800
}

$outDir = Join-Path $root "publish\$Mode"
if (Test-Path $outDir) { Remove-Item $outDir -Recurse -Force }

$args = @(
    'publish', 'HomeworkReminder.Desktop\HomeworkReminder.Desktop.csproj',
    '-c', $Configuration,
    '-r', $Runtime,
    '-o', $outDir,
    '-v', 'minimal'
)

switch ($Mode) {
    'fdd' {
        $args += @('--self-contained', 'false')
    }
    'sc' {
        $args += @('--self-contained', 'true')
    }
    'single' {
        $args += @(
            '--self-contained', 'true',
            '-p:PublishSingleFile=true',
            # SkiaSharp / HarfBuzzSharp 是原生库，单文件模式下必须允许自解压，
            # 否则运行时找不到它们。
            '-p:IncludeNativeLibrariesForSelfExtract=true'
        )
    }
    'trim' {
        $args += @(
            '--self-contained', 'true',
            '-p:PublishSingleFile=true',
            '-p:IncludeNativeLibrariesForSelfExtract=true',
            # 部分裁剪：只裁框架与可裁剪包，业务程序集保留（反射式 JSON 依赖它，
            # 配套开关在 csproj 的 JsonSerializerIsReflectionEnabledByDefault）。
            # 注：NativeAOT 需要 ILCompiler 包与 MSVC 工具链，本离线环境做不了。
            '-p:PublishTrimmed=true'
        )
    }
}


Write-Host "== publish ($Mode, $Runtime) ==" -ForegroundColor Cyan
dotnet @args
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

if ($NoPdb) {
    # 直接删掉 pdb，而不是靠 MSBuild 属性。
    # 试过 -p:DebugType=none / -p:DebugSymbols=false /
    # -p:AllowedReferenceRelatedFileExtensions=none，都只能去掉本项目程序集的 pdb；
    # SkiaSharp 与 HarfBuzzSharp 的 pdb 是原生包的内容，仍会照抄进输出目录
    # （两者合计约 100 MB，比主程序还大）。pdb 只含调试符号，发布包里删掉是安全的。
    $pdbs = @(Get-ChildItem $outDir -Recurse -File -Filter '*.pdb')
    if ($pdbs.Count -gt 0) {
        $freed = [math]::Round(($pdbs | Measure-Object Length -Sum).Sum / 1MB, 1)
        $pdbs | Remove-Item -Force
        Write-Host "  已移除 $($pdbs.Count) 个 pdb，省下 $freed MB"
    }
}

$files = Get-ChildItem $outDir -Recurse -File
$totalMb = [math]::Round(($files | Measure-Object Length -Sum).Sum / 1MB, 1)
$exe = Get-ChildItem $outDir -Filter '*.exe' | Sort-Object Length -Descending | Select-Object -First 1

Write-Host ''
Write-Host '== 发布完成 ==' -ForegroundColor Green
Write-Host "  输出目录 : $outDir"
Write-Host "  文件数   : $($files.Count)"
Write-Host "  总大小   : $totalMb MB"
if ($exe) { Write-Host "  主程序   : $($exe.Name) ($([math]::Round($exe.Length / 1MB, 1)) MB)" }

if ($Mode -eq 'fdd') {
    Write-Host ''
    Write-Host '  注意：这是框架依赖发布，目标机需要安装 .NET 10 桌面运行时。' -ForegroundColor Yellow
    Write-Host '        要免安装运行时，请用 -Mode single（自包含单文件）。' -ForegroundColor Yellow
}

Write-Host ''
Write-Host '  提醒：WebView2 运行时是独立组件，不随本程序分发；' -ForegroundColor Yellow
Write-Host '        目标机缺少它时请用登录页的「改用 Cookie 登录」兜底。' -ForegroundColor Yellow

if ($Open) { Start-Process explorer.exe $outDir }
