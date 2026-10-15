using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace HomeworkReminder.Services;

/// <summary>批改网登录态失效（未完成列表页渲染成「请先登录」）。</summary>
public sealed class PigaiAuthExpiredException(string message) : Exception(message);

/// <summary>批改网接口异常。</summary>
public sealed class PigaiApiException(string message) : Exception(message);

/// <summary>
/// 批改网（pigai.org）接口客户端。
/// <para>
/// 端点实测（2026-10，真实账号验证）：
/// </para>
/// <list type="bullet">
/// <item><c>GET /index.php?c=write&amp;a=waitComplete&amp;bf=4</c> —— 未完成作文列表。
/// <c>bf</c> 是作文类型筛选，<c>4</c> 即「作文」（该站还有翻译/默写等类型）。</item>
/// </list>
/// <para>
/// 该页是服务端渲染的 HTML 而非 JSON：表头是 <c>&lt;ul class="reItemM"&gt;</c>，
/// 每条作业是一个 <c>&lt;ul class="reItemS"&gt;</c>，列固定为
/// 作业号 / 作业标题 / 类型 / 出题人 / 指定班级 / 截止时间 / 操作。
/// 结构简单但没有 aria-label 之类的聚合字段，只能按行解析（脆弱性在此标注：
/// 批改网改版时需要跟着改，见 <see cref="ParseList"/> 的注释）。
/// </para>
/// <para>
/// <b>登录判定</b>：凭据失效时该页<b>不返回 302</b>，而是照常 200 并渲染
/// 「使用此功能，请先登录」。因此不能用状态码判断，只能看<b>响应正文</b>。
/// （会话建立由 <see cref="PigaiLoginService"/> 的页面探测负责，这里只做失效识别。）
/// </para>
/// </summary>
public sealed partial class PigaiApiClient : IDisposable
{
    private const string BaseUrl = "https://www.pigai.org";

    /// <summary>未完成作文列表（bf=4 即「作文」类型）。</summary>
    private const string WaitCompleteUrl = BaseUrl + "/index.php?c=write&a=waitComplete&bf=4";

    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
        "(KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";

    /// <summary>作业详情页（应用内 WebView 打开答题页 / 浏览器打开都用它）。</summary>
    public static string EssayUrl(long rid) => $"{BaseUrl}/index.php?c=v2&a=write&rid={rid}";

    private readonly HttpClient _http;
    private readonly string _cookieHeader;

