using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace HomeworkReminder.Services;

/// <summary>
/// Release 信息。GitHub 与 Gitea 的 release API 返回结构同构，共用一份 DTO。
/// <para>
/// 注意用的是「列出 release」接口而不是 <c>/releases/latest</c>：
/// 后者不返回 <c>assets</c>，拿不到下载地址，无法支撑直接更新。
/// </para>
/// </summary>
public sealed class ReleaseInfo
{
    [JsonPropertyName("tag_name")] public string? TagName { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("html_url")] public string? HtmlUrl { get; set; }
    [JsonPropertyName("body")] public string? Body { get; set; }
    [JsonPropertyName("draft")] public bool Draft { get; set; }
    [JsonPropertyName("prerelease")] public bool Prerelease { get; set; }
    [JsonPropertyName("assets")] public List<ReleaseAsset>? Assets { get; set; }

    /// <summary>
    /// 更新清单的**原始 JSON 文本**（由 <see cref="UpdateService"/> 在查询阶段取回后填入）。
    /// <para>
    /// 存原文而不是反序列化后的对象：DTO 保持纯数据、可被源生成序列化上下文处理，
    /// 解析失败也能在挑选包里降级处理（坏清单不该让整个更新不可用）。
    /// 不参与 JSON 序列化（两个方向都不需要，加 <see cref="JsonIgnoreAttribute"/> 省掉噪音）。
    /// </para>
    /// </summary>
    [JsonIgnore] public string? ManifestJson { get; set; }
}

/// <summary>Release 附件。</summary>
public sealed class ReleaseAsset
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("size")] public long Size { get; set; }
    [JsonPropertyName("browser_download_url")] public string? BrowserDownloadUrl { get; set; }
}

/// <summary>
/// 更新清单（release 里的 <c>update-manifest.json</c>）。
/// <para>
/// 存在的意义：把「更新」与「资产命名」解耦，并提供一个可信的 SHA256。
/// 没有它就只能靠文件名猜包，且无法校验完整性（实测 release 正文里没有哈希）。
/// </para>
/// </summary>
public sealed class UpdateManifest
{
    [JsonPropertyName("version")] public string? Version { get; set; }
    [JsonPropertyName("file")] public string? File { get; set; }
    [JsonPropertyName("sha256")] public string? Sha256 { get; set; }
    [JsonPropertyName("size")] public long Size { get; set; }
    /// <summary>该包内单文件的名字（单文件发布时就是 exe 名）。</summary>
    [JsonPropertyName("entry")] public string? Entry { get; set; }
}

/// <summary>
/// 版本号、更新检查与**更新包下载**。更新源是作者的 GitHub 与 Gitea 两个镜像仓库，
/// 取两边返回中较新的版本号对比本地版本。
/// </summary>
public static class UpdateService
{
    /// <summary>更新源（名称 + 列出 release 的 API + 仓库页面地址）。</summary>
    public static readonly (string Name, string ApiUrl, string RepoUrl)[] Sources =
    [
        ("GitHub",
            "https://api.github.com/repos/linpigeon/ChangjiangYu_HomeworkReminder/releases?per_page=10",
            "https://github.com/linpigeon/ChangjiangYu_HomeworkReminder"),
        ("Gitea",
            "https://git.pigeon-lin.top/api/v1/repos/lin/ChangjiangYu_HomeworkReminder/releases?limit=10",
            "https://git.pigeon-lin.top/lin/ChangjiangYu_HomeworkReminder"),
    ];

    /// <summary>更新清单在 release 里的资产名。</summary>
    public const string ManifestAssetName = "update-manifest.json";

    /// <summary>本平台更新包的文件名特征（用于在没有 manifest 时兜底挑包）。</summary>
    private static readonly string[] PlatformAssetMarkers = ["win-x64", ".zip"];

    // GitHub API 要求带 User-Agent，否则 403。
    private static readonly HttpClient Http = CreateClient();

    /// <summary>查询 release 列表的超时。GitHub 从国内访问经常要十几秒，15 秒太紧。</summary>
    private static readonly TimeSpan MetadataTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// 取更新清单的超时。清单只是几行 JSON，慢到这个程度基本就是被墙/断网了；
    /// 它本来就是可选资产，超时按「没有清单」处理即可，绝不能拖垮整次检查。
    /// </summary>
    private static readonly TimeSpan ManifestTimeout = TimeSpan.FromSeconds(20);

