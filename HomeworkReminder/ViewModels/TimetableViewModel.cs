using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using HomeworkReminder.Models;
using HomeworkReminder.Services;

namespace HomeworkReminder.ViewModels;

/// <summary>
/// 周视图里的一个课块（跨多个节次的整块）。
/// <para>
/// 位置与尺寸由 <see cref="Services.TimetableLayout"/> 一次算好（绝对像素坐标），
/// 视图只负责把矩形摆到画布上——这样跨节次占高、同格多门课分栏这两件事
/// 都不必依赖布局控件的能力。
/// </para>
/// </summary>
public sealed partial class TimetableBlockViewModel : ViewModelBase
{
    public TimetableBlockViewModel(ScheduleItem item, TimetableViewModels owner, TimetableLayout.BlockRect rect)
    {
        Model = item;
        Rect = rect;
        Fill = CourseColor.For(item.Name, owner.IsDarkTheme).Fill;
        Line = CourseColor.For(item.Name, owner.IsDarkTheme).Line;
        TextColor = CourseColor.For(item.Name, owner.IsDarkTheme).Text;
        Name = string.IsNullOrWhiteSpace(item.Name) ? "（未命名课程）" : item.Name;
        DetailText = string.Join(" · ",
            new[] { item.Teacher, item.Location }.Where(s => !string.IsNullOrWhiteSpace(s)));
        _isCurrentWeek = owner.IsCourseActiveInWeek(item);
    }

    public ScheduleItem Model { get; }

    /// <summary>画布上的矩形（绝对定位）。</summary>
    public TimetableLayout.BlockRect Rect { get; }

    public string Name { get; }

    public double X => Rect.X;

    public double Y => Rect.Y;

    public double Width => Rect.Width;

    public double Height => Rect.Height;

    /// <summary>课块底色（按课程名稳定取色）。</summary>
    public string Fill { get; }

    /// <summary>课块左边线与强调色。</summary>
    public string Line { get; }

    /// <summary>文字色（与底色配套，保证对比度）。</summary>
    public string TextColor { get; }

    public string? Teacher => Model.Teacher;

    public string? Location => Model.Location;

    /// <summary>教师与地点的合并副标题（空则整行不显示）。</summary>
    public string DetailText { get; }

    public bool HasDetail => DetailText.Length > 0;

    /// <summary>节次文案（如「3-4 节」）。</summary>
    public string PeriodText => Model.StartPeriod == Model.EndPeriod
        ? $"第 {Model.StartPeriod} 节"
        : $"{Model.StartPeriod}-{Model.EndPeriod} 节";

    /// <summary>时间文案（由节次时间表推导，见 <see cref="Services.Timetable.ResolveSessionTime"/>）。</summary>
    public string TimeText => $"{Model.StartTime}-{Model.EndTime}";

    /// <summary>周次文案（如「1-16 周」或「1-15 周(单)」）。</summary>
    public string WeekText
    {
        get
        {
            var range = Model.StartWeek == Model.EndWeek
                ? $"{Model.StartWeek} 周"
                : $"{Model.StartWeek}-{Model.EndWeek} 周";
            return Model.WeekParity switch
            {
                WeekParity.Odd => $"{range}(单)",
                WeekParity.Even => $"{range}(双)",
                _ => range,
            };
        }
    }

    /// <summary>这一周是否真的有这门课（否则是「其它周次」的课，弱化显示）。</summary>
    [ObservableProperty]
    private bool _isCurrentWeek;

    partial void OnIsCurrentWeekChanged(bool value)
    {
        OnPropertyChanged(nameof(IsOtherWeek));
        OnPropertyChanged(nameof(WeekBadge));
        OnPropertyChanged(nameof(Tooltip));
    }

    /// <summary>不是本周的课（仅当「显示其它周次课程」打开时才会出现在网格里）。</summary>
    public bool IsOtherWeek => !IsCurrentWeek;

    /// <summary>课块角标：非本周时标出它实际是哪几周，避免看起来像「排错了」。</summary>
    public string WeekBadge => IsCurrentWeek ? string.Empty : WeekText;

    /// <summary>课块高度是否够显示副标题（只有一行高时挤不下）。</summary>
    public bool CanShowDetail => Height >= TimetableLayout.PeriodRowHeight + 12;

