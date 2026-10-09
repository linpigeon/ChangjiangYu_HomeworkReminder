using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using HomeworkReminder.Models;

namespace HomeworkReminder.Services;

/// <summary>学习通登录态失效（列表页被重定向回登录页）。</summary>
public sealed class ChaoxingAuthExpiredException(string message) : Exception(message);

/// <summary>
/// 学习通（超星）接口客户端。
/// <para>
/// 端点均为实测（2026-10，真实账号验证）：作业列表是
/// <c>GET /work/stu-work</c>、考试列表是 <c>GET /exam-ans/exam/phone/examcode</c>，
/// 两者都是服务端渲染的 HTML 而不是 JSON——条目结构简单且有 aria-label 聚合字段，
/// 用正则解析（脆弱性已在解析处标注；超星改页面结构时需要跟着改）。
/// </para>
/// </summary>
public sealed class ChaoxingApiClient : IDisposable
{
    private const string HomeworkUrl = "https://mooc1-api.chaoxing.com/work/stu-work";
    private const string ExamUrl = "https://mooc1-api.chaoxing.com/exam-ans/exam/phone/examcode";

    private readonly HttpClient _http;
    private readonly string _cookieHeader;

    public ChaoxingApiClient(string cookieHeader)
    {
        _cookieHeader = cookieHeader;
        _http = new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
        })
        {
            Timeout = TimeSpan.FromSeconds(30),
        };
    }

    /// <summary>一条作业/考试（解析后的统一形态）。</summary>
    public sealed record ChaoxingTask(
        long TaskRefId,
        long CourseId,
        string Title,
        string CourseName,
        string Status,
        TimeSpan? Remaining,
        string SourceUrl,
        bool IsExam);

    public async Task<IReadOnlyList<ChaoxingTask>> GetHomeworkAsync(CancellationToken ct = default)
    {
        var html = await GetAsync(HomeworkUrl, ct).ConfigureAwait(false);

        // 每条 <li data="{深链}">，内容里有一个 aria-label 聚合字段：
        // 作业名称{title}作业状态{status}所属课程{course}剩余时间{remaining}（剩余时间可能缺）
        var items = new List<ChaoxingTask>();
        foreach (Match li in Regex.Matches(html, @"<li[^>]*data=""(https://mooc1-api\.chaoxing\.com/mooc-ans/android/mtaskmsgspecial[^""]*)""[^>]*>"))
        {
            var url = li.Groups[1].Value;
            var taskRefId = GetQueryLong(url, "taskrefId");
            var courseId = GetQueryLong(url, "courseId");

            // aria-label 在同一 li 块内（li 不是嵌套结构，直接向后找最近的 aria-label）
            var tail = html[li.Index..];
            var label = Regex.Match(tail, @"aria-label=""作业名称(.+?)作业状态(.+?)所属课程(.+?)(?:剩余时间(.+?))?""");
            if (!label.Success) continue;

            items.Add(new ChaoxingTask(
                TaskRefId: taskRefId,
                CourseId: courseId,
                Title: label.Groups[1].Value,
                CourseName: label.Groups[3].Value.Trim('《', '》'),
                Status: label.Groups[2].Value,
                Remaining: ParseRemaining(label.Groups[4].Success ? label.Groups[4].Value : null),
                SourceUrl: url,
                IsExam: false));
        }
        return items;
    }

    public async Task<IReadOnlyList<ChaoxingTask>> GetExamsAsync(CancellationToken ct = default)
    {
        var html = await GetAsync(ExamUrl, ct).ConfigureAwait(false);

        // 每条考试：<li data="/exam-ans/exam/phone/task-exam?taskrefId=...">，
        // 同一块里有 aria-label='考试名称：{title}; 考试状态：{status}; 剩余时间：{remaining}'
        // （标签文本内部允许换行与缩进）。
        var items = new List<ChaoxingTask>();
        foreach (Match block in Regex.Matches(html,
                     @"<li[^>]*data=""(/exam-ans/exam/phone/task-exam\?[^""]*)""[\s\S]{0,800}?aria-label='考试名称：(.+?);\s*考试状态：(.+?);\s*(?:剩余时间：(.+?))?\s*'"))
        {
            var path = block.Groups[1].Value;
            var taskRefId = GetQueryLong(path, "taskrefId");
            var courseId = GetQueryLong(path, "courseId");

            items.Add(new ChaoxingTask(
                TaskRefId: taskRefId,
                CourseId: courseId,
                Title: block.Groups[2].Value,
                CourseName: block.Groups[2].Value, // 考试条目的名称就是课程名（页面没有单独课程字段）
                Status: block.Groups[3].Value,
                Remaining: ParseRemaining(block.Groups[4].Success ? block.Groups[4].Value : null),
                SourceUrl: "https://mooc1-api.chaoxing.com" + path,
                IsExam: true));
        }
        return items;
    }

    /// <summary>「剩余X天X小时X分钟」→ TimeSpan；解析不出返回 null（页面没给期限）。</summary>
    private static TimeSpan? ParseRemaining(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        // 注意不能用一个全可选的组合正则：它会在位置 0 匹配到空串。
        var days = MatchInt(text, @"(\d+)天");
        var hours = MatchInt(text, @"(\d+)小时");
        var minutes = MatchInt(text, @"(\d+)分钟");
        if (days == 0 && hours == 0 && minutes == 0) return null;
        return new TimeSpan(days, hours, minutes, 0);
    }

    private static int MatchInt(string text, string pattern)
    {
        var m = Regex.Match(text, pattern);
        return m.Success ? int.Parse(m.Groups[1].Value) : 0;
    }

    private static long GetQueryLong(string url, string key)
    {
        var m = Regex.Match(url, $@"[?&]{key}=(\d+)");
        return m.Success ? long.Parse(m.Groups[1].Value) : 0;
    }

    private async Task<string> GetAsync(string url, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("Cookie", _cookieHeader);
        req.Headers.TryAddWithoutValidation("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");
        req.Headers.TryAddWithoutValidation("Referer", "https://i.chaoxing.com/");

        var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        // 会话失效时超星把列表页重定向回登录页（标题是「登录」）。
        if (text.Contains("<title>登录</title>", StringComparison.Ordinal)
            || text.Contains("passport2.chaoxing.com/login", StringComparison.Ordinal))
            throw new ChaoxingAuthExpiredException("学习通登录态已失效");

        if (!resp.IsSuccessStatusCode)
            throw new ChaoxingApiException($"接口返回 {(int)resp.StatusCode}");

        return text;
    }

    public void Dispose() => _http.Dispose();
}

public sealed class ChaoxingApiException(string message) : Exception(message);