    /// <summary>下载更新包时，只对「连上并拿到响应头」限时。</summary>
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// 传输阶段的总预算。给慢网留足余地（约 30 KB/s 也能传完 17 MB），
    /// 同时保证不会无限挂着——下载期间界面的「取消」是禁用的。
    /// </summary>
    private static readonly TimeSpan DownloadBudget = TimeSpan.FromMinutes(10);

    private static HttpClient CreateClient()
    {
        // 刻意**不设** HttpClient.Timeout：它是一刀切的整请求上限，同一个值既要卡
        // 「查一个小 JSON」，又要卡「下载十几 MB 更新包」——前者 30 秒足够，后者在慢网速下
        // 30 秒根本下不完。实测 15 秒时，慢网下的更新包下载会被中途掐断。
        // 现在改成：元数据查询用显式 CancellationToken 超时（见 GetStringAsync），
        // 下载只限连接阶段，流式读取交给调用方的 ct。
        var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("HomeworkReminder/" + CurrentVersion);
        return http;
    }

    /// <summary>
    /// 把异常翻译成**给用户看**的说明。
    /// <para>
    /// 为什么不直接 <c>ex.Message</c>：HttpClient 的超时原文是
    /// <c>The request was canceled due to the configured HttpClient.Timeout of 15 seconds elapsing.</c>
    /// ——纯英文、提到用户根本没配过的「HttpClient.Timeout」，用户既看不懂，
    /// 也无法据此判断该重试、该换网络还是该等一等。实测用户截图里出现的就是这一句。
    /// </para>
    /// </summary>
    public static string DescribeFailure(Exception ex) => ex switch
    {
        // 顺序要紧：TaskCanceledException 是 OperationCanceledException 的子类，
        // 更具体的必须排在前面。
        TaskCanceledException => "请求超时（网络较慢，或更新源当前不可达）",
        OperationCanceledException => "已取消",
        TimeoutException => ex.Message,
        HttpRequestException => $"网络不可达：{ex.Message}",
        _ => ex.Message,
    };

    /// <summary>
    /// 带**显式超时**的 GET，并把「超时」与「用户取消」分开报告。
    /// <para>
    /// 为什么要分开：两者抛出的都是 <see cref="OperationCanceledException"/> 的子类
    /// （超时是 <see cref="TaskCanceledException"/>），<b>只看异常类型分不出来</b>。
    /// 原先用 <c>catch (OperationCanceledException) { throw; }</c> 想「让用户取消透传」，
    /// 结果把超时也一起透传了出去，最终界面上出现的是 .NET 的原文：
    /// 「The request was canceled due to the configured HttpClient.Timeout of 15 seconds elapsing.」
    /// 唯一可靠的判据是**调用方自己的令牌有没有被取消**。
    /// </para>
    /// </summary>
    private static async Task<string> GetStringAsync(string url, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            return await Http.GetStringAsync(url, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // 调用方没取消 → 是我们自己的 CancelAfter 到点，即超时。
            throw new TimeoutException($"请求超时（{timeout.TotalSeconds:0} 秒内没有响应）");
        }
    }

    /// <summary>
    /// 当前应用版本（发布时与 release tag 保持一致）。
    /// <para>
    /// 读的是**入口程序集**（即 exe）而不是本共享库：版本号现在定义在根目录的
    /// <c>Directory.Build.props</c>，各头项目继承同一个值；但读 exe 更贴近事实——
    /// 用户跑的就是那个 exe，报它的版本才不会出现「显示 1.3.1 但 exe 属性是 1.0.0」。
    /// </para>
    /// </summary>
    public static string CurrentVersion
    {
        get
        {
            var assembly = Assembly.GetEntryAssembly() ?? typeof(UpdateService).Assembly;
            var info = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            // 构建可能追加「+commitsha」后缀，展示与比较时去掉。
            var version = info ?? assembly.GetName().Version?.ToString() ?? "0.0.0";
            var plus = version.IndexOf('+');
            return plus >= 0 ? version[..plus] : version;
        }
    }

    /// <summary>单个源的查询结果：Release 与 Error 必有其一为 null。</summary>
    public sealed record SourceResult(string Source, ReleaseInfo? Release, string? Error);

