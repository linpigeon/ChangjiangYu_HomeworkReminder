using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using HomeworkReminder.Models;

namespace HomeworkReminder.Services;

/// <summary>
/// 待办事项的持久化缓存（todos.json）。
/// <para>
/// 两个用途：一是启动时先把上次同步的列表填上，首屏不用等网络；
/// 二是给增量同步提供「上次每门课的签名 + 作业条目」，签名未变的课程
/// 可以跳过章节树与逐条详情请求（见 <see cref="SyncService"/>）。
/// </para>
/// </summary>
public sealed class TodoCacheFile
{
    public DateTimeOffset SyncedAt { get; set; }

    public List<TodoItem> Homework { get; set; } = [];

    /// <summary>
    /// 学习通（超星）条目。与 <see cref="Homework"/> 分开存放：它们的截止时间是
    /// 「同步时刻 + 相对剩余时间」，旧缓存里没有 <c>RemainingAtSync</c> 时只能按
    /// <see cref="SyncedAt"/> 粗略还原，分开存便于按来源区分处理与排错。
    /// </summary>
    public List<TodoItem> Chaoxing { get; set; } = [];

    /// <summary>
    /// 批改网（pigai.org）条目。同样与 <see cref="Homework"/> 分开存放，
    /// 便于按来源处理与排错；它的截止时间是绝对时间，无相对量还原问题。
    /// </summary>
    public List<TodoItem> Pigai { get; set; } = [];

    /// <summary>
    /// 学习通课程维度的未完成任务点汇总（不是待办条目）。
    /// </summary>
    public List<ChaoxingCourse> ChaoxingCourses { get; set; } = [];

    public List<TodoItem> Announcements { get; set; } = [];

    /// <summary>每门课（classroomId）上次同步时的作业日志签名，用于增量比对。</summary>
    public Dictionary<long, string> CourseSignatures { get; set; } = [];

    /// <summary>
    /// 每门课最近一次全量校验（章节树+逐条详情）的时间。只在全量路径刷新；
    /// 缺该字段的旧缓存在下次同步时自动全量校验。
    /// </summary>
    public Dictionary<long, DateTimeOffset> CourseFullSyncedAt { get; set; } = [];

    public int CourseCount { get; set; }

    public int ActivityCount { get; set; }

    public int UnreadNotificationCount { get; set; }
}

public interface ITodoCacheStore
{
    Task<TodoCacheFile?> LoadAsync(CancellationToken ct = default);
    Task SaveAsync(TodoCacheFile cache, CancellationToken ct = default);
    Task ClearAsync(CancellationToken ct = default);
}

/// <inheritdoc />
public sealed class TodoCacheStore : ITodoCacheStore
{
    /// <summary>与 SyncService 一致的时间脏数据区间。</summary>
    private static readonly DateTimeOffset MinPlausible = new(2015, 1, 1, 0, 0, 0, TimeSpan.FromHours(8));
    private static readonly DateTimeOffset MaxPlausible = new(2100, 1, 1, 0, 0, 0, TimeSpan.FromHours(8));

    private readonly string _path;

    public TodoCacheStore(string? directory = null)
    {
        var dir = directory ?? AppPaths.DataDirectory;
        _path = Path.Combine(dir, "todos.json");
    }

    public async Task<TodoCacheFile?> LoadAsync(CancellationToken ct = default)
    {
        if (!File.Exists(_path)) return null;
        try
        {
            var text = await File.ReadAllTextAsync(_path, ct).ConfigureAwait(false);
            var cache = JsonSerializer.Deserialize(text, AppJsonContext.Default.TodoCacheFile);
            if (cache is null) return null;

            // 合法性校验：坏条目（缺 id/标题、id 前缀与类型不符、时间戳离谱、重复 id）
            // 一律丢弃——缓存文件可能被手改、被旧版本写坏，不能让脏数据进列表。
            cache.Homework = Validate(cache.Homework, TodoKind.Homework);
            cache.Chaoxing = Validate(cache.Chaoxing, TodoKind.Homework, cache.SyncedAt);
            cache.Pigai = Validate(cache.Pigai, TodoKind.Homework);
            cache.Announcements = Validate(cache.Announcements, TodoKind.Announcement);
            cache.ChaoxingCourses = ValidateCourses(cache.ChaoxingCourses);
            return cache;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public async Task SaveAsync(TodoCacheFile cache, CancellationToken ct = default)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            // 先写临时文件再替换：写到一半被杀/断电不会留下半截 JSON。
            var tmp = _path + ".tmp";
            await File.WriteAllTextAsync(
                tmp,
                JsonSerializer.Serialize(cache, AppJsonContext.Default.TodoCacheFile),
                ct).ConfigureAwait(false);
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 缓存写不进去不影响本次会话：下次启动只是退回全量同步。
        }
    }