    public PigaiApiClient(string cookieHeader)
    {
        _cookieHeader = cookieHeader;
        _http = new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = true,
        })
        {
            Timeout = TimeSpan.FromSeconds(30),
        };
    }

    /// <summary>一条未完成作文（解析后的统一形态）。</summary>
    public sealed record PigaiTask(
        long Rid,
        string Title,
        string Type,
        string Teacher,
        string Classes,
        DateTimeOffset? DueAt);

    /// <summary>抓取并解析「未完成作文」列表。登录态失效时抛 <see cref="PigaiAuthExpiredException"/>。</summary>
    public async Task<IReadOnlyList<PigaiTask>> GetIncompleteEssaysAsync(CancellationToken ct = default)
    {
        var html = await GetAsync(WaitCompleteUrl, ct).ConfigureAwait(false);

        // 未登录时页面是「使用此功能，请先登录」+ 3 秒跳转，而不是列表。
        if (html.Contains("请先登录", StringComparison.Ordinal))
            throw new PigaiAuthExpiredException("批改网登录态已失效");

        return ParseList(html);
    }

    /// <summary>
    /// 解析未完成列表。
    /// <para>
    /// 逐条切分 <c>&lt;ul class="reItemS"&gt;…&lt;/ul&gt;</c>（表头用的是 <c>reItemM</c>，
    /// 不会误命中）；作业号与详情链接取 <c>rid</c> 查询参数；标题取该块内指向
    /// <c>rid</c> 的 <c>&lt;a&gt;</c> 文本；截止时间用「日期 时间」形态直接匹配。
    /// </para>
    /// <para>
    /// 刻意不按「第 N 个 &lt;li&gt;」取列：服务端改版时列顺序可能变，按语义匹配
    /// （rid 链接、日期样式）比按序号更耐用；但**列语义本身**仍是硬编码的约定，
    /// 批改网大改版时这里要跟着改，届时 selfcheck 会失败并指出来。
    /// </para>
    /// </summary>
    internal static IReadOnlyList<PigaiTask> ParseList(string html)
    {
        var result = new List<PigaiTask>();

        foreach (Match block in ItemRegex().Matches(html))
        {
            var body = block.Groups[1].Value;

            var ridMatch = RidRegex().Match(body);
            if (!ridMatch.Success) continue;
            if (!long.TryParse(ridMatch.Groups[1].Value, out var rid) || rid <= 0) continue;

            var titleMatch = TitleRegex().Match(body);
            if (!titleMatch.Success) continue;
            var title = CleanText(titleMatch.Groups[1].Value);
            if (string.IsNullOrWhiteSpace(title)) continue;

            var cells = new List<string>();
            foreach (Match li in CellRegex().Matches(body)) cells.Add(CleanText(li.Groups[1].Value));

            // 列顺序：0 作业号 / 1 标题 / 2 类型 / 3 出题人 / 4 指定班级 / 5 截止时间 / 6 操作
            var type = cells.Count > 2 ? cells[2] : string.Empty;
            var teacher = cells.Count > 3 ? cells[3] : string.Empty;
            var classes = cells.Count > 4 ? cells[4] : string.Empty;

            var dueMatch = DeadlineRegex().Match(body);
            var due = dueMatch.Success
                ? ParseDue(dueMatch.Value)
                : null;

            result.Add(new PigaiTask(rid, title, type, teacher, classes, due));
        }

        return result;
    }

    /// <summary>把 <c>2026-12-06 23:59</c> 解析成绝对时间（按北京时间）。</summary>
    private static DateTimeOffset? ParseDue(string text)
    {
        if (!DateTime.TryParseExact(
                text.Trim(),
                "yyyy-MM-dd HH:mm",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None,
                out var local))
            return null;

        // 批改网按北京时间返回，不随本机时区变化（与雨课堂的处理一致）。
        return new DateTimeOffset(local, TimeSpan.FromHours(8));
    }

    /// <summary>去标签、还原实体、压缩空白。</summary>
    private static string CleanText(string raw)
    {
        var noTags = TagRegex().Replace(raw, string.Empty);
        var decoded = WebUtility.HtmlDecode(noTags);
        return WhitespaceRegex().Replace(decoded, " ").Trim();
    }

    private async Task<string> GetAsync(string url, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("Cookie", _cookieHeader);
        req.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        req.Headers.TryAddWithoutValidation("Referer", BaseUrl + "/");
        req.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,*/*");

        HttpResponseMessage resp;
        try
        {
            resp = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new PigaiApiException("请求超时，批改网没有在预期时间内响应");
        }
        catch (HttpRequestException ex)
        {
            throw new PigaiApiException($"网络请求失败：{ex.Message}");
        }

        using (resp)
        {
            var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new PigaiAuthExpiredException($"登录态失效（HTTP {(int)resp.StatusCode}），请重新登录批改网");

            if (!resp.IsSuccessStatusCode)
                throw new PigaiApiException($"接口返回 {(int)resp.StatusCode}");

            return text;
        }
    }

    [GeneratedRegex(@"<ul class=""reItemS"">(.*?)</ul>", RegexOptions.Singleline)]
    private static partial Regex ItemRegex();

    [GeneratedRegex(@"\?c=v2&a=write&rid=(\d+)")]
    private static partial Regex RidRegex();

    [GeneratedRegex(@"<a href=""\?c=v2&a=write&rid=\d+""[^>]*>(.*?)</a>", RegexOptions.Singleline)]
    private static partial Regex TitleRegex();

    [GeneratedRegex(@"<li[^>]*>(.*?)</li>", RegexOptions.Singleline)]
    private static partial Regex CellRegex();

    [GeneratedRegex(@"\d{4}-\d{2}-\d{2}\s+\d{2}:\d{2}")]
    private static partial Regex DeadlineRegex();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex TagRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    public void Dispose() => _http.Dispose();
}
