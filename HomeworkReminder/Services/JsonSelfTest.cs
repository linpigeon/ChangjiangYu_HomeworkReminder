using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Linq;
using System.Threading.Tasks;
using HomeworkReminder.Models;

namespace HomeworkReminder.Services;

/// <summary>
/// JSON 序列化离线自检（<c>--jsoncheck</c>）。
/// <para>
/// 存在的理由：裁剪发布（PublishTrimmed）会移除反射序列化所需的元数据，
/// 这种崩溃只在运行时、且走到对应序列化路径时才暴露——历史上就是「登录后同步即崩」，
/// 而同步必须真实扫码登录才能触发，无法在发布机上离线验证。
/// 这里把每一个参与 JSON 往返的类型用生产代码同一份 options 各跑一遍真实序列化/反序列化，
/// 反射元数据缺失会在这里原样抛出线上同款异常，从而使 trim 包无需登录即可验证。
/// </para>
/// <para>退出码：0 = 全部通过；1 = 有失败（明细打印到控制台）。</para>
/// </summary>
public static class JsonSelfTest
{
    public static async Task<int> RunAsync()
    {
        var failures = new List<string>();

        void Check(string name, Action test)
        {
            try
            {
                test();
                Console.WriteLine($"  [OK]   {name}");
            }
            catch (Exception ex)
            {
                failures.Add(name);
                Console.WriteLine($"  [FAIL] {name}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        // ---- 雨课堂接口 DTO：反序列化样本贴近线上载荷，断言关键字段确实解析出来 ----

        Check("courses/list", () =>
        {
            var list = JsonSerializer.Deserialize(
                """[{"classroom_id":123,"name":"高等数学","term":"202601","course":{"id":7,"name":"高数"}}]""",
                AppJsonContext.Default.ListYktCourse);
            Assert(list is { Count: 1 } && list[0].ClassroomId == 123
                   && list[0].Term == 202601 && list[0].CourseId == 7,
                "课程字段解析不符");
        });

        Check("logs/learn", () =>
        {
            var list = JsonSerializer.Deserialize(
                """[{"id":9,"type":19,"title":"章节作业","courseware_id":"cw1","create_time":"1726000000000","is_finished":false,"hasRead":true,"content":{"score_d":"2026-09-20 23:59","leaf_id":"456"}}]""",
                AppJsonContext.Default.ListYktActivity);
            Assert(list is { Count: 1 } && list[0].Id == 9
                   && list[0].Content?.LeafId == 456,
                "学习日志字段解析不符");
        });

        Check("pub_new_pro", () =>
        {
            var payload = JsonSerializer.Deserialize(
                """{"leaf_schedules":{"1":1,"2":{"status":3,"total":1,"done":1,"score":0}}}""",
                AppJsonContext.Default.YktProgressData);
            Assert(payload?.LeafSchedules is { } schedules
                   && schedules.Count == 2
                   && schedules["1"].IsBareNumber
                   && schedules["2"].Done == 1,
                "进度字典解析不符");
        });

        Check("leaf_info", () =>
        {
            var detail = JsonSerializer.Deserialize(
                """{"publish_time":"1726000000000","score_deadline":"","is_assessed":true,"leaf_type":6}""",
                AppJsonContext.Default.YktLeafDetail);
            Assert(detail is { IsAssessed: true, LeafType: 6 }
                   && detail.PublishTime.Milliseconds == 1726000000000L,
                "作业详情解析不符");
        });

        Check("course/chapter", () =>
        {
            var chapters = JsonSerializer.Deserialize(
                """[{"id":1,"name":"第一章","section_leaf_list":[{"id":2,"name":"作业1","leafinfo_id":"55","score_deadline":1726000000000}]}]""",
                AppJsonContext.Default.ListYktChapter);
            Assert(chapters is { Count: 1 }
                   && chapters[0].SectionLeafList is { Count: 1 } leaves
                   && leaves[0].LeafInfoId == 55,
                "章节树解析不符");
        });

        Check("user/basic-info", () =>
        {
            var profile = JsonSerializer.Deserialize(
                """{"id":"u1","name":"张三","nickname":"微信用户","avatar":"http://x/y.png","school":"某大学","schoolNumber":"2024001","role":1}""",
                AppJsonContext.Default.YktUserProfile);
            Assert(profile?.DisplayName == "张三"
                   && profile.AvatarUrl == "https://x/y.png"
                   && profile.Subtitle == "某大学 · 2024001",
                "用户信息解析不符");

            // v/course_meta/user_info 用蛇形命名（school_number），也要能解析。
            var snake = JsonSerializer.Deserialize(
                """{"name":"李四","avatar":null,"school":"某大学","school_number":"2024002"}""",
                AppJsonContext.Default.YktUserProfile);
            Assert(snake?.Subtitle == "某大学 · 2024002", "蛇形命名的用户信息解析不符");
        });

        Check("POST 请求体", () =>
        {
            var progress = JsonSerializer.Serialize(
                new YktProgressRequest { ClassroomId = 1, Cid = 2, UniversityId = 3214 },
                AppJsonContext.Default.YktProgressRequest);
            // 字段名是线上契约，必须精确；数值与空白格式无关，往返校验。
            Assert(progress.Contains("\"classroom_id\"") && progress.Contains("\"uv_id\""),
                "进度请求体字段名不符: " + progress);
            var back = JsonSerializer.Deserialize(progress, AppJsonContext.Default.YktProgressRequest);
            Assert(back is { ClassroomId: 1, Cid: 2, UniversityId: 3214 }, "进度请求体往返不符");

            var markAll = JsonSerializer.Serialize(
                new YktMarkAllReadRequest { UniversityId = 3214 },
                AppJsonContext.Default.YktMarkAllReadRequest);
            Assert(markAll.Contains("\"term\"") && markAll.Contains("latest"),
                "已读请求体字段名不符: " + markAll);
        });

        // ---- 本地持久化：走真实 Store（含 DPAPI 信封），落盘到临时目录 ----

        var tempDir = Path.Combine(Path.GetTempPath(), "hw-jsoncheck-" + Guid.NewGuid().ToString("N"));
        try
        {
            Check("SessionStore 往返", () =>
            {
                var store = new SessionStore(tempDir);
                var file = new SessionFile
                {
                    SessionId = "sid-123",
                    CsrfToken = "csrf-abc",
                    UniversityId = 3214,
                    Term = 202601,
                    UserName = "张三",
                };
                store.SaveAsync(file).GetAwaiter().GetResult();
                var loaded = store.LoadAsync().GetAwaiter().GetResult();
                Assert(loaded is { HasValue: true }
                       && loaded.SessionId == "sid-123"
                       && loaded.CsrfToken == "csrf-abc"
                       && loaded.UserName == "张三",
                    "会话文件往返不符");
            });

            Check("LocalStateStore 往返", () =>
            {
                var store = new LocalStateStore(tempDir);
                store.SaveAsync(new LocalState
                {
                    Done = ["a", "b"],
                    ReadAnnouncements = ["c"],
                }).GetAwaiter().GetResult();
                var loaded = store.LoadAsync().GetAwaiter().GetResult();
                Assert(loaded.Done.SetEquals(["a", "b"]) && loaded.ReadAnnouncements.SetEquals(["c"]),
                    "本地状态往返不符");
            });

            Check("TodoCacheStore 往返与校验", () =>
            {
                var store = new TodoCacheStore(tempDir);
                store.SaveAsync(new TodoCacheFile
                {
                    SyncedAt = new DateTimeOffset(2026, 9, 20, 12, 0, 0, TimeSpan.FromHours(8)),
                    Homework =
                    [
                        new TodoItem
                        {
                            Id = "hw-100-200", Kind = TodoKind.Homework, Title = "作业A",
                            ClassroomId = 100, Status = TodoStatus.InProgress, Done = 1, Total = 3,
                        },
                        // 坏条目：id 前缀与类型不符，加载时必须被校验丢弃。
                        new TodoItem { Id = "junk", Kind = TodoKind.Homework, Title = "坏条目" },
                    ],
                    Announcements =
                    [
                        new TodoItem { Id = "ann-100-7", Kind = TodoKind.Announcement, Title = "公告X", ClassroomId = 100 },
                    ],
                    CourseSignatures = new Dictionary<long, string> { [100] = "sig" },
                    CourseCount = 2,
                }).GetAwaiter().GetResult();

                var loaded = store.LoadAsync().GetAwaiter().GetResult();
                Assert(loaded is not null
                       && loaded.Homework.Count == 1
                       && loaded.Homework[0] is { Id: "hw-100-200", Status: TodoStatus.InProgress, Done: 1, Total: 3 }
                       && loaded.Announcements.Count == 1
                       && loaded.CourseSignatures[100] == "sig"
                       && loaded.CourseCount == 2,
                    "待办缓存往返不符（含坏条目剔除）");

                store.ClearAsync().GetAwaiter().GetResult();
                Assert(store.LoadAsync().GetAwaiter().GetResult() is null, "缓存清除后应读不到");
            });

            Check("AppSettings 往返", () =>
            {
                var json = JsonSerializer.Serialize(
                    new AppSettings { UniversityId = 1, Term = 2, WallpaperOpacityPercent = 55, ThemeModeIndex = 1 },
                    AppJsonContext.Default.AppSettings);
                var back = JsonSerializer.Deserialize(json, AppJsonContext.Default.AppSettings);
                Assert(back is { UniversityId: 1, Term: 2, ThemeModeIndex: 1 }
                       && Math.Abs(back.WallpaperOpacityPercent - 55) < 0.001,
                    "设置往返不符");
            });
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch (IOException) { }
        }

        // ---- 自动同步的「新作业」判定：首次建基线不误报，之后新增才通知 ----

        Check("AutoSync 新作业判定", () =>
        {
            var notifications = new List<AppNotification>();
            using var svc = new AutoSyncService(
                _ => Task.FromResult<SyncResult?>(new SyncResult()),
                new RecordingNotifier(notifications));

            // 第一次：三项作业全部「没见过」，但这是基线，不应产生任何通知。
            var first = svc.Observe(Result(("hw-1", "作业A"), ("hw-2", "作业B"), ("hw-3", "作业C")));
            Assert(first.Count == 0, $"首次同步不应报新作业，实际报了 {first.Count} 项");

            // 第二次：同样的三项 —— 仍然没有新的。
            var second = svc.Observe(Result(("hw-1", "作业A"), ("hw-2", "作业B"), ("hw-3", "作业C")));
            Assert(second.Count == 0, $"内容未变不应报新作业，实际报了 {second.Count} 项");

            // 第三次：多了一项 —— 只报这一项。
            var third = svc.Observe(Result(
                ("hw-1", "作业A"), ("hw-2", "作业B"), ("hw-3", "作业C"), ("hw-4", "作业D")));
            Assert(third.Count == 1 && third[0].Id == "hw-4",
                $"应只报新增的 hw-4，实际报了 {third.Count} 项");

            // Notify 真走一遍通知平台（这里是记录桩），断言新作业确实送达。
            var notifyError = svc.Notify(third);
            Assert(notifyError is null, $"通知发送不应失败：{notifyError}");
            Assert(notifications.Count == 1 && notifications[0].Body.Contains("作业D"),
                $"Notify 应送达 1 条含新作业的通知，实际 {notifications.Count} 条");

            // 通知内容映射
            var n = AutoSyncService.BuildNotification(third);
            Assert(n is { } note && note.Body.Contains("作业D") && note.Attribution is not null,
                "单条新作业的通知内容不符");

            // 多项合并：标题带「等 N 项」，正文最多列 3 条
            var many = AutoSyncService.BuildNotification(
                [Todo("hw-5", "E"), Todo("hw-6", "F"), Todo("hw-7", "G"), Todo("hw-8", "H")]);
            Assert(many is not null, "多项新作业应能组装出通知");
            var m = many!.Value;
            Assert(m.Title.Contains("等 4 项") && m.Body.Contains("另有 1 项"),
                $"多项合并的通知内容不符：{m.Title} / {m.Body}");

            // 已完成的作业不该算「新待办」
            var done = svc.Observe(new SyncResult
            {
                Homework = [Hw("hw-9", "已完成作业", TodoStatus.Completed)],
            });
            Assert(done.Count == 0, "已完成的作业不应报为新作业");

            // ResetBaseline 后重新建基线，不误报
            svc.ResetBaseline();
            var afterReset = svc.Observe(Result(("hw-1", "作业A")));
            Assert(afterReset.Count == 0, "重置基线后首次同步不应报新作业");
        });

        await Task.CompletedTask.ConfigureAwait(false);

        Console.WriteLine();
        if (failures.Count == 0)
        {
            Console.WriteLine("JSON 自检全部通过。");
            return 0;
        }
        Console.WriteLine($"JSON 自检失败 {failures.Count} 项：{string.Join("、", failures)}");
        return 1;
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    // ---- 自动同步判定用的小工具 ----

    /// <summary>记录收到的通知，用于断言（不触达系统通知平台）。</summary>
    private sealed class RecordingNotifier(List<AppNotification> sink) : INotifier
    {
        public bool IsAvailable => true;

        public string? UnavailableReason => null;

        public string? Show(AppNotification notification)
        {
            sink.Add(notification);
            return null;
        }
    }

    private static TodoItem Todo(string id, string title) => new()
    {
        Id = id,
        Kind = TodoKind.Homework,
        Title = title,
        CourseName = "测试课程",
        DueAt = DateTimeOffset.Now.AddDays(1),
        Status = TodoStatus.NotStarted,
    };

    private static TodoItem Hw(string id, string title, TodoStatus status) => new()
    {
        Id = id,
        Kind = TodoKind.Homework,
        Title = title,
        CourseName = "测试课程",
        Status = status,
    };

    private static SyncResult Result(params (string Id, string Title)[] items) => new()
    {
        Homework = items.Select(i => Todo(i.Id, i.Title)).ToList(),
    };
}
