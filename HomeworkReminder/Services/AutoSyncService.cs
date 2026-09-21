using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HomeworkReminder.Models;

namespace HomeworkReminder.Services;

/// <summary>
/// 周期性自动同步，并在出现**新作业**时发系统通知。
/// <para>
/// 刻意与被同步的业务逻辑解耦：构造时传入一个「执行同步」的回调，
/// 因此可以脱离 ViewModel 单独测试（见 <c>--synccheck</c>）。
/// </para>
/// </summary>
public sealed class AutoSyncService : IDisposable
{
    /// <summary>同步回调；返回 null 表示本轮跳过（例如手动同步在途，或未登录）。</summary>
    private readonly Func<CancellationToken, Task<SyncResult?>> _sync;
    private readonly INotifier _notifier;
    private readonly Action<string>? _log;

    /// <summary>已见过的事项 key，用于判定「新」。首次同步只建立基线，不通知。</summary>
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);

    /// <summary>是否已经建立过基线。没建立之前不通知，否则首次同步会把全部作业都报成新的。</summary>
    private bool _baselineEstablished;

    /// <summary>_cts / _loop / _shutdown 的访问锁：启停可以从任意线程发起。</summary>
    private readonly object _lifecycleLock = new();

    private CancellationTokenSource? _cts;
    private Task? _loop;

    /// <summary>最近一次停止的收尾任务，让并发/重复 StopAsync 等到同一个收尾。</summary>
    private Task _shutdown = Task.CompletedTask;

    public AutoSyncService(
        Func<CancellationToken, Task<SyncResult?>> sync,
        INotifier notifier,
        Action<string>? log = null)
    {
        _sync = sync;
        _notifier = notifier;
        _log = log;
    }

    /// <summary>当前间隔（分钟）。0 表示不自动同步。</summary>
    public int IntervalMinutes { get; private set; }

    /// <summary>是否有循环在跑（含正在收尾的旧循环：停止要等它结束才算停）。</summary>
    public bool IsRunning
    {
        get
        {
            lock (_lifecycleLock)
                return _loop is { IsCompleted: false } || !_shutdown.IsCompleted;
        }
    }

    /// <summary>
    /// 启动（或重启）周期同步。<paramref name="intervalMinutes"/> 为 0 时停止。
    /// 会先停掉并等上一次循环结束，保证任何时刻最多只有一个循环。
    /// </summary>
    public async Task StartAsync(int intervalMinutes)
    {
        await StopAsync().ConfigureAwait(false);

        IntervalMinutes = AppSettings.NormalizeRefreshMinutes(intervalMinutes);
        if (IntervalMinutes <= 0)
        {
            _log?.Invoke("自动同步：已关闭");
            return;
        }

        lock (_lifecycleLock)
        {
            var cts = new CancellationTokenSource();
            _cts = cts;
            _loop = Task.Run(() => LoopAsync(cts.Token), cts.Token);
        }
        _log?.Invoke($"自动同步：每 {IntervalMinutes} 分钟一次");
    }

    /// <summary>
    /// 停止周期同步。返回的 Task 完成 = 旧循环已收尾，不会再访问基线与同步回调。
    /// 重复/并发调用安全，都会等到同一个收尾。
    /// </summary>
    public Task StopAsync()
    {
        lock (_lifecycleLock)
        {
            if (_cts is null) return _shutdown;

            var cts = _cts;
            var loop = _loop;
            _cts = null;
            _loop = null;
            _shutdown = StopCoreAsync(cts, loop);
            return _shutdown;
        }
    }

    private static async Task StopCoreAsync(CancellationTokenSource cts, Task? loop)
    {
        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 已经停过了。
        }

        if (loop is not null)
        {
            // 先等循环彻底退出再 Dispose cts：否则循环里的 Task.Delay(ct)
            // 可能撞上已释放的 CancellationTokenSource，把循环任务 fault 掉。
            try
            {
                await loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 正常停止。
            }
        }

        cts.Dispose();
    }

    /// <summary>
    /// 处理一次同步结果：更新基线，并在有新作业时通知。
    /// 手动同步与自动同步都应走这里，保证「新」的判定是连续的。
    /// </summary>
    /// <returns>本次识别出的新作业（按截止时间升序）。</returns>
    public IReadOnlyList<TodoItem> Observe(SyncResult result)
    {
        var current = result.Homework
            .Where(h => h.Status != TodoStatus.Completed)   // 已完成的作业不算「新待办」
            .ToList();

        var keys = current.Select(KeyOf).ToHashSet(StringComparer.Ordinal);

        // 手动同步（UI 线程）与自动同步（循环线程）都会走到这里，保护 _seen。
        lock (_seen)
        {
            if (!_baselineEstablished)
            {
                // 首次同步：只建基线。否则第一次启动就会把全部作业都通知一遍。
                _seen.UnionWith(keys);
                _baselineEstablished = true;
                _log?.Invoke($"已建立基线：{keys.Count} 项待完成作业，本次不通知");
                return [];
            }

            var fresh = current.Where(h => !_seen.Contains(KeyOf(h))).ToList();
            _seen.UnionWith(keys);
            return fresh.OrderBy(h => h.DueAt ?? DateTimeOffset.MaxValue).ToList();
        }
    }

    /// <summary>重置基线，下次同步不产生通知（例如退出登录后重新登录）。</summary>
    public void ResetBaseline()
    {
        lock (_seen)
        {
            _seen.Clear();
            _baselineEstablished = false;
        }
    }

    /// <summary>把新作业转成通知并发送。返回失败原因（成功为 null）。</summary>
    public string? Notify(IReadOnlyList<TodoItem> fresh)
    {
        var n = BuildNotification(fresh);
        return n is null ? null : _notifier.Show(n.Value);
    }

    /// <summary>
    /// 把新作业组装成一条通知。
    /// <para>
    /// 与实际发送分离，便于自检（<c>--synccheck</c>）在不依赖通知平台的情况下
    /// 校验「新作业 → 通知内容」的映射是否正确。
    /// </para>
    /// </summary>
    public static AppNotification? BuildNotification(IReadOnlyList<TodoItem> fresh)
    {
        if (fresh.Count == 0) return null;

        // 一次发现多项时合并成一条，避免刷屏；超出部分用「等 N 项」概括。
        var first = fresh[0];
        var title = fresh.Count == 1
            ? first.CourseName
            : $"{first.CourseName} 等 {fresh.Count} 项新作业";

        var body = fresh.Count == 1
            ? first.Title
            : string.Join("\n", fresh.Take(3).Select(h => "· " + h.Title))
              + (fresh.Count > 3 ? $"\n…另有 {fresh.Count - 3} 项" : string.Empty);

        var attribution = first.DueAt is null
            ? "无期限"
            : $"截止 {first.DueText}（{first.RemainingText}）";

        return new AppNotification(title, body, attribution, first.SourceUrl);
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        var interval = TimeSpan.FromMinutes(IntervalMinutes);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(interval, ct).ConfigureAwait(false);
                if (ct.IsCancellationRequested) break;

                try
                {
                    var result = await _sync(ct).ConfigureAwait(false);
                    if (result is null)
                    {
                        _log?.Invoke("本轮自动同步跳过（手动同步在途或未登录）");
                        continue;
                    }

                    var fresh = Observe(result);
                    if (fresh.Count > 0)
                    {
                        if (AppSettings.Current.NotifyOnNewHomework)
                        {
                            var error = Notify(fresh);
                            _log?.Invoke(error is null
                                ? $"发现 {fresh.Count} 项新作业，已通知"
                                : $"发现 {fresh.Count} 项新作业，但通知失败：{error}");
                        }
                        else
                        {
                            _log?.Invoke($"发现 {fresh.Count} 项新作业（通知已在设置中关闭）");
                        }
                    }
                    else
                    {
                        _log?.Invoke("自动同步完成，无新增");
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (YktAuthExpiredException)
                {
                    // 登录态失效：同步回调已把界面切到错误状态并请求停止本服务，
                    // 重试无意义，直接结束循环。
                    _log?.Invoke("自动同步：登录态已失效，已停止");
                    break;
                }
                catch (Exception ex)
                {
                    // 单次失败不终止循环：网络抖动等临时故障应能自愈。
                    _log?.Invoke($"自动同步失败：{ex.GetType().Name}: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 正常停止。
        }
    }

    /// <summary>
    /// 事项的稳定标识。用 Id 而不是标题——标题改一个字就会误判成新作业。
    /// </summary>
    private static string KeyOf(TodoItem item) => $"{item.Kind}:{item.Id}";

    public void Dispose()
    {
        // 可能在进程退出路径（UI 线程）上调用：限时等待在途循环，
        // 避免循环正在等 UI 线程派发时把退出卡死；超时由进程退出兜底。
        try
        {
            StopAsync().Wait(TimeSpan.FromSeconds(3));
        }
        catch (Exception)
        {
            // 退出路径不再上报。
        }
    }
}