    /// <summary>两个源的汇总结果。</summary>
    public sealed record Outcome(IReadOnlyList<SourceResult> Results)
    {
        /// <summary>所有成功源中版本最新的一条；全部失败时为 null。</summary>
        public SourceResult? Best =>
            Results
                .Where(r => r.Release?.TagName is not null && TryParseVersion(r.Release.TagName, out _))
                .OrderByDescending(r => ParseVersion(r.Release!.TagName!))
                .FirstOrDefault();

        /// <summary>各源的失败原因（「源：原因」）。</summary>
        public IEnumerable<string> Errors =>
            Results.Where(r => r.Error is not null).Select(r => $"{r.Source}：{r.Error}");
    }

    /// <summary>并行查询全部更新源，单源失败不影响其他源。</summary>
    /// <param name="includePrerelease">
    /// 是否把**预发布版**也纳入候选。默认 false——预发布是给测试用的，
    /// 推给正式版用户会让所有人被提示升级到一个尚未验证的版本。
    /// 草稿（draft）任何时候都不算候选。
    /// </param>
    /// <param name="ct">取消令牌。</param>
    public static async Task<Outcome> CheckAsync(bool includePrerelease = false, CancellationToken ct = default)
    {
        var tasks = Sources
            .Select(s => QuerySourceAsync(s.Name, s.ApiUrl, s.RepoUrl, includePrerelease, ct))
            .ToArray();
        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 只有**调用方真的取消**才透传；超时已在 QuerySourceAsync 内转成
            // TimeoutException 并收敛进 SourceResult，不会走到这里。
            throw;
        }
        catch (Exception)
        {
            // 单源异常已收敛进 SourceResult，WhenAll 只会在取消时抛出。
        }
        var results = tasks.Select(t => t.IsCompletedSuccessfully
            ? t.Result
            : new SourceResult("(未知)", null, "查询未完成")).ToArray();

        // 清单在这次查询里一并取回：检查更新时本来就联网，
        // 顺带取回来可以让「挑包」保持同步、无额外等待，也让 UI 能提前知道有没有校验哈希。
        await Task.WhenAll(results.Select(r => ResolveManifestAsync(r.Release, ct))).ConfigureAwait(false);

