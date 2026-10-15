# 发布一个 release：打包 Windows 单文件、生成更新清单、上传到 GitHub 与 Gitea。
#
# 本脚本解决「检查更新 → 直接下载并全量更新」功能对发布侧的三项要求：
#   ① 版本号与 tag 必须一致  —— 否则客户端会陷入更新循环（装了新版仍报旧版号）
#   ② 必须提供 update-manifest.json —— 客户端靠它拿到确切文件名与 SHA256
#   ③ 资产命名保持一致      —— 兜底挑包按「同时含 win-x64 与 .zip」匹配
#
# 用法：
#   .\tools\release.ps1 -Tag v1.4.0                # 校验 + 打包 + 生成清单（不上传）
#   .\tools\release.ps1 -Tag v1.4.0 -Upload        # 再上传到两个源
#   .\tools\release.ps1 -Tag v1.4.0 -Upload -SkipBuild   # 只重新上传已有产物
#
# 凭据（上传时需要，从环境变量读，**不写进仓库**）：
#   GITHUB_TOKEN   —— GitHub PAT，需要 repo 权限
#   GITEA_TOKEN    —— Gitea 访问令牌
param(
    [Parameter(Mandatory = $true)][string]$Tag,
    [string]$Runtime = 'win-x64',
    # 发布模式。默认 trim —— 必须与**历史 release 的构建方式一致**：
    # 历史 release 的 exe 是 41.5 MB，对应 publish.ps1 的 trim 模式；
    # single 模式同样是单文件、却是 101.3 MB（未裁剪），更新包会大约 2.4 倍
    # （17.2 MB → 42.6 MB）。两者都是「内含单个 exe」的单文件发布、格式一致，
    # 只是体积差很多，所以这里固定用 trim。
    [ValidateSet('single', 'trim')]
    [string]$Mode = 'trim',
    [switch]$Upload,
    [switch]$SkipBuild,
    # 发成预发布版（prerelease）。客户端默认**不接收**预发布版，
    # 需要在「设置 → 关于」勾选「接收预发布版更新」才看得到，因此不会打扰正式版用户。
    # tag 形如 v1.4.0-pre，版本号仍需与 tag 去掉 v 后一致。
    [switch]$Prerelease,
    # Android APK 路径（可选）。指定后会按 `HomeworkReminder-<tag>-android.apk` 归档、
    # 登记进更新清单的 android 字段，并随 -Upload 一起上传到两个源。
    # 历史 release 都带 APK，但客户端自动更新只针对桌面端，APK 不参与更新链路。
    [string]$AndroidApk = ''
)

$ErrorActionPreference = 'Stop'
# 创建/上传时使用的预发布标志（贯穿 GitHub 与 Gitea 两段）。
$prereleaseFlag = [bool]$Prerelease
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

# ---- ① 版本号一致性：Tag 与 Directory.Build.props 必须相同 ----
# 这是本项目最容易踩的坑：版本号原先只写在共享项目里，桌面头用的是默认 1.0.0，
# 导致 exe 的版本信息与 tag 对不上。现在版本号集中在 Directory.Build.props。
$cleanTag = $Tag.TrimStart('v', 'V')
$propsPath = Join-Path $root 'Directory.Build.props'
if (-not (Test-Path $propsPath)) {
    Write-Host "== 找不到 $propsPath（版本号的唯一事实来源）==" -ForegroundColor Red
    exit 1
}
$m = Select-String -Path $propsPath -Pattern '<Version>([^<]+)</Version>'
if (-not $m) {
    Write-Host '== Directory.Build.props 里没有 <Version> ==' -ForegroundColor Red
    exit 1
}
$propsVersion = $m.Matches[0].Groups[1].Value.Trim()

# 比较前归一化：都去掉 v 前缀、构建元数据（+sha）与预发布后缀（-pre）。
# 这样 `<Version>1.3.2-pre</Version>` 与 tag `v1.3.2-pre` 能判定为一致，
# 而 `1.3.2` 与 `v1.3.3` 仍会被正确判为不一致。
function Get-NormalizedVersion {
    param([string]$Raw)
    $v = $Raw.Trim()
    if ($v.StartsWith('v') -or $v.StartsWith('V')) { $v = $v.Substring(1) }
    $plus = $v.IndexOf('+'); if ($plus -ge 0) { $v = $v.Substring(0, $plus) }
    $dash = $v.IndexOf('-'); if ($dash -ge 0) { $v = $v.Substring(0, $dash) }
    return $v.Trim()
}
$normProps = Get-NormalizedVersion $propsVersion
$normTag = Get-NormalizedVersion $cleanTag

