using System;
using System.Collections.Generic;
using System.Linq;
using HomeworkReminder.Models;

namespace HomeworkReminder.Services;

/// <summary>
/// 课表纯逻辑：节次时间表、单双周、周次换算、按周展开为具体日期的课次。
/// <para>
/// 刻意不含任何 I/O 与网络：这部分是最容易算错也最值得单测的地方，
/// 全部逻辑都能在 <c>--jsoncheck</c> 里离线验证。
/// </para>
/// </summary>
public static class Timetable
{
    /// <summary>节次数量上限（与教务系统常见的一天最多节数一致）。</summary>
    public const int MaxPeriod = 20;

    /// <summary>周次上限。</summary>
    public const int MaxWeek = 30;

    /// <summary>
    /// 停用哨兵：某节开始与结束时间都是 <c>00:00</c> 即表示该节停用。
    /// <para>
    /// 不参与先后/相邻重叠校验，周视图不显示该节行，落在其上的课块也不显示。
    /// 这样学校作息调整（例如某天只有 11 节）不必删节次，改时间即可。
    /// </para>
    /// </summary>
    public const string DisabledPeriodTime = "00:00";

    /// <summary>列头星期文案（周一 = 第 1 列，与 <see cref="ScheduleItem.Weekday"/> 一致）。</summary>
    public static readonly string[] WeekdayLabels = ["周一", "周二", "周三", "周四", "周五", "周六", "周日"];

    /// <summary>
    /// 内置默认节次时间表：一天 12 节，上午 4 节（08:00 起）+ 下午 4 节（14:00 起）
    /// + 晚上 4 节（18:30 起），课间 10~20 分钟。
    /// <para>
    /// 教务系统的课表只给「第几节」，具体几点由这张表推导，
    /// 因此周视图、待办、提醒三处的时间口径天然一致。
    /// </para>
    /// </summary>
    public static IReadOnlyList<PeriodSlot> DefaultPeriods { get; } =
    [
        new() { Period = 1, StartTime = "08:00", EndTime = "08:45" },
        new() { Period = 2, StartTime = "08:55", EndTime = "09:40" },
        new() { Period = 3, StartTime = "10:00", EndTime = "10:45" },
        new() { Period = 4, StartTime = "10:55", EndTime = "11:40" },
        new() { Period = 5, StartTime = "14:00", EndTime = "14:45" },
        new() { Period = 6, StartTime = "14:55", EndTime = "15:40" },
        new() { Period = 7, StartTime = "16:00", EndTime = "16:45" },
        new() { Period = 8, StartTime = "16:55", EndTime = "17:40" },
        new() { Period = 9, StartTime = "18:30", EndTime = "19:15" },
        new() { Period = 10, StartTime = "19:25", EndTime = "20:10" },
        new() { Period = 11, StartTime = "20:20", EndTime = "21:05" },
        new() { Period = 12, StartTime = "21:15", EndTime = "22:00" },
    ];

    /// <summary>默认节次表的副本（避免调用方改到共享实例）。</summary>
    public static List<PeriodSlot> CreateDefaultPeriods() =>
        DefaultPeriods.Select(s => new PeriodSlot { Period = s.Period, StartTime = s.StartTime, EndTime = s.EndTime }).ToList();

    /// <summary>时间格式：<c>HH:mm</c>（24 小时制）。</summary>
    public static bool IsValidTime(string? value) =>
        value is { Length: 5 } && value[2] == ':'
        && value[0] is >= '0' and <= '2' && char.IsAsciiDigit(value[1])
        && char.IsAsciiDigit(value[3]) && char.IsAsciiDigit(value[4])
        && string.CompareOrdinal(value, "24:00") < 0;

    /// <summary>该节是否停用（开始与结束均为 <c>00:00</c>）。</summary>
    public static bool IsPeriodDisabled(PeriodSlot slot) =>
        slot.StartTime == DisabledPeriodTime && slot.EndTime == DisabledPeriodTime;

