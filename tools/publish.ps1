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
#   .\tools\publish.ps1 -Version 1.3.2        # 指定版本（应等于 release tag）
param(
    # fdd = 框架依赖；sc = 自包含多文件；single = 自包含单文件；trim = 单文件+裁剪（体积最小，启动最快）
    [ValidateSet('fdd', 'sc', 'single', 'trim')]
    [string]$Mode = 'single',

    [string]$Runtime = 'win-x64',
    [string]$Configuration = 'Release',
    # 发布版本号。留空则用根目录 Directory.Build.props 里的值；
    # 传值（通常来自 release tag）时会被烘进 exe 的版本信息。
    # **必须与 release tag 一致**，否则「检查更新」会陷入更新循环：
    #   远端 tag=v1.3.2 → 下载安装 → 程序仍报 1.3.1 → 再次提示有更新。
    [string]$Version = '',
    [switch]$NoPdb,
    [switch]$Open,
    # 联网还原。
    # 为什么需要它：仓库根的 NuGet.Config 把包源 `<clear />` 成只有本地 packages/，
    # 常规 build 够用，但 **自包含发布需要 runtime pack**
    # （Microsoft.NETCore.App.Runtime.win-x64 等），本地文件夹源里没有，
    # 于是 publish 会以 NU1101 失败。加这个开关会把 nuget.org 作为**额外源**传给
    # restore/publish（不改动 NuGet.Config 本身），在能联网的机器上完成还原。
    [switch]$OnlineRestore
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

# 联网还原：把 nuget.org 作为**额外源**（不动 NuGet.Config）。见 -OnlineRestore 的说明。
if ($OnlineRestore) {
    Write-Host '== 联网还原（额外源 nuget.org）==' -ForegroundColor Cyan
    $args += @('--source', 'https://api.nuget.org/v3/index.json', '--source', (Join-Path $root 'packages'))
}

# 版本号：优先用显式传入的（应来自 release tag），否则用 Directory.Build.props 的值。
# 通过 -p:Version 覆盖，保证 exe 的 FileVersion/ProductVersion 与 tag 一致——
# 更新包版本校验与「关于」页显示都依赖它。
$effectiveVersion = $Version
if (-not $effectiveVersion) {
    $propsPath = Join-Path $root 'Directory.Build.props'
    if (Test-Path $propsPath) {
        $m = Select-String -Path $propsPath -Pattern '<Version>([^<]+)</Version>'
        if ($m) { $effectiveVersion = $m.Matches[0].Groups[1].Value.Trim() }
    }
}
if (-not $effectiveVersion) {
    Write-Host '== 无法确定版本号：Directory.Build.props 里没有 <Version>，也没传 -Version ==' -ForegroundColor Red
    exit 1
}
$args += @("-p:Version=$effectiveVersion")
Write-Host "== 版本号：$effectiveVersion ==" -ForegroundColor Cyan

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