    /// <summary>
    /// 课块宽度是否够显示地点。
    /// <para>
    /// 同格多门课分栏后每栏可能只有 78px，地点会被截成「博文…」这种没信息量的碎片，
    /// 不如不显示——完整信息在 hover 提示与点击后的详情面板里都有。
    /// </para>
    /// </summary>
    public bool CanShowLocation => Width >= 104;

    /// <summary>提示：课程、时间、周次、教师地点，供 hover 显示完整信息。</summary>
    public string Tooltip
    {
        get
        {
            var head = IsCurrentWeek ? Name : $"{Name}（{WeekText}，本周不上）";
            return $"{head}\n{PeriodText} {TimeText}\n{WeekText}"
                   + (HasDetail ? $"\n{DetailText}" : string.Empty);
        }
    }
}

/// <summary>周视图的一天列表头（星期 + 日期，以及「今天」标记）。</summary>
public sealed partial class TimetableDayHeaderViewModel : ViewModelBase
{
    public TimetableDayHeaderViewModel(int weekday, string name, string dateText, bool isToday)
    {
        Weekday = weekday;
        DayName = name;
        DateText = dateText;
        IsToday = isToday;
    }

    /// <summary>星期（1 = 周一 … 7 = 周日）。</summary>
    public int Weekday { get; }

    /// <summary>「周一」这类星期文案。</summary>
    public string DayName { get; }

    /// <summary>日期文案（未配置学期基准时为空）。</summary>
    public string DateText { get; }

    /// <summary>是否今天（列头高亮）。</summary>
    public bool IsToday { get; }

    public bool HasDate => DateText.Length > 0;
}

/// <summary>课表左侧的节次刻度（第 N 节 + 时间）。</summary>
public sealed class TimetablePeriodViewModel(int period, string timeText, double top, double height)
{
    public int Period { get; } = period;

    /// <summary>节次序号文案。</summary>
    public string Label { get; } = period.ToString();

    /// <summary>该节起止时间（停用节次为空）。</summary>
    public string TimeText { get; } = timeText;

    public bool HasTime => TimeText.Length > 0;

    /// <summary>在网格画布上的顶部位置。</summary>
    public double Top { get; } = top;

    /// <summary>行高。</summary>
    public double Height { get; } = height;
}

/// <summary>
/// 课表视图模型：周次切换、按周投影网格、教务导入入口、课程详情与「下一节课」。
/// <para>
/// 网格是**时间轴**：行 = 节次、列 = 星期，课块按绝对像素矩形摆放，
/// 因此「1-2 节」的课真的占两行高，第 3 节紧接其下。
/// </para>
/// <para>
/// 「本周不上、但整个学期有这门课」的条目默认**不画**（与开源课表类应用一致：
/// 网格只呈现这一周真实要上的课），打开开关后以淡化样式画出并标注实际周次。
/// </para>
/// </summary>
public sealed partial class TimetableViewModels : ViewModelBase
{
    /// <summary>
    /// 节次刻度列宽度（与视图一致）。
    /// <para>
    /// 要放得下 <c>08:00-08:45</c> 这串时间（字号 9）。54px 时会被裁成「08:00-08:4」——
    /// 因为刻度内容要居中，两边都得留出余量，不能按文字裸宽算。
    /// </para>
    /// </summary>
    public const double PeriodColumnWidth = 68;

    /// <summary>网格最小高度：避免课少时画布塌成一条。</summary>
    private const double MinGridHeight = 520;

    /// <summary>网格最大高度：超过就纵向滚动，而不是把整页撑长。</summary>
    private const double MaxGridHeight = 720;

    /// <summary>网格总宽度（由视图在尺寸变化时写入）。</summary>
    [ObservableProperty]
    private double _gridWidth = 900;

    partial void OnGridWidthChanged(double value) => RebuildRequested?.Invoke();

    /// <summary>当前显示的周次（1 基）。</summary>
    [ObservableProperty]
    private int _weekNumber = 1;

    /// <summary>
    /// 周次变了就重算网格。
    /// <para>
    /// <b>这条不能少</b>：<see cref="MainViewModel.ShiftTimetableWeek"/> 只改这个属性，
    /// 漏掉它就会出现「周次变了、课表没变」——箭头点了像没反应
    /// （实测踩过：重写时把这一条弄丢了，WeekNumber 从 7 一路加到 11，WeekLabel 还停在「第 7 周」）。
    /// </para>
    /// </summary>
    partial void OnWeekNumberChanged(int value) => RebuildRequested?.Invoke();

