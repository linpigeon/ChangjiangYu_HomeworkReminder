using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using HomeworkReminder.Models;

namespace HomeworkReminder.Services;

/// <summary>课表文件（schedule.json）的整体结构。</summary>
public sealed class ScheduleFile
{
    public List<ScheduleItem> Items { get; set; } = [];

    /// <summary>节次时间表。为空/非法时整体回退 <see cref="Timetable.DefaultPeriods"/>。</summary>
    public List<PeriodSlot> Periods { get; set; } = [];
}

/// <summary>教务系统配置（jw-profile.json）。<b>只存网址，不存任何凭据</b>。</summary>
public sealed class JwProfile
{
    /// <summary>学校教务系统登录页网址（https）。</summary>
    public string LoginUrl { get; set; } = string.Empty;

    /// <summary>最近一次抓取时间（仅用于界面提示）。</summary>
    public DateTimeOffset? LastFetchAt { get; set; }

    /// <summary>最近一次抓取结果摘要（如「新增 12、更新 3、跳过 20」）。</summary>
    public string? LastFetchSummary { get; set; }
}

public interface IScheduleStore
{
    Task<ScheduleFile> LoadAsync(CancellationToken ct = default);
    Task SaveAsync(ScheduleFile file, CancellationToken ct = default);
}

/// <summary>
/// 课表持久化（<c>schedule.json</c>）。
/// <para>
/// 读取时对条目做合法性校验并丢弃坏数据——教务系统导入的条目会经手多个环节，
/// 一条坏数据不该让整张课表打不开。节次时间表非法则整体回退默认值。
/// </para>
/// </summary>
public sealed class ScheduleStore : IScheduleStore
{
    private readonly string _path;

    public ScheduleStore(string? directory = null)
    {
        var dir = directory ?? AppPaths.DataDirectory;
        _path = Path.Combine(dir, "schedule.json");
    }

    public string Location => _path;

    public async Task<ScheduleFile> LoadAsync(CancellationToken ct = default)
    {
        if (!File.Exists(_path)) return NewFile();
        try
        {
            var text = await File.ReadAllTextAsync(_path, ct).ConfigureAwait(false);
            var file = JsonSerializer.Deserialize(text, AppJsonContext.Default.ScheduleFile);
            if (file is null) return NewFile();

            file.Items = Validate(file.Items);
            file.Periods = Timetable.NormalizePeriods(file.Periods);
            return file;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // 损坏的课表文件不该让应用起不来；返回空课表而不是抛异常。
            return NewFile();
        }
    }

    public async Task SaveAsync(ScheduleFile file, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var json = JsonSerializer.Serialize(file, AppJsonContext.Default.ScheduleFile);

        // 先写临时文件再原子替换：进程中断不会留下半截 JSON（与其它 store 同口径）。
        var temp = _path + ".tmp";
        await File.WriteAllTextAsync(temp, json, ct).ConfigureAwait(false);
        File.Move(temp, _path, overwrite: true);
    }

    private static ScheduleFile NewFile() => new() { Items = [], Periods = Timetable.CreateDefaultPeriods() };

    /// <summary>丢弃不合法条目（缺课程名、星期/节次/周次越界、时间格式错）。</summary>
    public static List<ScheduleItem> Validate(IEnumerable<ScheduleItem>? items)
    {
        var valid = new List<ScheduleItem>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var item in items ?? [])
        {
            if (!IsValid(item)) continue;

            // 无 Id 的条目补一个稳定 Id，否则后续无法匹配更新。
            if (string.IsNullOrWhiteSpace(item.Id))
                item.Id = Guid.NewGuid().ToString("N");

            if (!seen.Add(item.Id)) continue;
            valid.Add(item);
        }
        return valid;
    }

    /// <summary>
    /// 单条合法性判定。导入路径用它**整批先校验**：
    /// 任一条不合法就一条都不写，避免留下「导入了一半」的课表。
    /// </summary>
    public static bool IsValid(ScheduleItem? item)
    {
        if (item is null) return false;
        if (string.IsNullOrWhiteSpace(item.Name)) return false;
        if (item.Weekday is < 1 or > 7) return false;
        if (item.StartPeriod < 1 || item.EndPeriod > Timetable.MaxPeriod || item.StartPeriod > item.EndPeriod) return false;
        if (item.StartWeek < 1 || item.EndWeek > Timetable.MaxWeek || item.StartWeek > item.EndWeek) return false;
        if (!Timetable.IsValidTime(item.StartTime) || !Timetable.IsValidTime(item.EndTime)) return false;
        return true;
    }

    /// <summary>单条校验，不合法时抛出带中文说明的异常（导入路径用）。</summary>
    public static void ValidateOne(ScheduleItem? item)
    {
        if (item is null) throw new ArgumentException("课表条目为空");
        if (string.IsNullOrWhiteSpace(item.Name)) throw new ArgumentException("课程名不能为空");
        if (item.Weekday is < 1 or > 7) throw new ArgumentException($"「{item.Name}」星期需在 1-7 之间");
        if (item.StartPeriod < 1 || item.EndPeriod > Timetable.MaxPeriod || item.StartPeriod > item.EndPeriod)
            throw new ArgumentException($"「{item.Name}」节次范围无效");
        if (item.StartWeek < 1 || item.EndWeek > Timetable.MaxWeek || item.StartWeek > item.EndWeek)
            throw new ArgumentException($"「{item.Name}」周次范围无效");
        if (!Timetable.IsValidTime(item.StartTime) || !Timetable.IsValidTime(item.EndTime))
            throw new ArgumentException($"「{item.Name}」上课时间格式需为 HH:mm");
    }
}

/// <summary>教务配置的读写（<c>jw-profile.json</c>）。</summary>
public sealed class JwProfileStore
{
    private readonly string _path;

    public JwProfileStore(string? directory = null)
    {
        var dir = directory ?? AppPaths.DataDirectory;
        _path = Path.Combine(dir, "jw-profile.json");
    }

    public string Location => _path;

    public async Task<JwProfile> LoadAsync(CancellationToken ct = default)
    {
        if (!File.Exists(_path)) return new JwProfile();
        try
        {
            var text = await File.ReadAllTextAsync(_path, ct).ConfigureAwait(false);
            return JsonSerializer.Deserialize(text, AppJsonContext.Default.JwProfile) ?? new JwProfile();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new JwProfile();
        }
    }

    public async Task SaveAsync(JwProfile profile, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var json = JsonSerializer.Serialize(profile, AppJsonContext.Default.JwProfile);
        var temp = _path + ".tmp";
        await File.WriteAllTextAsync(temp, json, ct).ConfigureAwait(false);
        File.Move(temp, _path, overwrite: true);
    }
}
