using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using HomeworkReminder.Models;

namespace HomeworkReminder.Services;

/// <summary>
/// 一次同步的结果。
/// <para>
/// 作业以学习日志为主源，完成状态一律以 pub_new_pro 的 leaf_schedules 判定；
/// 公告以通知中心为主源。两者都只是"候选"，真正的待办由界面层按完成状态筛选。
/// </para>
/// </summary>
public sealed class SyncResult
{
    public IReadOnlyList<TodoItem> Homework { get; init; } = [];
    public IReadOnlyList<TodoItem> Announcements { get; init; } = [];
    public int CourseCount { get; init; }
    public int ActivityCount { get; init; }
    public int UnreadNotificationCount { get; init; }
    public DateTimeOffset SyncedAt { get; init; } = DateTimeOffset.Now;
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>本次同步每门课的作业日志签名（供下次增量比对、落盘进 todos.json）。</summary>
    public IReadOnlyDictionary<long, string> CourseSignatures { get; init; } =
        new Dictionary<long, string>();

    /// <summary>
    /// 去重合并前的作业列表（每项保留自己的 ClassroomId），供落盘缓存。
    /// 合并会丢课堂归属，被并掉的课程下次就无法增量复用，所以缓存必须用合并前的。
    /// </summary>
    public IReadOnlyList<TodoItem> HomeworkForCache { get; init; } = [];

    /// <summary>这次同步里走增量捷径（跳过节点的课程数），仅用于状态展示。</summary>
    public int ReusedCourses { get; init; }

    public IEnumerable<TodoItem> PendingHomework =>
        Homework.Where(h => h.Status != TodoStatus.Completed);

    public IEnumerable<TodoItem> CompletedHomework =>
        Homework.Where(h => h.Status == TodoStatus.Completed);
}

public interface ISyncService
{
    /// <param name="previous">上次的缓存（todos.json）。null 或过期则全量同步。</param>
    Task<SyncResult> SyncAsync(TodoCacheFile? previous, IProgress<string>? progress = null, CancellationToken ct = default);
}

/// <inheritdoc />
public sealed class SyncService(IYktApi api) : ISyncService
{
    /// <summary>Beijing time; all 雨课堂 timestamps are in this zone regardless of host locale.</summary>
    private static readonly TimeSpan Beijing = TimeSpan.FromHours(8);

    /// <summary>区间外的时间戳视为脏数据（例如 0 或哨兵值）。</summary>
    private static readonly DateTimeOffset MinPlausible = new(2015, 1, 1, 0, 0, 0, Beijing);
    private static readonly DateTimeOffset MaxPlausible = new(2100, 1, 1, 0, 0, 0, Beijing);

    /// <summary>
    /// 缓存超过这个时间就强制全量校验：增量签名只覆盖学习日志，章节树里改截止时间、
    /// 老师删了章节项这类「日志不动」的变更，只能靠周期性全量兜住。
    /// </summary>
    private static readonly TimeSpan FullRevalidateAfter = TimeSpan.FromHours(24);