    /// <summary>是否配置了学期第一周（决定是否显示日期）。</summary>
    [ObservableProperty]
    private bool _hasTermAnchor;

    /// <summary>周次提示（如「第 3 周 · 09-21 ~ 09-27」）。</summary>
    [ObservableProperty]
    private string _weekLabel = string.Empty;

    /// <summary>当前显示的周次是不是「今天所在的周」（决定是否高亮今天）。</summary>
    [ObservableProperty]
    private bool _isCurrentWeekDisplayed = true;

    /// <summary>本周课程数（表头副标题用）。</summary>
    [ObservableProperty]
    private int _blockCount;

    /// <summary>「下一节课」提示（如「下一节：高等数学 · 10:00 明德楼B107」）。</summary>
    [ObservableProperty]
    private string _nextClassText = string.Empty;

    /// <summary>下一节课是否有内容（控制提示条可见性）。</summary>
    public bool HasNextClass => NextClassText.Length > 0;

    partial void OnNextClassTextChanged(string value) => OnPropertyChanged(nameof(HasNextClass));

    /// <summary>是否显示周六周日（桌面宽度足够时默认显示）。</summary>
    [ObservableProperty]
    private bool _showWeekend = true;

    partial void OnShowWeekendChanged(bool value) => RebuildRequested?.Invoke();

    /// <summary>是否把「其它周次」的课程也画出来（淡化 + 标注周次）。</summary>
    [ObservableProperty]
    private bool _showOtherWeeks;

    partial void OnShowOtherWeeksChanged(bool value) => RebuildRequested?.Invoke();

    /// <summary>当前选中的课块（点击课块后显示详情面板）。</summary>
    [ObservableProperty]
    private TimetableBlockViewModel? _selectedBlock;

    public bool HasSelection => SelectedBlock is not null;

    partial void OnSelectedBlockChanged(TimetableBlockViewModel? value) =>
        OnPropertyChanged(nameof(HasSelection));

    /// <summary>当前主题是否深色（决定课程配色取哪一套）。</summary>
    public bool IsDarkTheme { get; private set; }

    /// <summary>课块（绝对定位，按画布摆放）。</summary>
    public ObservableCollection<TimetableBlockViewModel> Blocks { get; } = [];

    /// <summary>节次刻度（左侧一列）。</summary>
    public ObservableCollection<TimetablePeriodViewModel> Periods { get; } = [];

    /// <summary>星期列表头。</summary>
    public ObservableCollection<TimetableDayHeaderViewModel> DayHeaders { get; } = [];

    /// <summary>网格内容高度（视图据此设置画布高度）。</summary>
    [ObservableProperty]
    private double _gridHeight = MinGridHeight;

    /// <summary>7 天区域的宽度（画布宽度）。随分栏数与控件宽度变化。</summary>
    public double DaysWidth => _daysWidth;

    private double _daysWidth = 900;

    /// <summary>本周是否一节课都没有（空状态提示）。</summary>
    [ObservableProperty]
    private bool _hasBlocks;

    /// <summary>学期第一周未配置时的提示。</summary>
    public bool ShowTermHint => !HasTermAnchor;

    partial void OnHasTermAnchorChanged(bool value) => OnPropertyChanged(nameof(ShowTermHint));

    partial void OnGridHeightChanged(double value) => OnPropertyChanged(nameof(DaysWidth));

    /// <summary>周次变化时请求重算（由 MainViewModel 提供数据）。</summary>
    public Action? RebuildRequested { get; set; }

    /// <summary>「今天」的日期（yyyy-MM-dd）；由调用方注入，便于离线自检固定日期。</summary>
    public string TodayDate { get; set; } = DateTime.Now.ToString("yyyy-MM-dd");

    /// <summary>本周是否真的有这门课（供课块初始化时判定）。</summary>
    internal bool IsCourseActiveInWeek(ScheduleItem item) => _activeItemIds.Contains(item.Id);

    private readonly HashSet<string> _activeItemIds = new(StringComparer.Ordinal);

    private int _lastMaxUsedPeriod;

