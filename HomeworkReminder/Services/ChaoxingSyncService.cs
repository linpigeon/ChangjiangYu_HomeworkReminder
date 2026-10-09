using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HomeworkReminder.Models;

namespace HomeworkReminder.Services;

/// <summary>
/// 学习通同步：作业（stu-work）与考试（examcode）两个列表合并成待办模型。
/// 与雨课堂的 <see cref="SyncService"/> 不同，这里没有按课程扇出——两个列表页
/// 已经带了全部需要的信息（标题/状态/课程/剩余时间/深链）。
/// </summary>
public static class ChaoxingSyncService
{
    /// <summary>
    /// 同步一次，返回待办条目（作业 + 考试）。
    /// 剩余时间折算成绝对截止时间（以同步时刻为基准）。
    /// </summary>
    public static async Task<IReadOnlyList<TodoItem>> SyncAsync(
        string cookieHeader, CancellationToken ct = default)
    {
        using var api = new ChaoxingApiClient(cookieHeader);

        // 两个列表相互独立，但都是轻量单页，串行即可（避免触发风控）。
        var work = await api.GetHomeworkAsync(ct).ConfigureAwait(false);
        var exams = await api.GetExamsAsync(ct).ConfigureAwait(false);

        var now = DateTimeOffset.Now;
        return work.Concat(exams).Select(t => Map(t, now)).ToList();
    }

    private static TodoItem Map(ChaoxingApiClient.ChaoxingTask t, DateTimeOffset now) => new()
    {
        Id = $"hw-cx-{(t.IsExam ? "exam" : "work")}-{t.TaskRefId}",
        Kind = TodoKind.Homework,
        Title = t.Title,
        CourseName = t.CourseName,
        Subject = t.CourseName,
        ClassroomId = t.CourseId,
        DueAt = t.Remaining is { } r ? now + r : null,
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
