using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace HomeworkReminder.Services;

/// <summary>
/// 应用内的本地状态（勾选完成、已读）。
/// <para>
/// 雨课堂没有「学生把作业标记为完成」的接口——完成状态由作答进度决定。所以应用里的
/// 勾选只影响本地视图，不改动雨课堂。这样勾选不会撒谎成 "已完成"，但也能让你把
/// 不想再看到的事项收起来。文件里记录的 id 是稳定 id，改标题不会丢。
/// </para>
/// </summary>
public interface ILocalStateStore
{
    Task<LocalState> LoadAsync(CancellationToken ct = default);
    Task SaveAsync(LocalState state, CancellationToken ct = default);
}

public sealed class LocalState
{
    /// <summary>在应用里被勾掉的事项 id。</summary>
    public HashSet<string> Done { get; set; } = [];

    /// <summary>在应用里标记为已读的公告 id。</summary>
    public HashSet<string> ReadAnnouncements { get; set; } = [];
}

/// <inheritdoc />
public sealed class LocalStateStore : ILocalStateStore
{
    private readonly string _path;

    public LocalStateStore(string? directory = null)
    {
        var dir = directory ?? AppPaths.DataDirectory;
        _path = Path.Combine(dir, "local-state.json");
    }

    public async Task<LocalState> LoadAsync(CancellationToken ct = default)
    {
        if (!File.Exists(_path)) return new LocalState();
        try
        {
            var text = await File.ReadAllTextAsync(_path, ct).ConfigureAwait(false);
            return JsonSerializer.Deserialize(text, AppJsonContext.Default.LocalState) ?? new LocalState();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new LocalState();
        }
    }

    public async Task SaveAsync(LocalState state, CancellationToken ct = default)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            await File.WriteAllTextAsync(
                _path,
                JsonSerializer.Serialize(state, AppJsonContext.Default.LocalState),
                ct).ConfigureAwait(false);
        }
        catch (IOException)
        {
            // 本地状态写不进去不影响本次会话的使用。
        }
    }
}