    /// <summary>用指定周次的条目重建网格。</summary>
    public void Rebuild(
        IReadOnlyList<ScheduleItem> items,
        int weekNumber,
        string? termStartDate,
        IReadOnlyList<PeriodSlot> periods,
        bool darkTheme)
    {
        IsDarkTheme = darkTheme;

        var weekStart = Services.Timetable.WeekStartDate(termStartDate, weekNumber);
        HasTermAnchor = weekStart is not null;
        IsCurrentWeekDisplayed =
            Services.Timetable.ComputeWeekNumber(TodayDate, termStartDate) is { } todayWeek
            && todayWeek == weekNumber;

        WeekLabel = weekStart is { } s
            ? $"第 {weekNumber} 周 · {s:MM-dd} ~ {s.AddDays(6):MM-dd}"
            : $"第 {weekNumber} 周（未设置学期第一周，暂不显示日期）";

        // 本周真正要上的课（含单双周判定）——决定课块是否弱化。
        _activeItemIds.Clear();
        foreach (var item in Services.Timetable.FilterForWeek(items, weekNumber))
            _activeItemIds.Add(item.Id);

        // 候选条目：默认只要本周的；打开开关后把同学期的其它条目也纳入（弱化显示）。
        var candidates = items.Where(i => ShowOtherWeeks || _activeItemIds.Contains(i.Id)).ToList();

        // 记住当前选中的课块：重建（改窗口宽度、换周次）不该把详情面板关掉——
        // 实测踩过：GridWidth 的 setter 会触发重建，而重建原本无条件清空选中，
        // 于是窗口一有尺寸变化（首次布局就会有一次）详情面板就自己消失了。
        var selectedKey = SelectedBlock is { } previous ? BlockKey(previous) : null;

        Blocks.Clear();
        Periods.Clear();
        DayHeaders.Clear();

        BuildDayHeaders(weekStart, termStartDate);

        // 收集可画课块（剔除落在停用节次上的）。
        var drawable = new List<(ScheduleItem Item, int Start, int End)>();
        foreach (var item in candidates)
        {
            if (Services.Timetable.RangeIncludesDisabled(periods, item.StartPeriod, item.EndPeriod)) continue;
            drawable.Add((item, item.StartPeriod, item.EndPeriod));
        }

        var maxUsed = TimetableLayout.MaxPeriodOf(drawable.Select(d => (d.Start, d.End)));

        var dayCount = ShowWeekend ? 7 : 5;
        var rowCount = TimetableLayout.VisibleRowCount(
            maxUsed, periods.Count, p => p <= periods.Count && Services.Timetable.IsPeriodDisabled(periods[p - 1]));
        _lastMaxUsedPeriod = maxUsed;

        GridHeight = Math.Clamp(TimetableLayout.GridHeight(rowCount), MinGridHeight, MaxGridHeight);

        for (var period = 1; period <= rowCount; period++)
        {
            var slot = period <= periods.Count ? periods[period - 1] : null;
            var timeText = slot is null || Services.Timetable.IsPeriodDisabled(slot)
                ? string.Empty
                : $"{slot.StartTime}-{slot.EndTime}";
            Periods.Add(new TimetablePeriodViewModel(
                period, timeText, TimetableLayout.RowTop(period), TimetableLayout.PeriodRowHeight));
        }

        // 同一天 + 节次区间相交的课块分栏（同格多门课不再互相压住）。
        // 分栏数与天宽互相牵制：先算出「任一天最多几栏」，再据此定天宽，
        // 否则栏一多每栏就被压成读不出来的窄条（宁可让网格横向滚动）。
        var perDayColumns = new List<(int Day, int Columns)>();
        var perDaySpans = new List<(int Day, List<(int Start, int End)>)> ();
        foreach (var day in drawable.GroupBy(d => d.Item.Weekday).ToList())
        {
            var spans = day.Select(d => (d.Start, d.End)).ToList();
            var columns = TimetableLayout.AssignColumns(spans);
            var columnCount = columns.Length == 0 ? 1 : columns.Max(c => c.ColumnCount);
            perDayColumns.Add((day.Key, columnCount));
            perDaySpans.Add((day.Key, spans));
        }

        var maxColumns = perDayColumns.Count == 0 ? 1 : perDayColumns.Max(d => d.Columns);
        _daysWidth = TimetableLayout.RequiredDaysWidth(
            Math.Max(0, GridWidth - PeriodColumnWidth), dayCount, maxColumns);
        OnPropertyChanged(nameof(DaysWidth));

        var spanIndex = 0;
        foreach (var day in drawable.GroupBy(d => d.Item.Weekday).ToList())
        {
            var spans = perDaySpans[spanIndex].Item2;
            spanIndex++;
            var columns = TimetableLayout.AssignColumns(spans);
            var list = day.ToList();
            for (var i = 0; i < list.Count; i++)
            {
                var (item, start, end) = list[i];
                var (column, columnCount) = columns[i];
                var rect = TimetableLayout.Measure(
                    item.Weekday, start, end, DaysWidth, column, columnCount, dayCount);
                Blocks.Add(new TimetableBlockViewModel(item, this, rect));
            }
        }

        // 本周真实要上的课数（不含淡化显示的其它周次课）。
        BlockCount = Blocks.Count(b => b.IsCurrentWeek);
        HasBlocks = BlockCount > 0;
        NextClassText = BuildNextClassText(Blocks, weekStart, weekNumber);

        // 恢复选中（课块是新实例，按稳定键找回）；这一周没有它了就清掉。
        SelectedBlock = selectedKey is null
            ? null
            : Blocks.FirstOrDefault(b => BlockKey(b) == selectedKey);
    }

