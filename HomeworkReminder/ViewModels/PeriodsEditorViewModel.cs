using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using HomeworkReminder.Models;
using HomeworkReminder.Services;

namespace HomeworkReminder.ViewModels;

/// <summary>
/// 时间表编辑器里的一行：一个节次的起止时间。
/// <para>
/// 时间刻意用**字符串**而不是 <see cref="TimeOnly"/>：手输更容易（用户会直接敲 0800、
/// 8:00 这类写法），而且格式问题应该在**保存时**给出人能看懂的提示，
/// 而不是让输入框悄悄把非法值吞掉。
/// </para>
/// </summary>
public sealed partial class PeriodRowViewModel : ViewModelBase
{
    public PeriodRowViewModel(int period, string startTime, string endTime)
    {
        Period = period;
        StartTime = startTime;
        EndTime = endTime;
    }

    /// <summary>节次序号（从 1 起、连续；增删后统一重排）。</summary>
    [ObservableProperty]
    private int _period;

    [ObservableProperty]
    private string _startTime;

    [ObservableProperty]
    private string _endTime;

    /// <summary>这一节是否停用（起止都是 00:00）。</summary>
    public bool IsDisabled =>
        StartTime.Trim() == Timetable.DisabledPeriodTime && EndTime.Trim() == Timetable.DisabledPeriodTime;

    /// <summary>停用提示（界面上给个明确说法，而不是让用户猜 00:00 是什么意思）。</summary>
    public string DisabledHint => IsDisabled ? "本节停用" : string.Empty;

    public bool HasDisabledHint => IsDisabled;

    partial void OnStartTimeChanged(string value) => NotifyDisabledChanged();

    partial void OnEndTimeChanged(string value) => NotifyDisabledChanged();

    private void NotifyDisabledChanged()
    {
        OnPropertyChanged(nameof(IsDisabled));
        OnPropertyChanged(nameof(DisabledHint));
        OnPropertyChanged(nameof(HasDisabledHint));
    }
}

/// <summary>
/// 「设置 → 课表」里的节次时间表编辑器。
/// <para>
/// <b>为什么要能编辑</b>：各校作息不同（有的上午 4 节、有的 5 节，晚上最晚到 22:30 还是 21:00），
/// 内置的默认时间表只是常见值。时间表是周视图、待办、提醒三处时间的**唯一来源**
/// （见 <see cref="Services.Timetable.ResolveSessionTime"/>），所以改这里等于全校正。
/// </para>
/// <para>
/// <b>编辑的是一份副本</b>：只有点保存并通过校验后才写盘并刷新周视图，
/// 中途关掉设置页不会留下半套时间。
/// </para>
/// </summary>
public sealed partial class PeriodsEditorViewModel : ViewModelBase
{
    /// <summary>节次数量上限（与 <see cref="Services.Timetable.MaxPeriod"/> 一致）。</summary>
    public static int MaxPeriods => Timetable.MaxPeriod;

    /// <summary>可编辑的节次行。</summary>
    public ObservableCollection<PeriodRowViewModel> Rows { get; } = [];

    /// <summary>编辑区的状态/错误提示（校验失败时显示具体原因）。</summary>
    [ObservableProperty]
    private string _status = string.Empty;

    /// <summary>是否有提示要显示。</summary>
    public bool HasStatus => Status.Length > 0;

    partial void OnStatusChanged(string value) => OnPropertyChanged(nameof(HasStatus));

    /// <summary>校验失败时把提示标红（成功提示不标）。</summary>
    [ObservableProperty]
    private bool _statusIsError;

    /// <summary>有未保存的改动（用于提示用户别直接走）。</summary>
    [ObservableProperty]
    private bool _isDirty;

    /// <summary>保存回调：把校验通过的节次表写盘并刷新周视图。由 MainViewModel 注入。</summary>
    public Func<IReadOnlyList<PeriodSlot>, Task<string>>? SaveRequested { get; set; }

    /// <summary>用当前时间表填充编辑区（进入选项卡 / 保存成功后调用）。</summary>
    public void Load(IReadOnlyList<PeriodSlot> periods)
    {
        foreach (var row in Rows) row.PropertyChanged -= OnRowChanged;
        Rows.Clear();

        var source = periods.Count > 0 ? periods : Timetable.DefaultPeriods;
        foreach (var slot in source)
        {
            var row = new PeriodRowViewModel(slot.Period, slot.StartTime, slot.EndTime);
            // 逐行订阅：用户直接在输入框里改时间也要算「有改动」，
            // 否则「未保存」提示永远不会亮，切走时也无从提醒。
            row.PropertyChanged += OnRowChanged;
            Rows.Add(row);
        }
        Renumber();
        IsDirty = false;
        SetStatus(string.Empty, isError: false);
    }