    /// <summary>单双周判定。</summary>
    public static bool ParityMatches(int week, WeekParity parity) => parity switch
    {
        WeekParity.Every => true,
        WeekParity.Odd => week % 2 == 1,
        WeekParity.Even => week % 2 == 0,
        _ => true,
    };

    /// <summary>
    /// 取第 N 节的时间；越界或该节停用时返回 null。
    /// </summary>
    public static (string StartTime, string EndTime)? ResolvePeriodTime(IReadOnlyList<PeriodSlot> periods, int period)
    {
        if (period < 1 || period > periods.Count) return null;
        var slot = periods[period - 1];
        if (IsPeriodDisabled(slot)) return null;
        return (slot.StartTime, slot.EndTime);
    }

    /// <summary>
    /// 推导条目的实际上下课时间。
    /// <para>
    /// 全部引用的节次都在时间表内 → 取首节开始 + 末节结束；
    /// 任一节越界（例如学校有 13 节而表里只有 12 节）→ **回退条目自带的时间**
    /// 并标记 <paramref name="outOfRange"/>，而不是静默显示错误时间。
    /// </para>
    /// </summary>
    public static (string StartTime, string EndTime, bool OutOfRange) ResolveSessionTime(
        ScheduleItem item, IReadOnlyList<PeriodSlot> periods)
    {
        var start = ResolvePeriodTime(periods, item.StartPeriod);
        var end = ResolvePeriodTime(periods, item.EndPeriod);
        if (start is { } s && end is { } e && item.StartPeriod <= item.EndPeriod)
            return (s.StartTime, e.EndTime, false);

        return (item.StartTime, item.EndTime, true);
    }

    /// <summary>
    /// 校验节次时间表。非法时抛出带中文说明的异常（而不是静默接受）。
    /// <para>规则：序号从 1 起且连续、时间格式合法、单节开始早于结束、相邻节次不重叠。</para>
    /// </summary>
    public static void ValidatePeriods(IReadOnlyList<PeriodSlot> periods, int maxPeriod = MaxPeriod)
    {
        if (periods.Count == 0) throw new ArgumentException("节次时间表不能为空");
        if (periods.Count > maxPeriod) throw new ArgumentException($"节次数量不能超过 {maxPeriod} 节");

        PeriodSlot? lastActive = null;
        for (var i = 0; i < periods.Count; i++)
        {
            var slot = periods[i];
            if (slot.Period != i + 1) throw new ArgumentException("节次序号必须从 1 起且连续");
            if (!IsValidTime(slot.StartTime) || !IsValidTime(slot.EndTime))
                throw new ArgumentException($"第 {slot.Period} 节时间格式需为 HH:mm");

            if (IsPeriodDisabled(slot)) continue;

            if (string.CompareOrdinal(slot.StartTime, slot.EndTime) >= 0)
                throw new ArgumentException($"第 {slot.Period} 节开始时间需早于结束时间");

            if (lastActive is { } prev && string.CompareOrdinal(slot.StartTime, prev.EndTime) < 0)
                throw new ArgumentException($"第 {prev.Period} 节与第 {slot.Period} 节时间重叠");

            lastActive = slot;
        }
    }

    /// <summary>规整节次时间表：非法或为空时整体回退默认值（宁可回默认，不可展示错乱时间）。</summary>
    public static List<PeriodSlot> NormalizePeriods(IReadOnlyList<PeriodSlot>? raw)
    {
        if (raw is null || raw.Count == 0 || raw.Count > MaxPeriod) return CreateDefaultPeriods();
        try
        {
            var sorted = raw.OrderBy(s => s.Period).ToList();
            ValidatePeriods(sorted);
            return sorted;
        }
        catch (ArgumentException)
        {
            return CreateDefaultPeriods();
        }
    }

    // ---------------- 周次与日期 ----------------

    /// <summary>解析 <c>yyyy-MM-dd</c>；非法返回 null。</summary>
    public static DateOnly? ParseDate(string? value) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", out var d) ? d : null;

    /// <summary>规整到所在周的周一（周日归上一周）。</summary>
    public static DateOnly MondayOf(DateOnly date) =>
        date.AddDays(-(((int)date.DayOfWeek + 6) % 7));

