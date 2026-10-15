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

    /// <summary>学科名（courses/list 内嵌 course.name，如「概率论与数理统计B」）。旧缓存没有时为空。</summary>
    public string? Subject { get; set; }

    public long ClassroomId { get; init; }

    /// <summary>
    /// 绝对截止时间（北京时间）。null 表示无期限——接口确实会返回空串，
    /// 或因来源只给了相对时间而见 <see cref="DeadlineAnchor"/>。
    /// 取值请用 <see cref="EffectiveDueAt"/>，它会覆盖相对时间那种形态。
    /// </summary>
    public DateTimeOffset? DueAt { get; set; }

    /// <summary>
    /// 相对剩余时间的基准时刻。仅「来源只给相对时间」的条目（学习通列表页）会填：
    /// 此时 <see cref="DueAt"/> 为空，截止时间 = <see cref="DeadlineAnchor"/> + <see cref="RemainingAtSync"/>。
    /// <para>
    /// 为什么要存这份而不只存推算出的绝对时间：页面上写的是「剩余 2 天」，绝对时间是我们
    /// 用同步那一刻加上去的。若把它当作既定事实落盘，应用关掉三天再打开，缓存首屏就会
    /// 显示「剩余 2 天」而实际只剩几小时——倒计时提醒类应用里这是会误事的错。
    /// 存下基准与相对量，任何时刻重新读取都能还原出正确的剩余时间。
    /// </para>
    /// </summary>
    public DateTimeOffset? DeadlineAnchor { get; set; }

    /// <summary>同步时刻的剩余时间，配合 <see cref="DeadlineAnchor"/> 使用。</summary>
    public TimeSpan? RemainingAtSync { get; set; }

    /// <summary>
    /// 实际生效的截止时间：优先绝对时间，其次由「基准 + 相对剩余」推算。
    /// </summary>
    public DateTimeOffset? EffectiveDueAt =>
        DueAt ?? (DeadlineAnchor is { } anchor && RemainingAtSync is { } left ? anchor + left : null);

    /// <summary>发布时间（北京时间）。</summary>
    public DateTimeOffset? PublishedAt { get; init; }

    // 增量同步会在「签名未变」的课程上就地把完成状态刷新为最新校验结果，因此是可写的。
    public TodoStatus Status { get; set; } = TodoStatus.Unknown;

    /// <summary>已完成题数。</summary>
    public int? Done { get; set; }

    /// <summary>总题数。0 表示题目尚未组卷（此时 done/total 显示为 0/0）。</summary>
    public int? Total { get; set; }

    public double? Score { get; set; }

    /// <summary>雨课堂站内的原始链接，便于跳回原页面。增量校验路径会按新日志重建，故可写。</summary>
    public string? SourceUrl { get; set; }

    /// <summary>应用内的已读状态（雨课堂单条已读没有服务端接口，仅 user_read_all 可反写）。</summary>
    public bool IsRead { get; set; }

    /// <summary>已完成的作业不算待办。</summary>
    public bool IsActionable => Status != TodoStatus.Completed;

    /// <summary>是否已过截止时间但尚未完成。</summary>
    public bool IsOverdue => IsActionable && EffectiveDueAt is { } d && d < DateTimeOffset.Now;

    /// <summary>距离截止的剩余时间，无期限时为 null。</summary>
    public TimeSpan? Remaining => EffectiveDueAt is { } d ? d - DateTimeOffset.Now : null;

    /// <summary>供界面直接绑定的进度文本，例如 "2/22"。</summary>
    public string ProgressText =>
        Done is { } done && Total is { } total ? $"{done}/{total}" : "—";

    /// <summary>供界面直接绑定的截止时间文本。</summary>
    public string DueText => EffectiveDueAt is { } d ? d.ToString("MM-dd HH:mm") : "无期限";

    /// <summary>供界面直接绑定的剩余时间文本。</summary>
    public string RemainingText
    {
        get
        {
            if (EffectiveDueAt is not { } due) return "无期限";
            var left = due - DateTimeOffset.Now;
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

    /// <summary>
    /// 把相对剩余时间「续期」到新的基准时刻：截止时间不变，基准整体前移。
    /// 增量同步复用旧缓存条目时调用，避免被 <see cref="TodoCacheStore"/> 的
    /// 时间戳合法性校验当成脏数据丢弃。
    /// <para>
    /// 刻意不要求 <see cref="DeadlineAnchor"/> 已存在：旧缓存（本字段加入之前写入的）
    /// 只有 <see cref="RemainingAtSync"/>，正是最需要回填基准的情况。
    /// 条件写成「有旧基准」会让这些条目永远补不上基准、截止时间变成无期限。
    /// </para>
    /// </summary>
    public void Reanchor(DateTimeOffset newAnchor)
    {
        if (DueAt is not null) return;
        if (RemainingAtSync is not { } left) return;

        // 旧基准存在时，剩余量要扣掉「基准到新基准」流逝的部分，截止时间才不变；
        // 没有旧基准时，剩余量本身就是相对新基准而言的，原样保留。
        // 这与下面的 Rebase 等价（旧基准未知时视为新基准）。
        if (DeadlineAnchor is { } oldAnchor)
            RemainingAtSync = oldAnchor + left - newAnchor;

        DeadlineAnchor = newAnchor;
    }

    /// <summary>
    /// 同上，但明确区分两个时刻：<paramref name="relativeOrigin"/> 是
    /// <see cref="RemainingAtSync"/> 的测量基准，<paramref name="newAnchor"/> 是落盘基准。
    /// 两者不同时（例如测量发生在同步开始、落盘对象带的却是另一个时刻）必须用这个，
    /// 否则算出的截止时间会静默偏移「两个时刻之差」。
    /// </summary>
    public void Rebase(DateTimeOffset relativeOrigin, DateTimeOffset newAnchor)
    {
        if (DueAt is not null) return;
        if (RemainingAtSync is not { } left) return;

        RemainingAtSync = relativeOrigin + left - newAnchor;
        DeadlineAnchor = newAnchor;
    }
}
