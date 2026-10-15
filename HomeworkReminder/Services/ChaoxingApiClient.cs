using System;
using System.Collections.Generic;
using System.Linq;
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
public sealed partial class ChaoxingApiClient : IDisposable
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

    /// <summary>
    /// 一门课程（来自 <c>backclazzdata</c> 课程列表）。
    /// <para>
    /// 三个 id 缺一不可且**不能混用**：<see cref="ClazzId"/> 是班级 id、
    /// <see cref="CourseId"/> 是课程 id、<see cref="PersonId"/> 是选课关系 id。
    /// 混用会拿到空白页或别的课程（实测踩过）。
    /// </para>
    /// </summary>
    public sealed record ChaoxingCourse(
        long CourseId,
        long ClazzId,
        long PersonId,
        string ClazzName,
        string CourseName,
        bool IsRetired);

    /// <summary>一个叶子章节的未完成任务点。</summary>
    public sealed record ChaoxingTaskPoint(string ChapterNumber, string Name, int PendingCount);

    /// <summary>一门课的未完成任务点汇总。</summary>
    public sealed record ChaoxingCourseTaskPoints(
        long CourseId,
        int PendingTotal,
        IReadOnlyList<ChaoxingTaskPoint> Leaves);

    // ---------------- 课程列表（backclazzdata） ----------------

    private const string CourseListUrl =
        "https://mooc1-api.chaoxing.com/mycourse/backclazzdata?view=json&rss=1";

    /// <summary>
    /// 课程列表。用 <c>backclazzdata</c>：它一次给出 clazzId / courseId / personId /
    /// 班级名 / 课程名 / 是否结课，正是任务点接口需要的三元组。
    /// </summary>
    public async Task<IReadOnlyList<ChaoxingCourse>> GetCoursesAsync(CancellationToken ct = default)
    {
        var json = await GetAsync(CourseListUrl, ct).ConfigureAwait(false);
        return ParseCourses(json);
    }

    /// <summary>
    /// 解析课程列表 JSON。
    /// <c>{ "channelList": [ { "content": { "id": clazzId, "cpi": personId,
    /// "name": 班级名, "isretire": 0/1, "course": { "data": [ { "id": courseId,
    /// "name": 课程名 } ] } } } ] }</c>
    /// </summary>
    internal static IReadOnlyList<ChaoxingCourse> ParseCourses(string json)
    {
        var list = new List<ChaoxingCourse>();
        System.Text.Json.JsonDocument doc;
        try
        {
            doc = System.Text.Json.JsonDocument.Parse(json);
        }
        catch (System.Text.Json.JsonException)
        {
            return list;
        }

        using (doc)
        {
            if (!doc.RootElement.TryGetProperty("channelList", out var channels)
                || channels.ValueKind != System.Text.Json.JsonValueKind.Array)
                return list;

            foreach (var channel in channels.EnumerateArray())
            {
                if (!channel.TryGetProperty("content", out var content)
                    || content.ValueKind != System.Text.Json.JsonValueKind.Object)
                    continue;

                var clazzId = ReadLong(content, "id");
                var personId = ReadLong(content, "cpi");
                var clazzName = ReadText(content, "name") ?? string.Empty;
                var retired = ReadLong(content, "isretire") == 1;

                var courseId = 0L;
                var courseName = string.Empty;
                if (content.TryGetProperty("course", out var course)
                    && course.ValueKind == System.Text.Json.JsonValueKind.Object
                    && course.TryGetProperty("data", out var data)
                    && data.ValueKind == System.Text.Json.JsonValueKind.Array
                    && data.GetArrayLength() > 0)
                {
                    var first = data[0];
                    courseId = ReadLong(first, "id");
                    courseName = ReadText(first, "name") ?? string.Empty;
                }

                // 三元组缺一就没法查任务点：宁可不显示，也不要拿错课程的数据。
                if (clazzId <= 0 || courseId <= 0 || personId <= 0) continue;

                list.Add(new ChaoxingCourse(
                    CourseId: courseId,
                    ClazzId: clazzId,
                    PersonId: personId,
                    ClazzName: clazzName,
                    // 课程名为空时退回班级名，避免界面出现空标题。
                    CourseName: string.IsNullOrWhiteSpace(courseName) ? clazzName : courseName,
                    IsRetired: retired));
            }
        }

        return list;
    }

    // ---------------- 未完成任务点（stucoursemiddle） ----------------

    /// <summary>
    /// 未完成任务点。走课程首页 <c>stucoursemiddle</c>（实测**不需要 enc 参数**，
    /// cookie 直连即可；新版移动端 JSON 接口基本都要求 enc）。
    /// </summary>
    public async Task<ChaoxingCourseTaskPoints> GetPendingTaskPointsAsync(
        long courseId, long clazzId, long personId, CancellationToken ct = default)
    {
        var url = "https://mooc1-2.chaoxing.com/visit/stucoursemiddle" +
                  $"?courseid={courseId}&clazzid={clazzId}&vc=1&cpi={personId}";
        var html = await GetAsync(url, ct, referer: "https://mooc1-2.chaoxing.com/").ConfigureAwait(false);
        return ParseTaskPoints(html, courseId);
    }

    /// <summary>
    /// 解析「待完成任务点」章节树。
    /// <para>
    /// <b>核心口径：只统计叶子章节。</b>父层级的 <c>knowledgeJobCount</c> 是子层级的
    /// <b>汇总值</b>，朴素求和会重复计数——实测「信号与系统」朴素求和得 16，
    /// 按叶子口径才是 13。
    /// </para>
    /// <para>
    /// 叶子判定：某个带任务点的章节，若它之后（同一章单元内）还存在更深层级的任务点，
    /// 它就是父层汇总，跳过。这样同时兼容两种课程结构：
    /// 有的课没有三级嵌套（任务点全在 <c>leveltwo</c>），有的课有 <c>levelthree</c>。
    /// </para>
    /// </summary>
    internal static ChaoxingCourseTaskPoints ParseTaskPoints(string html, long courseId)
    {
        var leaves = new List<ChaoxingTaskPoint>();

        // 没有这个区块 = 该课没有未完成任务点（已结课 / 纯资料课）。
        var start = html.IndexOf("待完成任务点", StringComparison.Ordinal);
        if (start < 0) return new ChaoxingCourseTaskPoints(courseId, 0, leaves);

        var block = html[start..];

        foreach (var unit in SplitUnits(block))
        {
            var marks = new List<(int Pos, int Depth, string Number, string Name, int Count)>();

            foreach (var (className, depth) in new[] { ("levelfirst", 1), ("leveltwo", 2), ("levelthree", 3) })
            {
                foreach (Match m in Regex.Matches(unit, $@"class=""{className}[^""]*"""))
                {
                    var tail = unit[m.Index..];
                    if (tail.Length > 4000) tail = tail[..4000];

                    var nameMatch = Regex.Match(tail, @"<span class=""articlename""[^>]*title=""([^""]*)""");
                    var numMatch = Regex.Match(tail, @"<span class=""chapterNumber"">\s*([^<]*?)\s*</span>");
                    var countMatch = JobCountRegex().Match(tail);
                    if (!countMatch.Success) continue;

                    marks.Add((
                        m.Index,
                        depth,
                        numMatch.Success ? numMatch.Groups[1].Value : string.Empty,
                        nameMatch.Success ? nameMatch.Groups[1].Value : string.Empty,
                        int.Parse(countMatch.Groups[1].Value)));
                }
            }

            // 必须按文档位置排序：下面判定「是否有更深层子节点」靠的是位置先后，
            // 而 marks 是按层级（levelfirst→leveltwo→levelthree）依次收集的，
            // 不排序会漏掉「leveltwo 在前、它的 levelthree 在后」这种最常见的情形，
            // 结果是父层汇总值被当成叶子一起算进去（数字偏大）。
            marks.Sort((a, b) => a.Pos.CompareTo(b.Pos));

            foreach (var mark in marks)
            {
                // 之后还有更深层级 → 它是父层汇总，跳过（否则数字会偏大）。
                var hasDeeperChild = marks.Any(other => other.Pos > mark.Pos && other.Depth > mark.Depth);
                if (hasDeeperChild) continue;
                if (mark.Count <= 0) continue;

                leaves.Add(new ChaoxingTaskPoint(mark.Number, mark.Name, mark.Count));
            }
        }

        return new ChaoxingCourseTaskPoints(courseId, leaves.Sum(l => l.PendingCount), leaves);
    }

    /// <summary>按 <c>&lt;div class="units"&gt;</c> 切出章单元（跳过第一个空片段）。</summary>
    private static IEnumerable<string> SplitUnits(string block)
    {
        var parts = Regex.Split(block, @"<div class=""units"">");
        for (var i = 1; i < parts.Length; i++) yield return parts[i];
    }

    private static long ReadLong(System.Text.Json.JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v)) return 0;
        return v.ValueKind switch
        {
            System.Text.Json.JsonValueKind.Number when v.TryGetInt64(out var l) => l,
            System.Text.Json.JsonValueKind.String when long.TryParse(v.GetString(), out var s) => s,
            _ => 0,
        };
    }

    private static string? ReadText(System.Text.Json.JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String
            ? v.GetString()
            : null;

    [GeneratedRegex(@"<input type=""hidden"" value=""(\d+)"" class=""knowledgeJobCount""")]
    private static partial Regex JobCountRegex();

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

    private async Task<string> GetAsync(string url, CancellationToken ct,
        string referer = "https://i.chaoxing.com/")
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("Cookie", _cookieHeader);
        req.Headers.TryAddWithoutValidation("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");
        req.Headers.TryAddWithoutValidation("Referer", referer);

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