    /// <summary>
    /// 计算某天是学期第几周（1 基）。无有效基准或日期非法时返回 null；
    /// 早于第 1 周按第 1 周处理。
    /// </summary>
    public static int? ComputeWeekNumber(string? date, string? termStartDate)
    {
        var today = ParseDate(date);
        var term = ParseDate(termStartDate);
        if (today is null || term is null) return null;

        var days = MondayOf(today.Value).DayNumber - MondayOf(term.Value).DayNumber;
        return Math.Max(1, days / 7 + 1);
    }

    /// <summary>第 N 周的周一日期；基准非法返回 null。</summary>
    public static DateOnly? WeekStartDate(string? termStartDate, int weekNumber)
    {
        var term = ParseDate(termStartDate);
        if (term is null || weekNumber < 1) return null;
        return MondayOf(term.Value).AddDays((weekNumber - 1) * 7);
    }

    /// <summary>从某周周一展开连续若干天的日期（默认 7 天），用于表头。</summary>
    public static List<string> WeekDates(string? mondayIso, int count = 7)
    {
        var monday = ParseDate(mondayIso);
        if (monday is null) return [];
        var start = MondayOf(monday.Value);
        return Enumerable.Range(0, count).Select(i => start.AddDays(i).ToString("yyyy-MM-dd")).ToList();
    }

    /// <summary>
    /// 按周次 / 单双周 / 学期过滤条目。
    /// </summary>
    public static List<ScheduleItem> FilterForWeek(IEnumerable<ScheduleItem> items, int weekNumber, int? term = null) =>
        items.Where(i =>
            (term is null || i.Term == term)
            && weekNumber >= i.StartWeek
            && weekNumber <= i.EndWeek
            && ParityMatches(weekNumber, i.WeekParity)).ToList();