    public async Task<SyncResult> SyncAsync(TodoCacheFile? previous, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var warnings = new List<string>();

        progress?.Report("正在读取课程列表…");
        var allCourses = await api.GetCoursesAsync(ct).ConfigureAwait(false);
        var courses = allCourses.Where(c => c.Term == api.Session.Term).ToList();

        var signatures = new Dictionary<long, string>();

        if (courses.Count == 0)
        {
            warnings.Add($"没有找到 {api.Session.Term} 学期的课程（接口共返回 {allCourses.Count} 门）。" +
                         "如果课程确实存在，可能是学期标识需要调整。");
            return new SyncResult { CourseCount = 0, Warnings = warnings, CourseSignatures = signatures };
        }

        // 缓存太旧就不走增量：周期性全量是对「日志不动的变更」的兜底校验。
        var reusable = previous is not null
            && previous.SyncedAt > DateTimeOffset.Now - FullRevalidateAfter
            ? previous : null;

        var homework = new List<TodoItem>();
        var announcements = new List<TodoItem>();
        var activityCount = 0;
        var reusedCourses = 0;

        for (var i = 0; i < courses.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var course = courses[i];
            progress?.Report($"正在读取课程 {i + 1}/{courses.Count}：{Trim(course.Name)}");

            List<YktActivity> activities;
            try
            {
                activities = (await api.GetLearnLogsAsync(course.ClassroomId, ct).ConfigureAwait(false)).ToList();
            }
            catch (YktAuthExpiredException)
            {
                throw;
            }
            catch (YktApiException ex)
            {
                warnings.Add($"{Trim(course.Name)}：学习日志读取失败（{ex.Message}）");
                continue;
            }

            activityCount += activities.Count;

            // 公告直接来自学习日志：type=9，hasRead 是唯一可靠的未读信号。
            foreach (var act in activities.Where(a => a.Type == YktActivityType.Announcement))
            {
                announcements.Add(new TodoItem
                {
                    Id = $"ann-{course.ClassroomId}-{act.Id}",
                    Kind = TodoKind.Announcement,
                    Title = act.Title,
                    CourseName = course.Name,
                    ClassroomId = course.ClassroomId,
                    PublishedAt = FromUnixMs(act.CreateTime.OrZero),
                    Status = TodoStatus.Unknown,
                    IsRead = act.HasRead ?? false,
                    // 公告详情页需要 notice id；日志 id 与通知中心的 notify_id 不同源，
                    // 没有实测确认，因此统一落到课程日志页（公告页签就在那里）。
                    SourceUrl = YktUrls.CourseLog(course.ClassroomId, api.Session.UniversityId),
                });
            }

            var homeworkActivities = activities
                .Where(a => a.Type is YktActivityType.ExamHomework or YktActivityType.ChapterHomework)
                .ToList();

            // 签名覆盖作业推导的全部日志侧输入（id/类型/标题/发布时间/leaf_id/截止时间），
            // 签名相同 ⇒ 这门课的作业身份、标题、截止时间不可能变。
            signatures[course.ClassroomId] = SignatureOf(homeworkActivities);

            if (homeworkActivities.Count == 0) continue;

            // 增量捷径：签名未变且缓存里有这门课的作业 → 跳过章节树与逐条详情
            // （请求数的大头），只拉一次完成进度校验已有项目的状态（可能别的设备上做了）。
            if (reusable is not null
                && reusable.CourseSignatures.TryGetValue(course.ClassroomId, out var oldSignature)
                && oldSignature == signatures[course.ClassroomId])
            {
                var cached = reusable.Homework
                    .Where(h => h.ClassroomId == course.ClassroomId)
                    .ToList();
                if (cached.Count > 0)
                {
                    await RefreshStatusOnly(course, cached, warnings, ct).ConfigureAwait(false);
                    homework.AddRange(cached);
                    reusedCourses++;
                    continue;
                }
            }

            // 完成状态：唯一的权威来源。
            IReadOnlyDictionary<string, YktLeafProgress> progressMap =
                new Dictionary<string, YktLeafProgress>();
            try
            {
                progressMap = await api.GetProgressAsync(course.ClassroomId, ct).ConfigureAwait(false);
            }
            catch (YktAuthExpiredException)
            {
                throw;
            }
            catch (YktApiException ex)
            {
                warnings.Add($"{Trim(course.Name)}：完成进度读取失败，这些作业的状态将显示为「未知」（{ex.Message}）");
            }

            // 章节树有两个用途：给试卷类作业（type=5）补截止时间，以及把日志里的
            // leaf_id 归一到「章节项 id」。
            //
            // 实测要点：leaf_schedules 的键、以及 /leaf_info/{room}/{id}/ 的 {id}，
            // 用的都是章节项的 `id`，而不是 `leafinfo_id`（后者是另一个体系的 id，
            // 拿它去查会得到 "Objects does not exist."，用它当 leaf_schedules 的键
            // 则一律查不到 → 完成状态全部退化成「未知」）。
            Dictionary<string, YktLeafInfo> tree = new(StringComparer.Ordinal);
            try
            {
                var chapters = await api.GetChaptersAsync(course.ClassroomId, ct).ConfigureAwait(false);
                foreach (var leaf in chapters.SelectMany(c => c.SectionLeafList ?? []))
                {
                    if (!string.IsNullOrEmpty(leaf.Name)) tree[Normalize(leaf.Name)] = leaf;
                }
            }
            catch (YktAuthExpiredException)
            {
                throw;
            }
            catch (YktApiException ex)
            {
                warnings.Add($"{Trim(course.Name)}：章节树读取失败，截止时间与完成状态可能缺失（{ex.Message}）");
            }

            foreach (var act in homeworkActivities)
            {
                ct.ThrowIfCancellationRequested();

                long? leafId = act.Content?.LeafId;
                long deadlineMs = act.Content?.ScoreDeadlineRaw.OrZero ?? 0;

                // 按标题对齐章节项：type=5 的日志没有内容体，只能这样找；
                // 其他类型用于校正 leaf_id 并补齐章节树里更权威的截止时间。
                if (tree.TryGetValue(Normalize(act.Title), out var node))
                {
                    leafId = node.Id;
                    if (node.ScoreDeadline.OrZero > 0) deadlineMs = node.ScoreDeadline.OrZero;
                }

                DateTimeOffset? published = FromUnixMs(act.CreateTime.OrZero);

                if (leafId is { } lid && lid > 0)
                {
                    // leaf_info 提供发布时间；拿不到就退回日志时间。
                    try
                    {
                        var detail = await api.GetLeafDetailAsync(course.ClassroomId, lid, ct).ConfigureAwait(false);
                        if (detail is { } d2)
                        {
                            if (d2.PublishTime.OrZero > 0) published = FromUnixMs(d2.PublishTime.OrZero);
                            if (deadlineMs <= 0 && d2.ScoreDeadline.OrZero > 0) deadlineMs = d2.ScoreDeadline.OrZero;
                        }
                    }
                    catch (YktAuthExpiredException)
                    {
                        throw;
                    }
                    catch (YktApiException)
                    {
                        // 详情失败不影响主流程，发布时间退回日志时间。
                    }
                }

                var (status, done, total, score) = Judge(leafId, progressMap);

                homework.Add(new TodoItem
                {
                    Id = $"hw-{course.ClassroomId}-{leafId?.ToString() ?? act.Id.ToString()}",
                    Kind = TodoKind.Homework,
                    Title = act.Title,
                    CourseName = course.Name,
                    ClassroomId = course.ClassroomId,
                    DueAt = FromUnixMs(deadlineMs),
                    PublishedAt = published,
                    Status = status,
                    Done = done,
                    Total = total,
                    Score = score,
                    // 作业详情页的路由是 /exam/{cid}/{eid} 或 /exercise/{cid}/{leafId}/{skuId}，
                    // 但前者该传哪个 id（日志 id 还是试卷 id）没有实测确认，后者还缺 sku_id，
                    // 所以统一指向课程日志页——作业就在该页的「未完成」页签下。
                    SourceUrl = YktUrls.CourseLog(course.ClassroomId, api.Session.UniversityId),
                });
            }
        }

        // 未读总数仅用于展示角标，失败不影响主流程。
        var unread = 0;
        try
        {
            unread = await api.GetUnreadCountAsync(ct).ConfigureAwait(false);
        }
        catch (YktAuthExpiredException)
        {
            throw;
        }
        catch (YktApiException ex)
        {
            warnings.Add($"未读总数读取失败：{ex.Message}");
        }

        // 同一条作业可能同时出现在多个班级课堂里，按「标题 + 截止时间」去重，保留最紧急的状态。
        var merged = homework
            .GroupBy(h => $"{h.Title}@{h.DueAt:yyyyMMddHHmm}")
            .Select(g => g
                .OrderBy(x => x.Status == TodoStatus.Completed ? 1 : 0)
                .ThenBy(x => x.CourseName.Length)
                .First())
            .ToList();

        return new SyncResult
        {
            Homework = merged,
            HomeworkForCache = homework,
            Announcements = announcements
                .GroupBy(a => a.Id)
                .Select(g => g.First())
                .OrderByDescending(a => a.PublishedAt)
                .ToList(),
            CourseCount = courses.Count,
            ActivityCount = activityCount,
            UnreadNotificationCount = unread,
            Warnings = warnings,
            CourseSignatures = signatures,
            ReusedCourses = reusedCourses,
        };
    }