    /// <summary>
    /// 课块的稳定标识：条目 Id + 星期 + 起始节次。
    /// <para>
    /// 不能直接用课块对象——每次重建都会 new 一批新实例；也不能只用条目 Id，
    /// 因为同一门课可能有多条（不同周次段）落在同一格。
    /// </para>
    /// </summary>
    private static string BlockKey(TimetableBlockViewModel block) =>
        $"{block.Model.Id}|{block.Model.Weekday}|{block.Model.StartPeriod}";

    /// <summary>重建列表头（星期 + 日期 + 今天）。</summary>
    private void BuildDayHeaders(DateOnly? weekStart, string? termStartDate)
    {
        var today = Services.Timetable.ParseDate(TodayDate);
        var count = ShowWeekend ? 7 : 5;

        for (var i = 0; i < count; i++)
        {
            var label = Services.Timetable.WeekdayLabels[i];
            var dateText = string.Empty;
            var isToday = false;

            if (weekStart is { } start)
            {
                var date = start.AddDays(i);
                dateText = date.ToString("MM-dd");
                isToday = IsCurrentWeekDisplayed && today is { } t && t == date;
            }

            DayHeaders.Add(new TimetableDayHeaderViewModel(i + 1, label, dateText, isToday));
        }

        _ = termStartDate;
    }

    /// <summary>
    /// 「下一节课」文案：只在**显示的就是今天所在的周**时才有意义。
    /// 取今天尚未开始的最近一节课；今天已无课则取明天的第一节。
    /// </summary>
    private string BuildNextClassText(IReadOnlyList<TimetableBlockViewModel> blocks, DateOnly? weekStart, int weekNumber)
    {
        if (weekStart is null || !IsCurrentWeekDisplayed) return string.Empty;
        var today = Services.Timetable.ParseDate(TodayDate);
        if (today is null) return string.Empty;

        var todayWeekday = ((int)today.Value.DayOfWeek + 6) % 7 + 1;
        var now = DateTime.Now.ToString("HH:mm");

        var upcoming = blocks
            .Where(b => b.IsCurrentWeek && b.Model.Weekday == todayWeekday
                        && string.CompareOrdinal(b.Model.StartTime, now) > 0)
            .OrderBy(b => b.Model.StartTime, StringComparer.Ordinal)
            .FirstOrDefault();

        if (upcoming is not null)
            return $"下一节：{upcoming.Name} · {upcoming.Model.StartTime}"
                   + (upcoming.Location is { Length: > 0 } loc ? $" · {loc}" : string.Empty);

        // 今天没有后续课程 → 报明天的第一节（若明天在本周内）。
        if (todayWeekday >= 7) return string.Empty;
        var tomorrow = todayWeekday + 1;
        var first = blocks
            .Where(b => b.IsCurrentWeek && b.Model.Weekday == tomorrow)
            .OrderBy(b => b.Model.StartTime, StringComparer.Ordinal)
            .FirstOrDefault();

        return first is null
            ? string.Empty
            : $"明天第一节：{first.Name} · {first.Model.StartTime}"
              + (first.Location is { Length: > 0 } loc2 ? $" · {loc2}" : string.Empty);
    }

    /// <summary>供自检读取：网格用了多少节次行。</summary>
    public int RowCount => Periods.Count;

    /// <summary>供自检读取：本周实际有课的节次上限。</summary>
    public int MaxUsedPeriod => _lastMaxUsedPeriod;
}
