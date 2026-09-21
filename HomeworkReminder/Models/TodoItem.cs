using System;

namespace HomeworkReminder.Models;

/// <summary>
/// 一条待办。作业与公告统一到这个模型，界面按 <see cref="Kind"/> 分流。
/// </summary>
public sealed class TodoItem
{
    /// <summary>稳定标识，用于去重与增量同步；改标题不会产生重复项。</summary>
    public required string Id { get; init; }

    public required TodoKind Kind { get; init; }

    public required string Title { get; init; }

    /// <summary>课程名（雨课堂的 classroom name，可能含多个班级）。</summary>
    public string CourseName { get; init; } = string.Empty;

    public long ClassroomId { get; init; }

    /// <summary>截止时间（北京时间）。null 表示无期限——接口确实会返回空串。</summary>
    public DateTimeOffset? DueAt { get; init; }

    /// <summary>发布时间（北京时间）。</summary>
    public DateTimeOffset? PublishedAt { get; init; }

    // 增量同步会在「签名未变」的课程上就地把完成状态刷新为最新校验结果，因此是可写的。
    public TodoStatus Status { get; set; } = TodoStatus.Unknown;

    /// <summary>已完成题数。</summary>
    public int? Done { get; set; }

    /// <summary>总题数。0 表示题目尚未组卷（此时 done/total 显示为 0/0）。</summary>
    public int? Total { get; set; }

    public double? Score { get; set; }

    /// <summary>雨课堂站内的原始链接，便于跳回原页面。</summary>
    public string? SourceUrl { get; init; }

    /// <summary>应用内的已读状态（雨课堂单条已读没有服务端接口，仅 user_read_all 可反写）。</summary>
    public bool IsRead { get; set; }

    /// <summary>已完成的作业不算待办。</summary>
    public bool IsActionable => Status != TodoStatus.Completed;

    /// <summary>是否已过截止时间但尚未完成。</summary>
    public bool IsOverdue => IsActionable && DueAt is { } d && d < DateTimeOffset.Now;

    /// <summary>距离截止的剩余时间，无期限时为 null。</summary>
    public TimeSpan? Remaining => DueAt is { } d ? d - DateTimeOffset.Now : null;

    /// <summary>供界面直接绑定的进度文本，例如 "2/22"。</summary>
    public string ProgressText =>
        Done is { } done && Total is { } total ? $"{done}/{total}" : "—";

    /// <summary>供界面直接绑定的截止时间文本。</summary>
    public string DueText => DueAt is { } d ? d.ToString("MM-dd HH:mm") : "无期限";

    /// <summary>供界面直接绑定的剩余时间文本。</summary>
    public string RemainingText
    {
        get
        {
            if (DueAt is null) return "无期限";
            var left = DueAt.Value - DateTimeOffset.Now;
            if (left < TimeSpan.Zero) return $"已过期 {-left.TotalDays:F1} 天";
            if (left.TotalDays >= 1) return $"剩 {left.TotalDays:F1} 天";
            if (left.TotalHours >= 1) return $"剩 {left.TotalHours:F1} 小时";
            return $"剩 {Math.Max(1, (int)left.TotalMinutes)} 分钟";
        }
    }

    /// <summary>状态的中文标签。</summary>
    public string StatusText => Status switch
    {
        TodoStatus.NotStarted => "未开始",
        TodoStatus.InProgress => "进行中",
        TodoStatus.Completed => "已完成",
        _ => "未知",
    };
}