    /// <summary>行内时间被改动 → 标记为有未保存改动，并清掉上一次的校验提示（它已经过期了）。</summary>
    private void OnRowChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(PeriodRowViewModel.StartTime) or nameof(PeriodRowViewModel.EndTime)))
            return;

        IsDirty = true;
        if (StatusIsError) SetStatus(string.Empty, isError: false);
    }

    /// <summary>在末尾追加一个节次（时间从上一节往后推，否则给个合理默认）。</summary>
    public void AddPeriod()
    {
        if (Rows.Count >= MaxPeriods)
        {
            SetStatus($"最多 {MaxPeriods} 节，不能再加了。", isError: true);
            return;
        }

        var (start, end) = SuggestNextTime();
        var row = new PeriodRowViewModel(Rows.Count + 1, start, end);
        row.PropertyChanged += OnRowChanged;
        Rows.Add(row);
        Renumber();
        IsDirty = true;
        SetStatus($"已新增第 {Rows.Count} 节，记得点「保存时间表」。", isError: false);
    }

    /// <summary>删掉最后一个节次。</summary>
    public void RemoveLast()
    {
        if (Rows.Count <= 1)
        {
            SetStatus("至少要保留 1 节。", isError: true);
            return;
        }

        var removed = Rows[^1];
        removed.PropertyChanged -= OnRowChanged;
        Rows.RemoveAt(Rows.Count - 1);
        Renumber();
        IsDirty = true;
        SetStatus($"已删除末节，现在共 {Rows.Count} 节，记得保存。", isError: false);
    }

    /// <summary>恢复内置默认时间表（一天 12 节）。</summary>
    public void ResetToDefault()
    {
        Load(Timetable.DefaultPeriods);
        IsDirty = true;
        SetStatus("已恢复默认时间表，记得点「保存时间表」。", isError: false);
    }

    /// <summary>
    /// 把某一行整节设为停用（起止都写 00:00）。
    /// <para>停用的节次不显示、不排课，也不参与先后/重叠校验——学校某天只有 11 节时不必删节次。</para>
    /// </summary>
    public void Disable(PeriodRowViewModel row)
    {
        row.StartTime = Timetable.DisabledPeriodTime;
        row.EndTime = Timetable.DisabledPeriodTime;
        IsDirty = true;
    }

    /// <summary>把某一行整节重新启用（给一组不冲突的默认时间）。</summary>
    public void Enable(PeriodRowViewModel row)
    {
        if (!row.IsDisabled) return;
        var (start, end) = SuggestTimeFor(row.Period);
        row.StartTime = start;
        row.EndTime = end;
        IsDirty = true;
    }

    /// <summary>
    /// 校验并保存。
    /// <para>
    /// <b>先校验、再落盘</b>：<see cref="Services.Timetable.ValidatePeriods"/> 会抛出带具体原因的异常
    /// （序号不连续、时间格式不对、开始不早于结束、相邻节次重叠），这里把原因原样显示给用户，
    /// 而不是静默改回默认值——写坏时间表会让整张课表的时间都错，必须让用户知道。
    /// </para>
    /// </summary>
    public async Task<bool> SaveAsync()
    {
        var drafts = Rows.Select(r => new PeriodSlot
        {
            Period = r.Period,
            StartTime = r.StartTime.Trim(),
            EndTime = r.EndTime.Trim(),
        }).ToList();

        try
        {
            Timetable.ValidatePeriods(drafts);
        }
        catch (ArgumentException ex)
        {
            // 校验失败：不写盘、不改内存，保持用户输入原样，让他接着改。
            SetStatus($"时间表有误：{ex.Message}", isError: true);
            return false;
        }

        if (SaveRequested is null)
        {
            SetStatus("保存通道未就绪，请重新打开设置页。", isError: true);
            return false;
        }

        try
        {
            var message = await SaveRequested(drafts).ConfigureAwait(true);
            IsDirty = false;
            SetStatus(message, isError: false);
            return true;
        }
        catch (Exception ex)
        {
            SetStatus($"保存失败：{ex.Message}", isError: true);
            return false;
        }
    }

    // ---------------- 内部 ----------------

    /// <summary>节次序号统一按行序重排（删掉中间一节后不能留下断号）。</summary>
    private void Renumber()
    {
        for (var i = 0; i < Rows.Count; i++) Rows[i].Period = i + 1;
    }

    private (string Start, string End) SuggestNextTime()
    {
        var last = Rows.LastOrDefault(r => !r.IsDisabled);
        if (last is null) return ("08:00", "08:45");

        // 上一节结束后隔 10 分钟，每节 45 分钟——与内置默认表的口径一致。
        if (!TryParseMinutes(last.EndTime, out var end)) return ("08:00", "08:45");
        var start = Math.Min(end + 10, 23 * 60 + 15);
        return (Format(start), Format(start + 45));
    }

    private (string Start, string End) SuggestTimeFor(int period)
    {
        // 优先沿用内置默认表里同一节的时间；越界则按「第 N 节从 08:00 起每节 50 分钟」推。
        if (period >= 1 && period <= Timetable.DefaultPeriods.Count)
        {
            var slot = Timetable.DefaultPeriods[period - 1];
            return (slot.StartTime, slot.EndTime);
        }

        var start = Math.Min(8 * 60 + (period - 1) * 50, 23 * 60);
        return (Format(start), Format(start + 45));
    }

    private static bool TryParseMinutes(string? value, out int minutes)
    {
        minutes = 0;
        if (!Timetable.IsValidTime(value)) return false;
        minutes = int.Parse(value!.AsSpan(0, 2)) * 60 + int.Parse(value.AsSpan(3, 2));
        return true;
    }

    private static string Format(int minutes) =>
        $"{Math.Clamp(minutes, 0, 23 * 60 + 59) / 60:00}:{Math.Clamp(minutes, 0, 23 * 60 + 59) % 60:00}";

    private void SetStatus(string message, bool isError)
    {
        Status = message;
        StatusIsError = isError;
    }
}
