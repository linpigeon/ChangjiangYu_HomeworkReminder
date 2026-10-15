using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HomeworkReminder.Models;

namespace HomeworkReminder.Services;

/// <summary>
/// 批改网同步：把「未完成作文」列表映射成待办模型。
/// <para>
/// 与雨课堂的 <see cref="SyncService"/> 不同，这里只有一个列表页、没有按课程扇出、
/// 也没有任何「完成进度」接口——未完成列表本身就已经是「还没做的事」，
/// 这正是待办语义，因此状态一律按 <see cref="TodoStatus.NotStarted"/> 处理。
/// </para>
/// </summary>
public static class PigaiSyncService
{
    /// <summary>同步一次，返回未完成作文的待办条目。</summary>
    public static async Task<IReadOnlyList<TodoItem>> SyncAsync(
        string cookieHeader, CancellationToken ct = default)
    {
        using var api = new PigaiApiClient(cookieHeader);
        var tasks = await api.GetIncompleteEssaysAsync(ct).ConfigureAwait(false);
        return tasks.Select(Map).ToList();
    }

    /// <summary>
    /// 映射说明（批改网没有「课程」这一维度，只有出题人与指定班级）：
    /// 主标题用出题人（稳定、可辨识），副标题用作文标题；指定班级放进 Subject，
    /// 供将来的分组使用。Id 用 <c>hw-pg-{rid}</c>，与雨课堂的
    /// <c>hw-{classroomId}-{leafId}</c>、学习通的 <c>hw-cx-…</c> 同族但不冲突。
    /// </summary>
    private static TodoItem Map(PigaiApiClient.PigaiTask t) => new()
    {
        Id = $"hw-pg-{t.Rid}",
        Kind = TodoKind.Homework,
        Title = t.Title,
        // 出题人（如「罗小芳」）。
        CourseName = string.IsNullOrWhiteSpace(t.Teacher) ? "批改网" : t.Teacher,
        // 指定班级（页面只有班级、没有课程名）。
        Subject = string.IsNullOrWhiteSpace(t.Classes) ? "批改网" : TrimTrailing(t.Classes),
        ClassroomId = t.Rid,
        DueAt = t.DueAt,
        PublishedAt = null, // 列表页不提供发布时间
        // 未完成列表里的条目就是「还没写」，没有更细的状态可用。
        Status = TodoStatus.NotStarted,
        SourceUrl = PigaiApiClient.EssayUrl(t.Rid),
    };

    /// <summary>「A班,B班,」这类列表串末尾会多一个逗号，顺手去掉。</summary>
    private static string TrimTrailing(string s) => s.Trim().TrimEnd(',', '，').Trim();

    /// <summary>
    /// 供离线自检（<c>--jsoncheck</c>）验证映射规则用的入口。
    /// 与生产路径共用同一个 <see cref="Map"/>，不是另写一份逻辑。
    /// </summary>
    internal static TodoItem MapForTest(PigaiApiClient.PigaiTask task) => Map(task);
}