        return new Outcome(results);
    }

    /// <summary>
    /// 取回 release 里的更新清单并填入 <see cref="ReleaseInfo.ManifestJson"/>。
    /// 没有清单、取回失败、或清单过大时都只是留空——**不视为查询失败**，
    /// 因为按文件名兜底挑包仍然可用（只是没有哈希可校验）。
    /// </summary>
    private static async Task ResolveManifestAsync(ReleaseInfo? release, CancellationToken ct)
    {
        if (release is null) return;

        // 只有「确实比本机新」才值得去取清单：已是最新时清单毫无用处（挑了包也用不上），
        // 而它的下载地址在 github.com 上、国内经常连不通——白等一个超时。
        // 实测「已是最新」被拖慢了约 20 秒，正是这么来的。
        if (release.TagName is not { } tag || !IsNewer(tag)) return;

        var asset = release.Assets?.FirstOrDefault(a =>
            string.Equals(a.Name, ManifestAssetName, StringComparison.OrdinalIgnoreCase));
        if (asset?.BrowserDownloadUrl is not { Length: > 0 } url) return;

        try
        {
            // 清单只是几行 JSON；设上限避免异常响应把小文件变成大下载。
            if (asset.Size > 64 * 1024) return;
            release.ManifestJson = await GetStringAsync(url, ManifestTimeout, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // 拿不到清单不影响更新可用性，静默降级。
            // 注意这里**必须**连超时一起吞掉：清单是可选资产，它的超时曾经
            // （因为 TaskCanceledException 也是 OperationCanceledException）
            // 被当成取消透传出去，把整次「检查更新」打成失败——
            // 而它的注释本来就写着「取回失败不视为查询失败」。
        }
    }

    private static async Task<SourceResult> QuerySourceAsync(
        string name, string apiUrl, string repoUrl, bool includePrerelease, CancellationToken ct)
    {
        try
        {
            var json = await GetStringAsync(apiUrl, MetadataTimeout, ct).ConfigureAwait(false);
            var releases = JsonSerializer.Deserialize(json, AppJsonContext.Default.ListReleaseInfo);
            if (releases is null || releases.Count == 0)
                return new SourceResult(name, null, "没有任何 release");

            // 取版本号最高的候选。草稿永远排除；预发布只在用户显式选择接收时才参与，
            // 否则正式版用户会被提示升级到尚未验证的版本。
            var candidates = releases
                .Where(r => !r.Draft)
                .Where(r => includePrerelease || !r.Prerelease)
                .Where(r => r.TagName is not null && TryParseVersion(r.TagName, out _))
                .ToList();

            var release = candidates
                .OrderByDescending(r => ParseVersion(r.TagName!))
                .FirstOrDefault();
            if (release is null)
            {
                return new SourceResult(name, null, releases.Any(r => r.Prerelease)
                    ? "只有预发布版（当前未开启接收预发布版更新）"
                    : "没有可用的正式 release");
            }

            // 不信任 API 返回的 html_url：Gitea 实例若 ROOT_URL 配错（如 localhost），
            // 返回的链接用户根本打不开。按已知仓库地址自行拼 release 页。
            release.HtmlUrl = $"{repoUrl}/releases/tag/{release.TagName}";

            // 同样不信任资产里给的下载地址的主机名（镜像/代理可能配错），
            // 一律改写成「仓库地址 + 标准下载路径」，保证点得开、下得动。
            if (release.Assets is { Count: > 0 } assets)
            {
                foreach (var a in assets)
                {
                    if (a.Name is { Length: > 0 })
                        a.BrowserDownloadUrl = $"{repoUrl}/releases/download/{release.TagName}/{Uri.EscapeDataString(a.Name)}";
                }
            }

            return new SourceResult(name, release, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            // 超时（TimeoutException）与网络不可达分开说，用户才知道是「慢」还是「不通」。
            return new SourceResult(name, null, DescribeFailure(ex));
        }
    }

    /// <summary>解析「v1.2.6」形式的 tag（忽略预发布后缀）。</summary>
    public static bool TryParseVersion(string tag, out Version version)
    {
        var text = tag.TrimStart('v', 'V');
        var dash = text.IndexOf('-');
        if (dash >= 0) text = text[..dash];
        return Version.TryParse(text, out version!);
    }

    private static Version ParseVersion(string tag) =>
        TryParseVersion(tag, out var v) ? v : new Version(0, 0, 0);

    /// <summary>tag 是否比当前版本新（解析不出来的一律不算新）。</summary>
    public static bool IsNewer(string tag)
    {
        if (!TryParseVersion(tag, out var remote)) return false;
        if (!TryParseVersion(CurrentVersion, out var local)) return false;
        // 版本号可能一边三位一边四位（1.2.6 vs 1.2.6.0），补齐到等长再比。
        static Version Normalize(Version v) => new(
            v.Major, v.Minor,
            Math.Max(0, v.Build), Math.Max(0, v.Revision));
        return Normalize(remote) > Normalize(local);
    }

    // ---------------- 资产挑选与下载 ----------------

    /// <summary>挑中的更新包（资产 + 可选的清单）。</summary>
    public sealed record PackageChoice(ReleaseAsset Asset, UpdateManifest? Manifest, string Source);

    /// <summary>
    /// 在 release 的资产里挑出适用于本平台的更新包。
    /// <para>
    /// 优先看有没有 <c>update-manifest.json</c>：它给出确切文件名与 SHA256，
    /// 让客户端不必猜命名。没有清单时按「文件名同时含平台特征」兜底挑，
    /// 此时**无法校验哈希**，调用方应据此降低信任（见 <see cref="DownloadAsync"/> 的说明）。
    /// </para>
    /// </summary>
    /// <param name="release">已查询到的 release。</param>
    /// <param name="sourceName">来源名（用于结果标注）。</param>
    /// <param name="manifestJson">清单内容；null 表示没有清单。</param>
    public static PackageChoice? ChoosePackage(
        ReleaseInfo release, string sourceName, string? manifestJson)
    {
        var assets = release.Assets;
        if (assets is null || assets.Count == 0) return null;

        UpdateManifest? manifest = null;
        if (!string.IsNullOrWhiteSpace(manifestJson))
        {
            try
            {
                manifest = JsonSerializer.Deserialize(manifestJson, AppJsonContext.Default.UpdateManifest);
            }
            catch (JsonException)
            {
                // 清单坏了就退回按名字挑，不要因此完全不能更新。
                manifest = null;
            }
        }

        // ① 有清单：按清单里的文件名精确匹配
        if (manifest?.File is { Length: > 0 } wanted)
        {
            var hit = assets.FirstOrDefault(a =>
                string.Equals(a.Name, wanted, StringComparison.OrdinalIgnoreCase));
            if (hit is not null) return new PackageChoice(hit, manifest, sourceName);
        }

        // ② 没清单（或清单里的文件不在资产里）：按平台特征兜底
        var fallback = assets.FirstOrDefault(a =>
            a.Name is { Length: > 0 } name &&
            PlatformAssetMarkers.All(m => name.Contains(m, StringComparison.OrdinalIgnoreCase)));
        return fallback is null ? null : new PackageChoice(fallback, manifest, sourceName);
    }

    /// <summary>下载并校验的结果。</summary>
    public sealed record DownloadResult(
        string FilePath, string Sha256, bool HashVerified, string? ManifestVersion);

    /// <summary>下载进度（已下载字节 / 总字节；总字节未知时为 0）。</summary>
    public sealed record DownloadProgress(long Received, long Total)
    {
        public int? Percent => Total > 0 ? (int)Math.Clamp(Received * 100 / Total, 0, 100) : null;
    }

    /// <summary>
    /// 下载更新包到指定目录，并计算 SHA256。
    /// <para>
    /// <b>校验口径</b>：清单里给了 <c>sha256</c> 就必须匹配，不匹配直接抛异常并删除文件
    /// （半个包或错包绝不能进入替换流程）。没有清单时无法校验，
    /// 此时 <see cref="DownloadResult.HashVerified"/> 为 false，由调用方决定是否继续——
    /// 替换前还有一道「解出的 exe 版本号」兜底检查（见 <see cref="UpdateApplier"/>）。
    /// </para>
    /// </summary>
    /// <param name="choice">挑中的包。</param>
    /// <param name="targetDirectory">下载目标目录（会被创建）。</param>
    /// <param name="progress">进度回调，可为 null。</param>
    /// <param name="ct">取消令牌。</param>
    public static async Task<DownloadResult> DownloadAsync(
        PackageChoice choice,
        string targetDirectory,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken ct = default)
    {
        var url = choice.Asset.BrowserDownloadUrl
            ?? throw new InvalidOperationException("该资产没有下载地址");
        var fileName = choice.Asset.Name ?? "update.zip";

        Directory.CreateDirectory(targetDirectory);
        // 先下到 .part，成功后再改名——中断时不会留一个看起来完整的 zip。
        var finalPath = Path.Combine(targetDirectory, fileName);
        var partPath = finalPath + ".part";

        try
        {
            // 两个阶段两种上限，不能一刀切：
            //   ① 连接阶段（拿到响应头）：ConnectTimeout。连不上就该尽快报错；
            //   ② 传输阶段：DownloadBudget 的总预算。给够慢网下传十几 MB 的余地，
            //      但也**不能完全不设**——界面在下载期间禁用「取消」，真卡死会永远挂着。
            // 原先用的是 HttpClient.Timeout = 15 秒一刀切：慢网下正经下载会被拦腰掐断。
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(ConnectTimeout);

            HttpResponseMessage resp;
            try
            {
                resp = await Http
                    .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"下载更新包超时：{ConnectTimeout.TotalSeconds:0} 秒内没有建立连接。请稍后重试或手动下载。");
            }

            cts.CancelAfter(DownloadBudget);   // 换成传输阶段的总预算

            using (resp)
            {
                resp.EnsureSuccessStatusCode();
                var total = resp.Content.Headers.ContentLength ?? choice.Asset.Size;

                // 传输阶段的读取用 cts（用户取消 + 总预算），不是裸 ct。
                try
                {
                    await using var src = await resp.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
                    await using var dst = new FileStream(
                        partPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);

                    var buffer = new byte[81920];
                    long received = 0;
                    int read;
                    while ((read = await src.ReadAsync(buffer, cts.Token).ConfigureAwait(false)) > 0)
                    {
                        await dst.WriteAsync(buffer.AsMemory(0, read), cts.Token).ConfigureAwait(false);
                        received += read;
                        progress?.Report(new DownloadProgress(received, total));
                    }
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new TimeoutException(
                        $"下载更新包超时：超过 {DownloadBudget.TotalMinutes:0} 分钟仍未传完。"
                        + "已丢弃未完成的文件，请稍后重试或手动下载。");
                }
            }

            var sha = ComputeSha256(partPath);

            // 清单声明了哈希就必须一致，否则宁可失败也不装。
            if (choice.Manifest?.Sha256 is { Length: > 0 } expected)
            {
                if (!string.Equals(Normalize(sha), Normalize(expected), StringComparison.Ordinal))
                {
                    TryDelete(partPath);
                    throw new InvalidDataException(
                        $"更新包校验失败：期望 {expected}，实际 {sha}。已丢弃该文件，请重试或手动下载。");
                }
            }

            // 大小也要对得上（有清单时）——能拦住截断的下载。
            if (choice.Manifest is { Size: > 0 } manifestWithSize)
            {
                var declared = manifestWithSize.Size;
                var actual = new FileInfo(partPath).Length;
                if (actual != declared)
                {
                    TryDelete(partPath);
                    throw new InvalidDataException(
                        $"更新包大小不符：期望 {declared} 字节，实际 {actual} 字节。已丢弃该文件。");
                }
            }

            File.Move(partPath, finalPath, overwrite: true);
            return new DownloadResult(
                finalPath, sha, choice.Manifest?.Sha256 is { Length: > 0 }, choice.Manifest?.Version);
        }
        catch
        {
            TryDelete(partPath);
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* 清理失败不影响主流程 */ }
    }

    /// <summary>SHA256 转大写十六进制（与发布脚本、清单里的写法统一）。</summary>
    public static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static string Normalize(string hash) => hash.Trim().Replace(" ", string.Empty).ToUpperInvariant();

    // ---------------- 更新包内容检查 ----------------

    /// <summary>
    /// 从更新包（zip，内含单个 exe）里读出 exe 的文件版本号。
    /// <para>
    /// 用途：没有清单、无法校验哈希时，**至少确认下载到的确实是目标版本**，
    /// 拦住「下到旧包 / 下到错包」。读取方式是流式读该条目，
    /// 不落盘、不整包解压（解压要 41 MB，没必要）。
    /// </para>
    /// </summary>
    /// <param name="zipPath">更新包路径。</param>
    /// <returns>exe 的文件版本；读不出来返回 null。</returns>
    public static string? ReadPackageExeVersion(string zipPath)
    {
        try
        {
            using var zip = ZipFile.OpenRead(zipPath);
            // 单文件发布包里只有一个 exe；取体积最大的那个 exe 更稳。
            var entry = zip.Entries
                .Where(e => e.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(e => e.Length)
                .FirstOrDefault();
            if (entry is null) return null;

            // FileVersionInfo 需要一个真实路径，所以先落到临时文件。
            var temp = Path.Combine(Path.GetTempPath(), $"hr-ver-{Guid.NewGuid():N}.exe");
            try
            {
                entry.ExtractToFile(temp, overwrite: true);
                var info = System.Diagnostics.FileVersionInfo.GetVersionInfo(temp);
                return info.FileVersion ?? info.ProductVersion;
            }
            finally
            {
                TryDelete(temp);
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// 判断包内 exe 的版本是否**不低于某个基准**（低于说明会把用户降到更旧的版本）。
    /// 版本号读不出来时返回 true —— 不做无根据的拒绝，交给后续哈希校验兜底。
    /// </summary>
    /// <param name="zipPath">更新包路径。</param>
    /// <param name="baselineVersion">
    /// 比较基准。**应当传「当前已安装的版本」而不是 release tag**：
    /// tag 与包内 exe 的版本号并不保证一致（本项目实测过 tag=v1.3.1 而包内是 1.0.0.0 的历史缺陷），
    /// 拿 tag 当基准会把正常更新误判成降级。
    /// </param>
    public static bool PackageIsNotOlderThan(string zipPath, string baselineVersion)
    {
        var raw = ReadPackageExeVersion(zipPath);
        if (string.IsNullOrWhiteSpace(raw)) return true;
        if (!TryParseVersion(raw, out var packed)) return true;
        if (!TryParseVersion(baselineVersion, out var baseline)) return true;

        static Version Normalize(Version v) => new(
            v.Major, v.Minor, Math.Max(0, v.Build), Math.Max(0, v.Revision));
        return Normalize(packed) >= Normalize(baseline);
    }

    /// <summary>把包内的 exe 解到指定路径（覆盖已存在的）。</summary>
    public static string ExtractPackageExe(string zipPath, string destinationDirectory)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        var entry = zip.Entries
            .Where(e => e.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(e => e.Length)
            .FirstOrDefault()
            ?? throw new InvalidDataException("更新包里没有可执行文件");

        Directory.CreateDirectory(destinationDirectory);
        var dest = Path.Combine(destinationDirectory, entry.Name);
        entry.ExtractToFile(dest, overwrite: true);
        return dest;
    }
}