    /// <summary>
    /// 增量捷径里的「轻量校验」：只拉完成进度（每课 1 个请求），
    /// 就地刷新缓存项的完成状态/进度/成绩。作业的叶子 id 从稳定 Id（hw-{classroomId}-{leafId}）解析。
    /// </summary>
    private async Task RefreshStatusOnly(
        YktCourse course, List<TodoItem> cached, List<string> warnings, CancellationToken ct)
    {
        IReadOnlyDictionary<string, YktLeafProgress> progressMap;
        try
        {
            progressMap = await api.GetProgressAsync(course.ClassroomId, ct).ConfigureAwait(false);
        }
        catch (YktAuthExpiredException)
        {
            throw;
        }
        catch (YktApiException ex)
        {
            // 校验失败就保留缓存里的旧状态，不影响展示。
            warnings.Add($"{Trim(course.Name)}：完成状态校验失败，沿用缓存（{ex.Message}）");
            return;
        }

        foreach (var item in cached)
        {
            var (status, done, total, score) = Judge(LeafIdOf(item), progressMap);
            item.Status = status;
            item.Done = done;
            item.Total = total;
            item.Score = score;
        }
    }

    /// <summary>从稳定 Id（hw-{classroomId}-{leafId}）解析叶子 id；解析失败返回 null（判 Unknown）。</summary>
    private static long? LeafIdOf(TodoItem item)
    {
        var tail = item.Id[(item.Id.LastIndexOf('-') + 1)..];
        return long.TryParse(tail, CultureInfo.InvariantCulture, out var id) ? id : null;
    }

