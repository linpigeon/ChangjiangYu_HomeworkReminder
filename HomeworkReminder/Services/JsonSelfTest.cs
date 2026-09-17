using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

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
}
