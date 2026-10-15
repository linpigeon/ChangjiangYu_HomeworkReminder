using System.Collections.Generic;

namespace HomeworkReminder.Models;

/// <summary>
/// 一门学习通课程的「未完成任务点」汇总。
/// <para>
/// 与 <see cref="TodoItem"/> 分开建模是刻意的：任务点是**课程维度的进度指标**
/// （粒度到小节，一门课动辄几十个），不是一条条待办。把它拆成逐条待办会把列表淹掉，
/// 所以这里按课程汇总展示，明细放在 <see cref="Leaves"/> 里供展开查看。
/// </para>
/// </summary>
public sealed class ChaoxingCourse
{
    /// <summary>课程 id（超星的 courseId）。</summary>
    public long CourseId { get; init; }

    /// <summary>班级 id（超星的 clazzId）。</summary>
    public long ClazzId { get; init; }

    /// <summary>课程名（如「信号与系统」）。</summary>
    public string CourseName { get; init; } = string.Empty;

    /// <summary>班级名（如「2026-2027-1-通信」），用于区分同一门课的多个班。</summary>
    public string ClazzName { get; init; } = string.Empty;

    /// <summary>该课是否已结课。</summary>
    public bool IsRetired { get; init; }

    /// <summary>未完成任务点总数（只统计叶子章节，已排除父层汇总值）。</summary>
    public int PendingTaskPoints { get; set; }

    /// <summary>未完成任务点的明细（叶子章节）。</summary>
    public List<ChaoxingTaskPoint> Leaves { get; set; } = [];
}

/// <summary>一个叶子章节的未完成任务点。</summary>
public sealed class ChaoxingTaskPoint
{
    /// <summary>章节号，如「4.5」。</summary>
    public string ChapterNumber { get; init; } = string.Empty;

    /// <summary>章节名，如「傅里叶变换的性质」。</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>该节的未完成任务点数。</summary>
    public int PendingCount { get; init; }
}