    /// <summary>作业日志条的增量签名：覆盖作业推导的全部日志侧输入字段。</summary>
    private static string SignatureOf(List<YktActivity> homeworkActivities)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var a in homeworkActivities.OrderBy(x => x.Id))
        {
            sb.Append(a.Id).Append('|')
              .Append(a.Type).Append('|')
              .Append(Normalize(a.Title)).Append('|')
              .Append(a.CreateTime.OrZero).Append('|')
              .Append(a.Content?.LeafId?.ToString(CultureInfo.InvariantCulture) ?? "-").Append('|')
              .Append(a.Content?.ScoreDeadlineRaw.OrZero ?? 0).Append(';');
        }
        return Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(sb.ToString())));
    }

    /// <summary>
    /// 判定完成状态。
    /// 实测 leaf_schedules 的值有两种形态：数字（该 leaf 无作答内容）或对象。
    /// 数字形态在实测中无法区分「未开始」与「已完成」，因此报 Unknown 而不是猜。
    /// </summary>
    private static (TodoStatus Status, int? Done, int? Total, double? Score) Judge(
        long? leafId,
        IReadOnlyDictionary<string, YktLeafProgress> map)
    {
        if (leafId is not { } id) return (TodoStatus.Unknown, null, null, null);
        if (!map.TryGetValue(id.ToString(CultureInfo.InvariantCulture), out var p)) return (TodoStatus.Unknown, null, null, null);
        if (p.IsBareNumber) return (TodoStatus.Unknown, null, null, null);

        var status = p switch
        {
            { Done: 0 } => TodoStatus.NotStarted,
            { Total: { } t, Done: { } d } when d < t => TodoStatus.InProgress,
            { Total: { } t2, Done: { } d2 } when d2 >= t2 => TodoStatus.Completed,
            // total 为 0/空（题目尚未组卷）时，done=0 已经在上面的分支处理，
            // 其余情况按已完成处理——与雨课堂页面一致。
            _ => TodoStatus.Completed,
        };

        return (status, p.Done, p.Total, p.Score);
    }

    private static DateTimeOffset? FromUnixMs(long ms)
    {
        if (ms <= 0) return null;
        DateTimeOffset value;
        try
        {
            value = DateTimeOffset.FromUnixTimeMilliseconds(ms).ToOffset(Beijing);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }

        return value < MinPlausible || value > MaxPlausible ? null : value;
    }

    private static string Trim(string name) => name.Length <= 18 ? name : name[..18] + "…";

    /// <summary>
    /// 标题归一化，用于日志与章节树的匹配。
    /// 两侧可能出现全角/半角空白或多余空格的差异，直接比较会漏配。
    /// </summary>
    private static string Normalize(string title) =>
        title.Trim().Replace('\u3000', ' ').Replace("  ", " ");
}