    public Task ClearAsync(CancellationToken ct = default)
    {
        try
        {
            if (File.Exists(_path)) File.Delete(_path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 同上：删不掉最多下次启动看到旧缓存，同步后会被覆盖。
        }
        return Task.CompletedTask;
    }

    private static List<TodoItem> Validate(
        List<TodoItem> items, TodoKind kind, DateTimeOffset? anchorFallback = null)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var valid = new List<TodoItem>(items.Count);
        foreach (var item in items)
        {
            if (string.IsNullOrWhiteSpace(item.Id) || string.IsNullOrWhiteSpace(item.Title)) continue;
            if (item.Kind != kind) continue;
            var prefix = kind == TodoKind.Homework ? "hw-" : "ann-";
            if (!item.Id.StartsWith(prefix, StringComparison.Ordinal)) continue;

            // 相对时间形态（学习通）：只给了基准 + 剩余量时，截止时间由两者推算，
            // 此时 DueAt 本就为空、基准时刻才是需要校验的那个时间戳。
            if (item.DueAt is null && item.RemainingAtSync is { } left)
            {
                if (item.DeadlineAnchor is { } storedAnchor)
                {
                    // 基准已经存在：它必须落在合理区间，否则这条记录的相对量无从还原。
                    if (!PlausibleAnchor(storedAnchor)) continue;
                }
                else
                {
                    // 旧缓存（本改动之前写入的）没有基准字段：用缓存自身的同步时刻补上，
                    // 宁可让倒计时偏差一次，也好过把这条作业整个丢掉。
                    if (anchorFallback is not { } fb || !PlausibleAnchor(fb)) continue;
                    item.Reanchor(fb);
                }

                if (!seen.Add(item.Id)) continue;
                valid.Add(item);
                continue;
            }

            if (!Plausible(item.DueAt) || !Plausible(item.PublishedAt)) continue;
            if (!seen.Add(item.Id)) continue;
            valid.Add(item);
        }
        return valid;
    }

    private static bool Plausible(DateTimeOffset? t)
        => t is null || (t >= MinPlausible && t <= MaxPlausible);

    /// <summary>
    /// 课程汇总的合法性校验：丢弃没有课程名或 id 不成立的条目。
    /// 任务点数按「不明则为 0」处理，不因为一个数字离谱就丢掉整门课。
    /// </summary>
    private static List<ChaoxingCourse> ValidateCourses(List<ChaoxingCourse> courses)
    {
        var seen = new HashSet<long>();
        var valid = new List<ChaoxingCourse>(courses.Count);
        foreach (var c in courses)
        {
            if (string.IsNullOrWhiteSpace(c.CourseName)) continue;
            if (c.CourseId <= 0 || c.ClazzId <= 0) continue;
            if (!seen.Add(c.CourseId)) continue;
            if (c.PendingTaskPoints < 0) c.PendingTaskPoints = 0;
            // 明细里丢掉明显无效的行（没有名字、计数非正）。
            c.Leaves = c.Leaves
                .Where(l => !string.IsNullOrWhiteSpace(l.Name) && l.PendingCount > 0)
                .ToList();
            valid.Add(c);
        }
        return valid;
    }

    /// <summary>
    /// 基准时刻的合法性：既不能离谱地早于/晚于合理区间，也不能晚于现在太多
    /// （基准在未来意味着相对剩余量会被算成负数以外的怪值）。
    /// </summary>
    private static bool PlausibleAnchor(DateTimeOffset anchor)
        => anchor >= MinPlausible && anchor <= MaxPlausible && anchor <= DateTimeOffset.Now.AddDays(1);
}