Write-Host "== 版本校验 ==" -ForegroundColor Cyan
Write-Host "   Tag                    : $Tag"
Write-Host "   Directory.Build.props  : $propsVersion"

if ($normProps -ne $normTag) {
    Write-Host ''
    Write-Host "   版本号不一致！请先把 Directory.Build.props 的 <Version> 改为 $cleanTag" -ForegroundColor Red
    Write-Host '   不改的话：客户端装了新版仍报旧版号 → 每次检查都提示有更新（更新循环）。' -ForegroundColor Yellow
    exit 1
}
Write-Host '   一致 ✓' -ForegroundColor Green

# ---- ② 打包 ----
$pkgDir = Join-Path $root "publish\HomeworkReminder-$Tag-$Runtime"
$zipName = "HomeworkReminder-$Tag-$Runtime.zip"
$zipPath = Join-Path $root "publish\$zipName"

if (-not $SkipBuild) {
    Write-Host ''
    Write-Host "== 打包（single / $Runtime / $cleanTag）==" -ForegroundColor Cyan

    # publish.ps1 输出到 publish\<Mode>，再整理成带版本号的目录名，
    # 与实际 release 里的目录结构保持一致。
    & (Join-Path $PSScriptRoot 'publish.ps1') -Mode $Mode -Runtime $Runtime -Configuration Release -Version $cleanTag -NoPdb -OnlineRestore
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    $src = Join-Path $root (Join-Path 'publish' $Mode)
    if (Test-Path $pkgDir) { Remove-Item $pkgDir -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $pkgDir | Out-Null

    $exe = Get-ChildItem $src -Filter '*.exe' | Sort-Object Length -Descending | Select-Object -First 1
    if (-not $exe) {
        Write-Host '== 发布目录里没有 exe ==' -ForegroundColor Red
        exit 1
    }
    Copy-Item $exe.FullName $pkgDir

    # 关键：绝不把 WebView2 缓存打进包（实测 67 MB，且含本机登录 cookie）。
    $webview = Get-ChildItem $src -Directory -Filter '*.WebView2' -ErrorAction SilentlyContinue
    if ($webview) {
        Write-Host "   已排除 WebView2 缓存目录（$($webview.Count) 个）" -ForegroundColor Yellow
    }
}

# ---- 打包 zip（只含 exe）----
$exeInPkg = Get-ChildItem $pkgDir -Filter '*.exe' | Sort-Object Length -Descending | Select-Object -First 1
if (-not $exeInPkg) {
    Write-Host "== $pkgDir 里没有 exe，无法打包 ==" -ForegroundColor Red
    exit 1
}
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }

Write-Host ''
Write-Host "== 生成 $zipName ==" -ForegroundColor Cyan
Compress-Archive -Path $exeInPkg.FullName -DestinationPath $zipPath -CompressionLevel Optimal
$zipSize = (Get-Item $zipPath).Length
$zipHash = (Get-FileHash $zipPath -Algorithm SHA256).Hash

# ---- ③ 生成更新清单 ----
# 客户端优先读它：有了确切的文件名与 SHA256，就不必猜资产命名、也能校验完整性
# （实测 release 正文与 HTTP 头里都没有哈希，所以这份清单是校验的唯一来源）。
$manifest = [ordered]@{
    version = $cleanTag
    file    = $zipName
    sha256  = $zipHash
    size    = $zipSize
    entry   = $exeInPkg.Name
}

# Android APK（可选）：历史 release 都带一份，按同样命名归档并登记进清单。
# 它单独一个文件，不属于「更新包」链路（客户端更新只针对桌面端），
# 因此只在清单里加一个 android 字段供下载页/脚本使用。
$apkPath = $null
$apkName = $null
if ($AndroidApk) {
    if (-not (Test-Path $AndroidApk)) {
        Write-Host "== 指定的 Android APK 不存在：$AndroidApk ==" -ForegroundColor Red
        exit 1
    }
    $apkName = "HomeworkReminder-$Tag-android.apk"
    $apkPath = Join-Path $root "publish\$apkName"
    Copy-Item $AndroidApk $apkPath -Force
    $manifest['android'] = [ordered]@{
        file   = $apkName
        sha256 = (Get-FileHash $apkPath -Algorithm SHA256).Hash
        size   = (Get-Item $apkPath).Length
    }
}

