using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HomeworkReminder.Models;

namespace HomeworkReminder.Services;

/// <summary>
/// 上课提醒：按「上课前 N 分钟」发系统通知。
/// <para>
/// <b>为什么要轮询而不是给每节课排定时器</b>：课表可能在运行期间被导入/编辑，
/// 学期基准也可能被改，一次性排好的定时器会立刻过期；而且跨天、休眠唤醒后
/// 定时器未必可靠。轮询「现在该提醒谁」则天然自愈——醒来发现错过了就静默跳过
/// （见 <see cref="Timetable.DueReminders"/> 的第二条规则）。
/// </para>
/// <para>
/// 与 <see cref="AutoSyncService"/> 同款结构：构造时注入「取课表」与通知器，
/// 因此可以脱离界面单独测试。
/// </para>
/// </summary>
public sealed class ClassReminderService : IDisposable
{
    /// <summary>
    /// 轮询间隔。30 秒足够贴合「提前 N 分钟」的精度，
    /// 同时保证「上课后不再补提醒」这条规则真正生效（间隔远小于任何提前量）。
    /// </summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);

    /// <summary>
    /// 已经提醒过的课次 Id，避免重复提醒。
    /// 只保留最近这些条：课次 Id 自带日期，跨天后旧的就没用了，
    /// 不清理会让集合随运行天数无限增长。
    /// </summary>
    private const int MaxRemembered = 200;

    private readonly Func<ScheduleFile> _schedule;
    private readonly Func<string?> _termStartDate;
    private readonly Func<int> _minutesBefore;
    private readonly INotifier _notifier;
    private readonly Action<string>? _log;
    private readonly Func<DateTime> _now;

    private readonly object _gate = new();
    private readonly List<string> _notifiedOrder = [];
    private readonly HashSet<string> _notified = new(StringComparer.Ordinal);

    private CancellationTokenSource? _cts;
    private Task? _loop;

    public ClassReminderService(
        Func<ScheduleFile> schedule,
        Func<string?> termStartDate,
        Func<int> minutesBefore,
        INotifier notifier,
        Action<string>? log = null,
        Func<DateTime>? now = null)
    {
        _schedule = schedule;
        _termStartDate = termStartDate;
        _minutesBefore = minutesBefore;
        _notifier = notifier;
        _log = log;
        _now = now ?? (() => DateTime.Now);
    }

    /// <summary>是否有轮询在跑。</summary>
    public bool IsRunning
    {
        get { lock (_gate) return _loop is { IsCompleted: false }; }
    }

    /// <summary>启动轮询（已在跑则先停）。</summary>
    public async Task StartAsync()
    {
        await StopAsync().ConfigureAwait(false);

        lock (_gate)
        {
            var cts = new CancellationTokenSource();
            _cts = cts;
            _loop = Task.Run(() => LoopAsync(cts.Token), cts.Token);
        }
        _log?.Invoke($"上课提醒：已启动（提前 {_minutesBefore()} 分钟）");
    }

    /// <summary>停止轮询。重复/并发调用安全。</summary>
    public Task StopAsync()
    {
        CancellationTokenSource? cts;
        Task? loop;
        lock (_gate)
        {
            cts = _cts;
            loop = _loop;
            _cts = null;
            _loop = null;
        }

        if (cts is null) return Task.CompletedTask;
        return StopCoreAsync(cts, loop);
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

    /// <summary>清掉「已提醒」记录（换学期、重新导入课表后调用）。</summary>
    public void ResetNotified()
    {
        lock (_gate)
        {
            _notified.Clear();
            _notifiedOrder.Clear();
        }
    }

    /// <summary>
    /// 检查一次并发送该发的提醒。返回本次实际发出的条数。
    /// <para>抽成公开方法是为了让自检能直接驱动它，不必等真实的 30 秒轮询。</para>
    /// </summary>
    public int CheckOnce()
    {
        var minutesBefore = _minutesBefore();
        if (minutesBefore <= 0) return 0;

        var file = _schedule();
        if (file.Items.Count == 0) return 0;

        var termStart = _termStartDate();
        if (string.IsNullOrWhiteSpace(termStart))
        {
            // 没有学期基准就算不出「今天第几节是几点」。不报错、也不提醒——
            // 用户可以在设置里补上，界面已有提示。
            return 0;
        }

        var now = _now();
        var sessions = Timetable.Expand(
            file.Items, termStart, file.Periods,
            fromDate: now.ToString("yyyy-MM-dd"), days: 1);

        List<Timetable.ClassReminder> due;
        lock (_gate)
        {
            due = Timetable.DueReminders(sessions, now, minutesBefore, _notified);
        }

        var sent = 0;
        foreach (var reminder in due)
        {
            var notification = Timetable.BuildReminderNotification(reminder.Session, reminder.MinutesUntil);
            var error = _notifier.Show(notification);

            // 不论成功失败都记账：通知平台临时不可用时反复重试同一节课只会刷日志，
            // 而上课时间一到这条提醒本来就过期了。
            Remember(reminder.Session.Id);

            if (error is null)
            {
                sent++;
                _log?.Invoke($"上课提醒：{notification.Title} {notification.Body}（{notification.Attribution}）");
            }
            else
            {
                _log?.Invoke($"上课提醒发送失败：{error}");
            }
        }

        return sent;
    }

    private void Remember(string sessionId)
    {
        lock (_gate)
        {
            if (!_notified.Add(sessionId)) return;
            _notifiedOrder.Add(sessionId);
            while (_notifiedOrder.Count > MaxRemembered)
            {
                var oldest = _notifiedOrder[0];
                _notifiedOrder.RemoveAt(0);
                _notified.Remove(oldest);
            }
        }
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(PollInterval, ct).ConfigureAwait(false);
                if (ct.IsCancellationRequested) break;

                try
                {
                    CheckOnce();
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // 单次失败不终止循环：课表读坏、通知平台抖动都应能自愈。
                    _log?.Invoke($"上课提醒检查失败：{ex.GetType().Name}: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 正常停止。
        }
    }

    public void Dispose()
    {
        // 可能在进程退出路径（UI 线程）上调用：限时等待，超时由进程退出兜底。
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
