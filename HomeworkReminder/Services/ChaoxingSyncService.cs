using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HomeworkReminder.Models;

namespace HomeworkReminder.Services;

/// <summary>
/// 学习通同步：作业（stu-work）与考试（examcode）两个列表合并成待办模型，
/// 另加**课程维度的未完成任务点汇总**。
/// <para>
/// 与雨课堂的 <see cref="SyncService"/> 不同，这里作业/考试不按课程扇出——
/// 两个列表页已经带了全部需要的信息（标题/状态/课程/剩余时间/深链）。
/// 任务点则相反：必须按课程逐个拉（见 <see cref="SyncCoursesAsync"/>）。
/// </para>
/// </summary>
public static class ChaoxingSyncService
{
    /// <summary>
    /// 同步一次，返回待办条目（作业 + 考试）。
    /// 剩余时间折算成绝对截止时间（以同步时刻为基准），同时把「同步时刻」与
    /// 「相对剩余时间」一并记录进缓存列表：这是唯一能忠实还原的信息——
    /// 只存绝对时间的话，应用关掉三天再打开，缓存里的倒计时会少算三天。
    /// </summary>
    /// <param name="cookieHeader">学习通整条 Cookie。</param>
    /// <param name="syncedAt">本次同步的时刻（调用方统一传入，保证绝对与相对两种表示同源）。</param>
    /// <param name="ct">取消令牌。</param>
    public static async Task<(IReadOnlyList<TodoItem> Items, IReadOnlyList<TodoItem> ForCache)> SyncAsync(
        string cookieHeader, DateTimeOffset syncedAt, CancellationToken ct = default)
    {
        using var api = new ChaoxingApiClient(cookieHeader);

        // 两个列表相互独立，但都是轻量单页，串行即可（避免触发风控）。
        var work = await api.GetHomeworkAsync(ct).ConfigureAwait(false);
        var exams = await api.GetExamsAsync(ct).ConfigureAwait(false);

        // 相对剩余时间的测量基准（页面抓取的那一刻）。与落盘基准分开传，
        // 是为了不去假设「抓取时刻」与「落盘基准」永远是同一个值——
        // 一旦两者不同，绝对截止时间与缓存推算值会静默偏移两者之差。
        var measuredAt = DateTimeOffset.Now;
        var tasks = work.Concat(exams).ToList();
        var items = tasks.Select(t => Map(t, measuredAt)).ToList();
        var forCache = tasks.Select(t => MapForCache(t, measuredAt, syncedAt)).ToList();
        return (items, forCache);
    }

    /// <summary>
    /// 同步「课程维度」的未完成任务点。
    /// <para>
    /// 流程：课程列表（<c>backclazzdata</c>，一次拿到 clazzId/courseId/personId）→
    /// 逐课拉课程首页 <c>stucoursemiddle</c> → 解析「待完成任务点」章节树的叶子节点。
    /// </para>
    /// <para>
    /// <b>不对课程做任何预过滤。</b>曾经按「已结课就跳过」省请求，
    /// 结果漏掉了 <c>信号与系统</c>（13 个未完成任务点）——它没有 <c>isretire</c> 字段，
    /// 真正已结课的课在响应里是 <c>isretire=1</c>，缺失的既不是 1 也不该被当成已结课来跳过。
    /// 现在一律请求：没有「待完成任务点」区块的课程自然得到 0，不需要靠猜。
    /// </para>
    /// <para>
    /// 单课失败只跳过该课，不中断整次同步（与两个列表页的容错口径一致）。
    /// </para>
    /// </summary>
    public static async Task<(IReadOnlyList<ChaoxingCourse> Courses, IReadOnlyList<string> Warnings)>
        SyncCoursesAsync(
            string cookieHeader,
            IProgress<string>? progress = null,
            CancellationToken ct = default)
    {
        using var api = new ChaoxingApiClient(cookieHeader);
        var warnings = new List<string>();

        var courses = await api.GetCoursesAsync(ct).ConfigureAwait(false);

        var result = new List<ChaoxingCourse>();
        for (var i = 0; i < courses.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var c = courses[i];
            progress?.Report($"正在读取课程任务点 {i + 1}/{courses.Count}：{c.CourseName}");

            ChaoxingApiClient.ChaoxingCourseTaskPoints points;
            try
            {
                points = await api.GetPendingTaskPointsAsync(c.CourseId, c.ClazzId, c.PersonId, ct)
                    .ConfigureAwait(false);
            }
            catch (ChaoxingAuthExpiredException)
            {
                throw;
            }
            catch (ChaoxingApiException ex)
            {
                warnings.Add($"{c.CourseName}：任务点读取失败（{ex.Message}）");
                continue;
            }

            result.Add(new ChaoxingCourse
            {
                CourseId = c.CourseId,
                ClazzId = c.ClazzId,
                CourseName = c.CourseName,
                ClazzName = c.ClazzName,
                IsRetired = c.IsRetired,
                PendingTaskPoints = points.PendingTotal,
                Leaves = points.Leaves
                    .Select(l => new ChaoxingTaskPoint
                    {
                        ChapterNumber = l.ChapterNumber,
                        Name = l.Name,
                        PendingCount = l.PendingCount,
                    })
                    .ToList(),
            });
        }

        return (result, warnings);
    }

    /// <summary>
    /// 界面形态：给出绝对截止时间（参与排序与「我的一天」判定），
    /// 同时保留基准与相对量，便于按需还原。
    /// </summary>
    private static TodoItem Map(ChaoxingApiClient.ChaoxingTask t, DateTimeOffset measuredAt)
    {
        var item = MapForCache(t, measuredAt, measuredAt);
        item.DueAt = t.Remaining is { } r ? measuredAt + r : null;
        return item;
    }

    /// <summary>缓存形态：截止时间留空，只记「基准 + 相对剩余时间」。</summary>
    private static TodoItem MapForCache(
        ChaoxingApiClient.ChaoxingTask t, DateTimeOffset measuredAt, DateTimeOffset anchor) => new()
    {
        Id = $"hw-cx-{(t.IsExam ? "exam" : "work")}-{t.TaskRefId}",
        Kind = TodoKind.Homework,
        Title = t.Title,
        CourseName = t.CourseName,
        Subject = t.CourseName,
        ClassroomId = t.CourseId,
        DueAt = null,
        DeadlineAnchor = anchor,
        RemainingAtSync = t.Remaining,
        PublishedAt = null, // 列表页不提供发布时间
        Status = MapStatus(t.Status),
        SourceUrl = t.SourceUrl,
    };

    /// <summary>
    /// 状态映射：未提交/未交 = 未开始；待批阅 = 进行中（已交待改）；
    /// 已批阅/已完成/已结束 = 已完成。其余按未知处理（视为未完成）。
    /// </summary>
    private static TodoStatus MapStatus(string status) => status switch
    {
        "未提交" or "未交" => TodoStatus.NotStarted,
        "待批阅" => TodoStatus.InProgress,
        "已批阅" or "已完成" or "已结束" => TodoStatus.Completed,
        _ => TodoStatus.Unknown,
    };
}