$manifestPath = Join-Path $root 'publish\update-manifest.json'
$manifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $manifestPath -Encoding UTF8

Write-Host ''
Write-Host '== 产物 ==' -ForegroundColor Green
Write-Host "   程序     : $($exeInPkg.FullName) ($([math]::Round($exeInPkg.Length/1MB,1)) MB)"
Write-Host "   更新包   : $zipPath ($([math]::Round($zipSize/1MB,1)) MB)"
Write-Host "   SHA256   : $zipHash"
if ($apkPath) {
    Write-Host "   Android  : $apkPath ($([math]::Round((Get-Item $apkPath).Length/1MB,1)) MB)"
}
Write-Host "   更新清单 : $manifestPath"
Write-Host ''
Write-Host '   请把下面这些文件作为同一个 release 的资产上传：' -ForegroundColor Yellow
Write-Host "     - $zipName"
if ($apkName) { Write-Host "     - $apkName" }
Write-Host "     - update-manifest.json"

# ---- ④ 上传（可选）----
if (-not $Upload) {
    Write-Host ''
    Write-Host '   （未指定 -Upload，跳过上传。加 -Upload 可自动上传到 GitHub 与 Gitea。）' -ForegroundColor Yellow
    exit 0
}

$githubRepo = 'linpigeon/ChangjiangYu_HomeworkReminder'
$giteaApi = 'https://git.pigeon-lin.top/api/v1/repos/lin/ChangjiangYu_HomeworkReminder'
$giteaDownload = 'https://git.pigeon-lin.top/lin/ChangjiangYu_HomeworkReminder/releases/download'

Write-Host ''
Write-Host '== 上传 ==' -ForegroundColor Cyan

function Test-Release {
    param([string]$Url, [hashtable]$Headers)
    try {
        $r = Invoke-RestMethod -Uri $Url -Headers $Headers -TimeoutSec 30 -ErrorAction Stop
        return $r
    } catch { return $null }
}

# --- GitHub ---
if ($env:GITHUB_TOKEN) {
    $ghHeaders = @{
        Authorization          = "Bearer $($env:GITHUB_TOKEN)"
        Accept                 = 'application/vnd.github+json'
        'User-Agent'           = 'HomeworkReminder-release'
    }
    $ghApi = "https://api.github.com/repos/$githubRepo"
    $ghRelease = Test-Release -Url "$ghApi/releases/tags/$Tag" -Headers $ghHeaders
    if (-not $ghRelease) {
        Write-Host "   GitHub: 创建 release $Tag"
        $body = @{ tag_name = $Tag; name = $Tag; draft = $false; prerelease = $prereleaseFlag } | ConvertTo-Json
        $ghRelease = Invoke-RestMethod -Uri "$ghApi/releases" -Method Post -Headers $ghHeaders `
            -ContentType 'application/json' -Body $body -TimeoutSec 60
    } else {
        Write-Host "   GitHub: release $Tag 已存在"
    }

    foreach ($asset in (@($zipPath, $manifestPath) + @($apkPath | Where-Object { $_ }))) {
        $name = Split-Path $asset -Leaf
        $existing = $ghRelease.assets | Where-Object { $_.name -eq $name }
        if ($existing) {
            Write-Host "   GitHub: 删除同名旧资产 $name"
            Invoke-RestMethod -Uri "$ghApi/releases/assets/$($existing.id)" -Method Delete -Headers $ghHeaders -TimeoutSec 60 | Out-Null
        }
        $uploadUrl = ($ghRelease.upload_url -replace '\{\?.*\}', '') + "?name=$name"
        Write-Host "   GitHub: 上传 $name"
        Invoke-RestMethod -Uri $uploadUrl -Method Post -Headers $ghHeaders `
            -ContentType 'application/octet-stream' -InFile $asset -TimeoutSec 900 | Out-Null
    }
} else {
    Write-Host '   GitHub: 跳过（未设置 GITHUB_TOKEN）' -ForegroundColor Yellow
}

