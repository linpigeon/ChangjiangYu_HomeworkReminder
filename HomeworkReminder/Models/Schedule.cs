using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace HomeworkReminder.Models;

/// <summary>
/// 单双周。与教务系统「1-15周(单)」这类表述对应。
/// </summary>
public enum WeekParity
{
    /// <summary>每周都上。</summary>
    Every = 0,

    /// <summary>单周（第 1、3、5… 周）。</summary>
    Odd = 1,

    /// <summary>双周（第 2、4、6… 周）。</summary>
    Even = 2,
}

/// <summary>
/// 一条课表条目：**一个「星期 + 节次 + 连续周次段」的上课安排**。
/// <para>
/// 为什么不是「一门课一条」：教务系统的原始记录常带不连续周次
/// （如 <c>1-8,10-16周</c>）或单双周。这里按**连续段拆开**，
/// 每条只表示一段连续周次，让「某周是否有课」变成一次简单的区间判断，
/// 周视图与提醒都不必再做区间合并。
/// </para>
/// </summary>
public sealed class ScheduleItem
{
    /// <summary>稳定 Id（导入时用于匹配既有条目）。</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>课程名。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>任课教师（可空）。</summary>
    public string? Teacher { get; set; }

    /// <summary>上课地点（可空）。</summary>
    public string? Location { get; set; }

    /// <summary>星期，1 = 周一 … 7 = 周日。</summary>
    public int Weekday { get; set; }

    /// <summary>起始节次。</summary>
    public int StartPeriod { get; set; }

    /// <summary>结束节次。</summary>
    public int EndPeriod { get; set; }

    /// <summary>起始周（1 基）。</summary>
    public int StartWeek { get; set; }

    /// <summary>结束周（1 基，含）。</summary>
    public int EndWeek { get; set; }

    /// <summary>单双周。</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<WeekParity>))]
    public WeekParity WeekParity { get; set; } = WeekParity.Every;

    /// <summary>上课开始时间（HH:mm）。由节次时间表推导，见 <see cref="Services.Timetable.ResolveSessionTime"/>。</summary>
    public string StartTime { get; set; } = "08:00";

    /// <summary>上课结束时间（HH:mm）。</summary>
    public string EndTime { get; set; } = "09:40";

    /// <summary>学期标识（与 AppSettings.Term 一致）。</summary>
    public int Term { get; set; }
}

/// <summary>单个节次的时间映射。</summary>
public sealed class PeriodSlot
{
    /// <summary>节次序号（从 1 起、连续）。</summary>
    public int Period { get; set; }

    /// <summary>开始时间（HH:mm）。</summary>
    public string StartTime { get; set; } = "08:00";

    /// <summary>结束时间（HH:mm）。</summary>
    public string EndTime { get; set; } = "08:45";
}

/// <summary>
/// 某一天要上的课（由 <see cref="ScheduleItem"/> 按周次展开而来）。
/// 周视图与「今日课程」都用它，避免各自再算一遍周次。
/// </summary>
public sealed class ClassSession
{
    /// <summary>展开后的唯一标识：<c>{条目Id}:{yyyy-MM-dd}</c>。</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>来源条目 Id。</summary>
    public string ScheduleItemId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string? Location { get; set; }

    /// <summary>上课日期（yyyy-MM-dd）。</summary>
    public string Date { get; set; } = string.Empty;

    public string StartTime { get; set; } = "08:00";

    public string EndTime { get; set; } = "09:40";
}