    /// <summary>
    /// 把条目展开为具体日期的课次（按起止周与单双周逐周展开，再按日期过滤）。
    /// </summary>
    /// <param name="items">条目。</param>
    /// <param name="termStartDate">学期第一周周一（yyyy-MM-dd）；非法则返回空。</param>
    /// <param name="periods">节次时间表（用于推导实际时间）。</param>
    /// <param name="fromDate">起始日期（含）；null = 今天。</param>
    /// <param name="days">展开天数。</param>
    /// <param name="term">学期过滤；null = 不过滤。</param>
    public static List<ClassSession> Expand(
        IEnumerable<ScheduleItem> items,
        string? termStartDate,
        IReadOnlyList<PeriodSlot> periods,
        string? fromDate = null,
        int days = 14,
        int? term = null)
    {
        var termStart = ParseDate(termStartDate);
        if (termStart is null) return [];

        var monday = MondayOf(termStart.Value);
        var from = ParseDate(fromDate) ?? DateOnly.FromDateTime(DateTime.Now);
        var until = from.AddDays(days);

        var sessions = new List<ClassSession>();
        foreach (var item in term is null ? items : items.Where(i => i.Term == term))
        {
            var time = ResolveSessionTime(item, periods);
            for (var week = item.StartWeek; week <= item.EndWeek; week++)
            {
                if (!ParityMatches(week, item.WeekParity)) continue;

                var date = monday.AddDays((week - 1) * 7 + (item.Weekday - 1));
                if (date < from || date >= until) continue;

                sessions.Add(new ClassSession
                {
                    Id = $"{item.Id}:{date:yyyy-MM-dd}",
                    ScheduleItemId = item.Id,
                    Name = item.Name,
                    Location = item.Location,
                    Date = date.ToString("yyyy-MM-dd"),
                    StartTime = time.StartTime,
                    EndTime = time.EndTime,
                });
            }
        }
        return sessions
            .OrderBy(s => s.Date, StringComparer.Ordinal)
            .ThenBy(s => s.StartTime, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>该校次是否落在已停用的节次上（用于周视图隐藏）。</summary>
    public static bool RangeIncludesDisabled(IReadOnlyList<PeriodSlot> periods, int startPeriod, int endPeriod)
    {
        var from = Math.Min(startPeriod, endPeriod);
        var to = Math.Max(startPeriod, endPeriod);
        for (var p = from; p <= to; p++)
        {
            if (p < 1 || p > periods.Count) continue;
            if (IsPeriodDisabled(periods[p - 1])) return true;
        }
        return false;
    }

    // ---------------- 上课提醒 ----------------

    /// <summary>该发提醒的一节课。</summary>
    /// <param name="Session">课次。</param>
    /// <param name="MinutesUntil">距上课还有多少分钟（向上取整，最小 1）。</param>
    public readonly record struct ClassReminder(ClassSession Session, int MinutesUntil);

    /// <summary>
    /// 找出「现在该提醒」的课次。**纯函数**，不碰时间也不碰通知，便于离线断言。
    /// <para>
    /// 规则（三条缺一不可）：
    /// </para>
    /// <list type="number">
    /// <item>还没提醒过（<paramref name="alreadyNotified"/> 里没有它）——否则每分钟都会重复提醒一次；</item>
    /// <item>还没开始上课——已开始的课再提醒没有意义，也避免「打开应用就被补一条过时提醒」；</item>
    /// <item>距上课时间不超过提前量——提前量设为 15 分钟，就不该在提前 40 分钟时提醒。</item>
    /// </list>
    /// <para>
    /// <paramref name="minutesBefore"/> 为 0 时按「关闭提醒」处理，直接返回空。
    /// </para>
    /// </summary>
    /// <param name="sessions">候选课次（通常只传今天的）。</param>
    /// <param name="now">当前时间。</param>
    /// <param name="minutesBefore">提前多少分钟提醒；0 = 不提醒。</param>
    /// <param name="alreadyNotified">已经提醒过的课次 Id。</param>
    public static List<ClassReminder> DueReminders(
        IEnumerable<ClassSession> sessions,
        DateTime now,
        int minutesBefore,
        IReadOnlySet<string> alreadyNotified)
    {
        var result = new List<ClassReminder>();
        if (minutesBefore <= 0) return result;

        var today = now.ToString("yyyy-MM-dd");
        foreach (var session in sessions)
        {
            // 只认今天：跨夜的候选（例如次日凌晨的课）不该在今晚被提醒。
            if (!string.Equals(session.Date, today, StringComparison.Ordinal)) continue;
            if (alreadyNotified.Contains(session.Id)) continue;

            if (!TryParseClock(session.StartTime, out var start)) continue;

            var until = start - now.TimeOfDay;
            // 已经上课了（或正好在上课）→ 不补提醒。
            if (until <= TimeSpan.Zero) continue;
            // 还早得很 → 等进入提前量窗口再说。
            if (until > TimeSpan.FromMinutes(minutesBefore)) continue;

            var minutes = (int)Math.Ceiling(until.TotalMinutes);
            result.Add(new ClassReminder(session, Math.Max(1, minutes)));
        }

        return result.OrderBy(r => r.Session.StartTime, StringComparer.Ordinal).ToList();
    }

    /// <summary>把 <c>HH:mm</c> 解析成一天内的偏移；格式非法返回 false。</summary>
    private static bool TryParseClock(string? value, out TimeSpan time)
    {
        time = default;
        if (!IsValidTime(value)) return false;
        time = new TimeSpan(int.Parse(value!.AsSpan(0, 2)), int.Parse(value.AsSpan(3, 2)), 0);
        return true;
    }

    /// <summary>
    /// 把一节课组装成通知内容。
    /// <para>与实际发送分离，便于离线自检校验「课次 → 通知文案」的映射。</para>
    /// </summary>
    public static AppNotification BuildReminderNotification(ClassSession session, int minutesUntil)
    {
        var where = string.IsNullOrWhiteSpace(session.Location) ? string.Empty : $" · {session.Location}";
        return new AppNotification(
            Title: session.Name,
            Body: $"{session.StartTime}-{session.EndTime}{where}",
            Attribution: minutesUntil <= 1 ? "马上上课" : $"{minutesUntil} 分钟后上课");
    }
}
