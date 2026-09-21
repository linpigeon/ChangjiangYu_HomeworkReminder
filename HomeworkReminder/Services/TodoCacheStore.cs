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

    public List<TodoItem> Announcements { get; set; } = [];

    /// <summary>每门课（classroomId）上次同步时的作业日志签名，用于增量比对。</summary>
    public Dictionary<long, string> CourseSignatures { get; set; } = [];

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
            cache.Announcements = Validate(cache.Announcements, TodoKind.Announcement);
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

    private static List<TodoItem> Validate(List<TodoItem> items, TodoKind kind)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var valid = new List<TodoItem>(items.Count);
        foreach (var item in items)
        {
            if (string.IsNullOrWhiteSpace(item.Id) || string.IsNullOrWhiteSpace(item.Title)) continue;
            if (item.Kind != kind) continue;
            var prefix = kind == TodoKind.Homework ? "hw-" : "ann-";
            if (!item.Id.StartsWith(prefix, StringComparison.Ordinal)) continue;
            if (!Plausible(item.DueAt) || !Plausible(item.PublishedAt)) continue;
            if (!seen.Add(item.Id)) continue;
            valid.Add(item);
        }
        return valid;
    }

    private static bool Plausible(DateTimeOffset? t)
        => t is null || (t >= MinPlausible && t <= MaxPlausible);
}
