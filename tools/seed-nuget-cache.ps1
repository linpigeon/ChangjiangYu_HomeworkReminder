# Seed a workspace-local NuGet package folder from the machine cache.
#
# Why: .NET cannot reach nuget.org in this sandbox (TLS credential store is
# blocked) and cannot write to the machine cache (D:\DevCache\nuget is outside
# the writable sandbox boundary). NUGET_PACKAGES is pointed at the workspace
# instead, so the packages that a Windows Avalonia build actually needs are
# copied here. Packages for other platforms (webassembly, linux, macos natives,
# mqttnet, ...) are deliberately skipped.
param(
    # 机器级 NuGet 缓存来源。优先级：本参数 > NUGET_PACKAGES 环境变量 >
    # 候选默认值（第一个存在的）：D:\DevCache\nuget、%USERPROFILE%\.nuget\packages。
    [string]$Source,
    # 目标目录，默认取仓库根（脚本上级目录）下的 .nuget-packages。
    [string]$Destination
)
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot

if (-not $Source) { $Source = $env:NUGET_PACKAGES }
if (-not $Source) {
    $Source = @('D:\DevCache\nuget', (Join-Path $env:USERPROFILE '.nuget\packages')) |
        Where-Object { Test-Path $_ } | Select-Object -First 1
}
if (-not $Source) { throw 'no machine NuGet cache found; pass -Source or set NUGET_PACKAGES' }
if (-not $Destination) { $Destination = Join-Path $root '.nuget-packages' }

$src = $Source
$dst = $Destination

$wanted = @(
    'avalonia',
    'avalonia.desktop',
    'avalonia.themes.fluent',
    'avalonia.fonts.inter',
    'avalonia.skia',
    'avalonia.win32',
    'avalonia.native',
    'avalonia.harfbuzz',
    'avalonia.freedesktop',
    'avalonia.freedesktop.atspi',
    'avalonia.x11',
    'avalonia.remote.protocol',
    'avalonia.angle.windows.natives',
    'avalonia.buildservices',
    'communitytoolkit.mvvm',
    'skiasharp',
    'skiasharp.nativeassets.win32',
    'harfbuzzsharp',
    'harfbuzzsharp.nativeassets.win32',
    'microcom.runtime',
    'tmds.dbus.protocol',
    'microsoft.net.illink.tasks',
    'microsoft.netcore.app.runtime.win-x64',
    'microsoft.windowsdesktop.app.runtime.win-x64',
    'microsoft.aspnetcore.app.runtime.win-x64',
    # SkiaSharp/HarfBuzzSharp declare these unconditionally, so restore asks for
    # them even for a Windows-only desktop head.
    'skiasharp.nativeassets.linux',
    'skiasharp.nativeassets.macos',
    'skiasharp.nativeassets.webassembly',
    'harfbuzzsharp.nativeassets.linux',
    'harfbuzzsharp.nativeassets.macos',
    'harfbuzzsharp.nativeassets.webassembly'
)

New-Item -ItemType Directory -Force -Path $dst | Out-Null

$copied = 0
$bytes = 0
foreach ($name in $wanted) {
    $srcPkg = Join-Path $src $name
    if (-not (Test-Path $srcPkg)) { Write-Warning "cache miss: $name"; continue }
    foreach ($verDir in Get-ChildItem $srcPkg -Directory) {
        $target = Join-Path $dst "$name\$($verDir.Name)"
        if (Test-Path $target) { continue }
        New-Item -ItemType Directory -Force -Path $target | Out-Null
        Copy-Item -Path (Join-Path $verDir.FullName '*') -Destination $target -Recurse -Force
        $copied++
        $bytes += (Get-ChildItem $target -Recurse -File -Force | Measure-Object Length -Sum).Sum
    }
}

# The WebView package is not in the machine cache; it comes from the local feed
# in packages\ instead, so restore resolves it without network access.
Write-Host ("seeded {0} package versions, {1:N0} MB" -f $copied, ($bytes / 1MB))
Get-ChildItem $dst -Directory | Measure-Object | ForEach-Object { Write-Host ("total packages in workspace cache: {0}" -f $_.Count) }