function Send-GiteaAsset {
    <#
      手工拼 multipart/form-data 上传附件。
      刻意不用 Invoke-RestMethod 的 -Form：那个参数要 PowerShell 6+，
      而发布机可能只有 PowerShell 5.1（本仓库的开发机就是 5.1）。
    #>
    param([string]$Uri, [hashtable]$Headers, [string]$FieldName, [string]$FilePath)

    $boundary = [Guid]::NewGuid().ToString('N')
    $fileName = [System.IO.Path]::GetFileName($FilePath)
    $fileBytes = [System.IO.File]::ReadAllBytes($FilePath)
    $enc = [System.Text.Encoding]::UTF8

    $head = "--$boundary`r`n" +
            "Content-Disposition: form-data; name=`"$FieldName`"; filename=`"$fileName`"`r`n" +
            "Content-Type: application/octet-stream`r`n`r`n"
    $tail = "`r`n--$boundary--`r`n"

    $headBytes = $enc.GetBytes($head)
    $tailBytes = $enc.GetBytes($tail)
    $body = New-Object byte[] ($headBytes.Length + $fileBytes.Length + $tailBytes.Length)
    [Array]::Copy($headBytes, 0, $body, 0, $headBytes.Length)
    [Array]::Copy($fileBytes, 0, $body, $headBytes.Length, $fileBytes.Length)
    [Array]::Copy($tailBytes, 0, $body, $headBytes.Length + $fileBytes.Length, $tailBytes.Length)

    $sendHeaders = @{}
    foreach ($k in $Headers.Keys) { $sendHeaders[$k] = $Headers[$k] }
    $sendHeaders['Content-Type'] = "multipart/form-data; boundary=$boundary"

    Invoke-RestMethod -Uri $Uri -Method Post -Headers $sendHeaders `
        -Body $body -TimeoutSec 900 | Out-Null
}

# --- Gitea ---
if ($env:GITEA_TOKEN) {
    $gtHeaders = @{ Authorization = "token $($env:GITEA_TOKEN)"; 'User-Agent' = 'HomeworkReminder-release' }
    $gtRelease = Test-Release -Url "$giteaApi/releases/tags/$Tag" -Headers $gtHeaders
    if (-not $gtRelease) {
        Write-Host "   Gitea: 创建 release $Tag"
        $body = @{ tag_name = $Tag; name = $Tag; draft = $false; prerelease = $prereleaseFlag } | ConvertTo-Json
        $gtRelease = Invoke-RestMethod -Uri "$giteaApi/releases" -Method Post -Headers $gtHeaders `
            -ContentType 'application/json' -Body $body -TimeoutSec 60
    } else {
        Write-Host "   Gitea: release $Tag 已存在"
    }

    foreach ($asset in (@($zipPath, $manifestPath) + @($apkPath | Where-Object { $_ }))) {
        $name = Split-Path $asset -Leaf
        $existing = $gtRelease.assets | Where-Object { $_.name -eq $name }
        if ($existing) {
            Write-Host "   Gitea: 删除同名旧资产 $name"
            Invoke-RestMethod -Uri "$giteaApi/releases/$($gtRelease.id)/assets/$($existing.id)" `
                -Method Delete -Headers $gtHeaders -TimeoutSec 60 | Out-Null
        }
        Write-Host "   Gitea: 上传 $name"
        # Gitea 用 multipart/form-data 上传附件。
        # 刻意不用 Invoke-RestMethod 的 -Form：那个参数要 PowerShell 6+，
        # 而发布机可能只有 PowerShell 5.1（本仓库的开发机就是 5.1）。
        Send-GiteaAsset -Uri "$giteaApi/releases/$($gtRelease.id)/assets?name=$name" `
            -Headers $gtHeaders -FieldName 'attachment' -FilePath $asset
    }
} else {
    Write-Host '   Gitea: 跳过（未设置 GITEA_TOKEN）' -ForegroundColor Yellow
}

Write-Host ''
Write-Host '== 完成 ==' -ForegroundColor Green
Write-Host "   客户端将在下次「检查更新」时看到 $Tag，并可一键下载更新。"
