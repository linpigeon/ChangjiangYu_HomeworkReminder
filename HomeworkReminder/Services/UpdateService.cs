using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace HomeworkReminder.Services;

/// <summary>Release 信息。GitHub 与 Gitea 的 release API 返回结构同构，共用一份 DTO。</summary>
public sealed class ReleaseInfo
{
    [JsonPropertyName("tag_name")] public string? TagName { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("html_url")] public string? HtmlUrl { get; set; }
}

/// <summary>
/// 版本号与更新检查。更新源是作者的 GitHub 与 Gitea 两个镜像仓库，
/// 取两边返回中较新的版本号对比本地版本。
/// </summary>
public static class UpdateService
{
    /// <summary>更新源（名称 + latest release API + 仓库页面地址）。</summary>
    public static readonly (string Name, string ApiUrl, string RepoUrl)[] Sources =
    [
        ("GitHub",
            "https://api.github.com/repos/linpigeon/ChangjiangYu_HomeworkReminder/releases/latest",
            "https://github.com/linpigeon/ChangjiangYu_HomeworkReminder"),
        ("Gitea",
            "https://git.pigeon-lin.top/api/v1/repos/lin/ChangjiangYu_HomeworkReminder/releases/latest",
            "https://git.pigeon-lin.top/lin/ChangjiangYu_HomeworkReminder"),
    ];

    // GitHub API 要求带 User-Agent，否则 403。
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("HomeworkReminder/" + CurrentVersion);
        return http;
    }

    /// <summary>当前应用版本（共享程序集的版本号，发布时与 release tag 保持一致）。</summary>
    public static string CurrentVersion
    {
        get
        {
            var info = typeof(UpdateService).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            // 构建可能追加「+commitsha」后缀，展示与比较时去掉。
            var version = info ?? typeof(UpdateService).Assembly.GetName().Version?.ToString() ?? "0.0.0";
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
    public static async Task<Outcome> CheckAsync(CancellationToken ct = default)
    {
        var tasks = Sources.Select(s => QuerySourceAsync(s.Name, s.ApiUrl, s.RepoUrl, ct)).ToArray();
        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // 单源异常已收敛进 SourceResult，WhenAll 只会在取消时抛出。
        }
        return new Outcome(tasks.Select(t => t.IsCompletedSuccessfully
            ? t.Result
            : new SourceResult("(未知)", null, "查询未完成")).ToArray());
    }

    private static async Task<SourceResult> QuerySourceAsync(string name, string apiUrl, string repoUrl, CancellationToken ct)
    {
        try
        {
            var json = await Http.GetStringAsync(apiUrl, ct).ConfigureAwait(false);
            var release = JsonSerializer.Deserialize(json, AppJsonContext.Default.ReleaseInfo);
            if (release?.TagName is null)
                return new SourceResult(name, null, "返回内容不含版本号");

            // 不信任 API 返回的 html_url：Gitea 实例若 ROOT_URL 配错（如 localhost），
            // 返回的链接用户根本打不开。按已知仓库地址自行拼 release 页。
            release.HtmlUrl = $"{repoUrl}/releases/tag/{release.TagName}";
            return new SourceResult(name, release, null);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return new SourceResult(name, null, ex is HttpRequestException or TaskCanceledException
                ? "网络不可达"
                : ex.Message);
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
}
