using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HomeworkReminder.Models;
using HomeworkReminder.ViewModels;

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
                    ChaoxingCourses =
                    [
                        new ChaoxingCourse
                        {
                            CourseId = 244459960, ClazzId = 153050293, CourseName = "信号与系统",
                            ClazzName = "2026-2027-1-通信", PendingTaskPoints = 3,
                            Leaves = [new ChaoxingTaskPoint { ChapterNumber = "4.5", Name = "傅里叶变换的性质", PendingCount = 3 }],
                        },
                        // 坏条目：缺课程名，加载时必须被剔除。
                        new ChaoxingCourse { CourseId = 9, ClazzId = 9 },
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

                // 课程维度汇总也必须能往返（源生成上下文漏注册会静默丢字段）。
                Assert(loaded is not null,
                    "缓存反序列化结果为 null");
                Assert(loaded!.ChaoxingCourses.Count == 1
                       && loaded.ChaoxingCourses[0].PendingTaskPoints == 3
                       && loaded.ChaoxingCourses[0].Leaves.Count == 1
                       && loaded.ChaoxingCourses[0].Leaves[0].Name == "傅里叶变换的性质",
                    $"课程汇总未正确往返：{loaded.ChaoxingCourses.Count} 门");

                // 坏课程（缺课程名 / id 不成立 / 重复）应被剔除。
                Assert(loaded.ChaoxingCourses.All(c => !string.IsNullOrWhiteSpace(c.CourseName) && c.CourseId > 0),
                    "缓存校验未剔除坏课程条目");

                store.ClearAsync().GetAwaiter().GetResult();
                Assert(store.LoadAsync().GetAwaiter().GetResult() is null, "缓存清除后应读不到");
            });

            Check("学习通条目缓存往返（相对时间还原）", () =>
            {
                // 学习通列表页只给「剩余 2 天」这种相对量，截止时间是用同步那一刻推算的。
                // 缓存必须存下「基准 + 相对量」，否则应用关几天再打开，倒计时会少算那几天。
                var syncedAt = DateTimeOffset.Now.AddHours(-6);

                // 先做一次纯序列化往返：把「字段没被写进去」与「校验逻辑丢了」区分开。
                var probeFile = new TodoCacheFile
                {
                    SyncedAt = syncedAt,
                    Chaoxing =
                    [
                        new TodoItem
                        {
                            Id = "hw-cx-work-probe", Kind = TodoKind.Homework, Title = "probe",
                            DeadlineAnchor = syncedAt, RemainingAtSync = TimeSpan.FromDays(2), DueAt = null,
                        },
                    ],
                };
                var probeJson = JsonSerializer.Serialize(probeFile, AppJsonContext.Default.TodoCacheFile);
                var probeBack = JsonSerializer.Deserialize(probeJson, AppJsonContext.Default.TodoCacheFile);
                var probeItem = probeBack?.Chaoxing.FirstOrDefault();
                Assert(probeItem?.DeadlineAnchor is not null && probeItem.RemainingAtSync == TimeSpan.FromDays(2),
                    "截止时间的基准/相对量未能通过 JSON 往返"
                    + "（源生成上下文对 DateTimeOffset? / TimeSpan? 的支持有问题？）");

                var store = new TodoCacheStore(tempDir);
                store.SaveAsync(new TodoCacheFile
                {
                    SyncedAt = syncedAt,
                    Chaoxing =
                    [
                        new TodoItem
                        {
                            Id = "hw-cx-work-555", Kind = TodoKind.Homework, Title = "学习通作业",
                            CourseName = "高等数学", Subject = "高等数学", ClassroomId = 77,
                            DueAt = null,                       // 相对时间形态：绝对时间为空
                            DeadlineAnchor = syncedAt,
                            RemainingAtSync = TimeSpan.FromDays(2),
                            Status = TodoStatus.NotStarted,
                        },
                        // 坏条目：基准落在未来，相对量无从还原（很可能是手改或被写坏），
                        // 应当被剔除。
                        new TodoItem
                        {
                            Id = "hw-cx-exam-556", Kind = TodoKind.Homework, Title = "基准在未来的考试",
                            DeadlineAnchor = DateTimeOffset.Now.AddYears(1),
                            RemainingAtSync = TimeSpan.FromDays(1),
                        },
                    ],
                }).GetAwaiter().GetResult();

                var loaded = store.LoadAsync().GetAwaiter().GetResult();
                var item = loaded?.Chaoxing.FirstOrDefault();
                Assert(item is not null && loaded!.Chaoxing.Count == 1,
                    $"学习通缓存往返不符（应保留 1 条、剔除基准在未来的项），实际 {loaded?.Chaoxing.Count} 条");

                // 绝对时间仍是空的（因为它本来就不该被当成既定事实），由基准 + 相对量推算。
                Assert(item!.DueAt is null && item.RemainingAtSync == TimeSpan.FromDays(2),
                    "相对时间形态不应落成绝对时间");

                var expected = syncedAt + TimeSpan.FromDays(2);
                Assert(CloseEnough(item.EffectiveDueAt, expected),
                    $"推算出的截止时间不符：期望 {expected:O}，实际 {item.EffectiveDueAt:O}");

                // 关键性质：同步发生在 6 小时前，而页面当时说「还剩 2 天」，
                // 所以此刻真实的剩余时间应当约 42 小时 —— 而不是页面上的 2 天。
                // 这正是「存基准 + 相对量」的意义：绝对时间会随休眠时间自然缩短。
                var left = item.Remaining;
                var expectedLeft = TimeSpan.FromDays(2) - TimeSpan.FromHours(6);
                var lt = left?.Ticks ?? long.MinValue;
                Assert(left is not null && Math.Abs(lt - expectedLeft.Ticks) < TimeSpan.FromMinutes(1).Ticks,
                    $"相对截止时间应随真实时间流逝而减少（期望约 42 小时），实际 {left}");

                store.ClearAsync().GetAwaiter().GetResult();
            });

            Check("旧缓存学习通条目的基准回填", () =>
            {
                // 本改动之前写入的缓存没有 DeadlineAnchor 字段。加载时应当用缓存自身的
                // SyncedAt 把基准补上，而不是把这条作业整个丢掉。
                var syncedAt = DateTimeOffset.Now.AddHours(-2);
                var store = new TodoCacheStore(tempDir);
                store.SaveAsync(new TodoCacheFile
                {
                    SyncedAt = syncedAt,
                    Chaoxing =
                    [
                        new TodoItem
                        {
                            Id = "hw-cx-work-777", Kind = TodoKind.Homework, Title = "旧缓存作业",
                            RemainingAtSync = TimeSpan.FromDays(1),
                            DeadlineAnchor = null,          // 模拟旧缓存：没有基准字段
                        },
                    ],
                }).GetAwaiter().GetResult();

                var item = store.LoadAsync().GetAwaiter().GetResult()?.Chaoxing.FirstOrDefault();
                Assert(item is not null, "旧缓存条目不应被丢弃");
                Assert(CloseEnough(item!.DeadlineAnchor, syncedAt),
                    $"旧缓存的基准应回填为缓存自身的同步时刻，实际 {item.DeadlineAnchor:O}");
                Assert(CloseEnough(item.EffectiveDueAt, syncedAt + TimeSpan.FromDays(1)),
                    $"回填后推算出的截止时间不符：实际 {item.EffectiveDueAt:O}");

                store.ClearAsync().GetAwaiter().GetResult();
            });

            Check("批改网未完成作文列表解析", () =>
            {
                // 样本照抄真实页面（2026-10 实测）：表头是 reItemM，每条作业是 reItemS，
                // 列固定为 作业号/标题/类型/出题人/指定班级/截止时间/操作。
                // 刻意保留真实页面里的坑：标题含 &#39; 实体、末列带 &nbsp;、
                // 标题被服务端截断加省略号。
                const string html = """
                    <div id="essayList" class="ydTable">
                    <ul class="reItemM">
                        <li style="width:80px;" class="ft">作业号</li>
                        <li style="width:448px;">作业标题</li>
                        <li style="width:50px;">类型</li>
                        <li style="width:80px;">出题人</li>
                        <li style="width:100px;">指定班级</li>
                        <li style="width:80px;">截止时间</li>
                        <li style="width:110px;">操作</li>
                        <div style="clear:both"></div>
                    </ul>
                    <ul class="reItemS">
                        <li style="width:80px;">3429100</li>
                        <li style="width:448px;" class="stu_title">
                        <a href="?c=v2&a=write&rid=3429100" title="查看作文:">2025级BIII作文4：The Internet Era and College Students&#39; Growth</a>
                        </li>
                        <li style="width:50px;">作文</li>
                        <li style="width:80px;">罗小芳</li>
                        <li style="width:100px;" title="2025级大英BIII周二１.２节罗小芳,">2025级大英BIII周二１.２节罗小芳,</li>
                        <li style="width:80px;">2026-12-06 23:59</li>
                        <li style="width:110px;"><a href="?c=v2&a=write&rid=3429100">查看</a></li>
                    </ul>
                    <ul class="reItemS">
                        <li style="width:80px;">3429097</li>
                        <li style="width:448px;" class="stu_title">
                        <a href="?c=v2&a=write&rid=3429097" title="查看作文:">2025级BIII作文1：Whether College Students Should Take Holiday...</a>
                        </li>
                        <li style="width:50px;">作文</li>
                        <li style="width:80px;">罗小芳</li>
                        <li style="width:100px;">2025级大英BIII周四１.２节罗小芳,</li>
                        <li style="width:80px;">2026-12-06 23:59</li>
                        <li style="width:110px;">&nbsp;</li>
                    </ul>
                    </div>
                    """;

                var tasks = PigaiApiClient.ParseList(html);
                Assert(tasks.Count == 2, $"应解析出 2 条，实际 {tasks.Count} 条");

                var first = tasks[0];
                Assert(first.Rid == 3429100, $"作业号不符：{first.Rid}");
                Assert(first.Title == "2025级BIII作文4：The Internet Era and College Students' Growth",
                    $"标题实体未正确还原：{first.Title}");
                Assert(first.Type == "作文", $"类型不符：{first.Type}");
                Assert(first.Teacher == "罗小芳", $"出题人不符：{first.Teacher}");
                Assert(first.Classes == "2025级大英BIII周二１.２节罗小芳,",
                    $"指定班级不符：{first.Classes}");
                Assert(first.DueAt == new DateTimeOffset(2026, 12, 6, 23, 59, 0, TimeSpan.FromHours(8)),
                    $"截止时间不符：{first.DueAt:O}");

                // 表头 reItemM 不得被当成一条作业。
                Assert(tasks.All(t => t.Rid != 0), "表头被误解析成了作业条目");

                // 映射到待办：稳定 Id、未开始、详情链接。
                var item = PigaiSyncService.MapForTest(tasks[0]);
                Assert(item.Id == "hw-pg-3429100", $"稳定 Id 不符：{item.Id}");
                Assert(item.Kind == TodoKind.Homework && item.Status == TodoStatus.NotStarted,
                    "未完成作文应映射为「未开始」的作业");
                Assert(item.SourceUrl == "https://www.pigai.org/index.php?c=v2&a=write&rid=3429100",
                    $"详情链接不符：{item.SourceUrl}");
                Assert(item.Title.Length > 0 && item.CourseName == "罗小芳",
                    "标题/出题人映射不符");
            });

            Check("批改网会话判定（不得要求特定 cookie 名）", () =>
            {
                // 回归用例：曾要求 cookie 里必须有 _JKU，否则一路 continue、永不落盘。
                // 真机登录后 cookie 里未必有该项，结果表现为「登录过、重启后仍显示未登录、
                // 待办里也没有作文」且完全静默。登录是否成立由页面探测/接口判定，
                // 这里只负责「把会话存下来」。
                var withoutJku = PigaiLoginService.BuildSessionFromVerifiedLogin(
                    "PHPSESSID=abc123; old=2012; isPrize=0");
                Assert(withoutJku is not null,
                    "探测已确认登录的会话不得因为没有 _JKU 就被丢弃");
                Assert(withoutJku!.HasValue && withoutJku.CookieHeader!.Contains("PHPSESSID=abc123"),
                    "会话应保留整条 cookie");
                Assert(withoutJku.Account is not null && withoutJku.Account.Length > 0,
                    "没有 _JKU 时账号显示名应退回占位而不是 null");

                // 有 _JKU 时用它作为账号显示名。
                var withJku = PigaiLoginService.BuildSessionFromVerifiedLogin(
                    "PHPSESSID=abc; _JKU=13900000000; isPrize=0");
                Assert(withJku?.Account == "13900000000", $"应优先用 _JKU 作为账号名，实际 {withJku?.Account}");

                // 一条 cookie 都没有时才算拿不到会话。
                Assert(PigaiLoginService.BuildSessionFromVerifiedLogin(null) is null,
                    "空 cookie 应返回 null");
                Assert(PigaiLoginService.BuildSessionFromVerifiedLogin("   ") is null,
                    "空白 cookie 应返回 null");
            });

            Check("学习通课程列表解析（backclazzdata）", () =>
            {
                // 样本照抄真实响应结构：content.id 是 clazzId、content.cpi 是 personId、
                // course.data[0].id 是 courseId。三者混用会拿到空白页。
                const string json = """
                    {"result":1,"msg":"获取成功","channelList":[
                      {"cfid":-1,"cataName":"课程","cpi":500602665,"id":0,"content":{
                        "id":153050293,"cpi":500602665,"name":"2026-2027-1-通信","isretire":1,
                        "course":{"data":[{"id":244459960,"name":"信号与系统"}]}}},
                      {"cfid":-1,"cataName":"课程","cpi":500602665,"id":0,"content":{
                        "id":152328665,"cpi":500602665,"name":"2026秋","isretire":0,
                        "course":{"data":[{"id":266218466,"name":"人工智能与法律"}]}}},
                      {"cfid":-1,"cataName":"课程","id":0,"content":{
                        "id":1,"name":"缺 cpi 的条目","course":{"data":[{"id":2,"name":"X"}]}}}
                    ]}
                    """;

                var courses = ChaoxingApiClient.ParseCourses(json);
                Assert(courses.Count == 2, $"应解析出 2 门（缺 cpi 的应被丢弃），实际 {courses.Count}");

                var ykt = courses[0];
                Assert(ykt.CourseId == 244459960 && ykt.ClazzId == 153050293 && ykt.PersonId == 500602665,
                    $"三元组解析错位：course={ykt.CourseId} clazz={ykt.ClazzId} person={ykt.PersonId}");
                Assert(ykt.CourseName == "信号与系统" && ykt.ClazzName == "2026-2027-1-通信",
                    "课程名/班级名不符");
                Assert(ykt.IsRetired, "isretire=1 应识别为已结课");
                Assert(!courses[1].IsRetired, "isretire=0 不应识别为已结课");
            });

            Check("学习通任务点解析（只算叶子，防父层汇总重复计数）", () =>
            {
                // 样本照抄真实页面结构。关键陷阱：父章节的 knowledgeJobCount 是
                // 子章节的汇总值，直接求和会偏大（实测信号与系统 16 vs 13）。
                const string html = """
                    <div class="continue-study"><span class="charter">待完成任务点</span></div>
                    <div class="content1"><div class="timeline">

                      <!-- 章一：一个 leveltwo 带更深层子节点 → 它是汇总，不该计入 -->
                      <div class="units">
                        <h2 class="levelfirst clearfix"><span class="chapterNumber">第2章</span></h2>
                        <div class="leveltwo">
                          <h3><a aria-label="2.1 父节点">
                            <span class="chapterNumber">2.1</span>
                            <span class="articlename" title="父节点">父节点</span>
                          </a></h3>
                          <input type="hidden" value="1" class="knowledgeJobCount"/><em class="orange">1</em>
                          <div class="levelthree" id="100">
                            <h3><a aria-label="2.1.1 子节点">
                              <span class="chapterNumber">2.1.1</span>
                              <span class="articlename" title="子节点">子节点</span>
                            </a></h3>
                            <input type="hidden" value="1" class="knowledgeJobCount"/><em class="orange">1</em>
                          </div>
                        </div>
                      </div>

                      <!-- 章二：扁平结构，任务点直接挂在 leveltwo（无三级嵌套）→ 计入 -->
                      <div class="units">
                        <h2 class="levelfirst clearfix"><span class="chapterNumber">第4章</span></h2>
                        <div class="leveltwo">
                          <h3><a aria-label="4.5 傅里叶变换的性质">
                            <span class="chapterNumber">4.5</span>
                            <span class="articlename" title="傅里叶变换的性质">傅里叶变换的性质</span>
                          </a></h3>
                          <input type="hidden" value="3" class="knowledgeJobCount"/><em class="orange">3</em>
                        </div>
                        <div class="leveltwo">
                          <h3><a aria-label="4.6 无任务点">
                            <span class="chapterNumber">4.6</span>
                            <span class="articlename" title="无任务点">无任务点</span>
                          </a></h3>
                        </div>
                      </div>
                    </div></div>
                    """;

                var tp = ChaoxingApiClient.ParseTaskPoints(html, 244459960);

                // 只有 2.1.1（子）与 4.5（扁平）是真叶子：1 + 3 = 4。
                // 若误把 2.1 父层汇总也算上，会得到 5。
                Assert(tp.PendingTotal == 4,
                    $"应按叶子口径得 4（2.1 父层汇总不计），实际 {tp.PendingTotal}");
                Assert(tp.Leaves.Count == 2, $"应有 2 个叶子章节，实际 {tp.Leaves.Count}");
                Assert(tp.Leaves.Any(l => l.ChapterNumber == "2.1.1" && l.PendingCount == 1),
                    "缺失 2.1.1 子节点");
                Assert(tp.Leaves.Any(l => l.ChapterNumber == "4.5" && l.PendingCount == 3),
                    "缺失 4.5（扁平结构）");
                Assert(tp.Leaves.All(l => l.ChapterNumber != "2.1"),
                    "父层汇总 2.1 不该被计入");
                Assert(tp.Leaves.All(l => l.ChapterNumber != "4.6"),
                    "没有任务点的章节不该出现在明细里");

                // 没有「待完成任务点」区块 = 该课没有未完成任务点（已结课/纯资料课）。
                var none = ChaoxingApiClient.ParseTaskPoints("<html><body>没有那个区块</body></html>", 1);
                Assert(none.PendingTotal == 0 && none.Leaves.Count == 0,
                    "无「待完成任务点」区块时应返回 0 而不是报错");
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

            Check("ReleaseInfo 解析与版本比较", () =>
            {
                // GitHub/Gitea latest release 的关键字段。
                const string payload = """
                    {"tag_name": "v9.9.9", "name": "v9.9.9", "html_url": "https://example.com/r", "prerelease": false}
                    """;
                var info = JsonSerializer.Deserialize(payload, AppJsonContext.Default.ReleaseInfo);
                Assert(info is { TagName: "v9.9.9", HtmlUrl: "https://example.com/r" }, "ReleaseInfo 解析不符");
                Assert(UpdateService.IsNewer("v9.9.9"), "远端 v9.9.9 应判定为有更新");
                Assert(!UpdateService.IsNewer("v0.0.1"), "远端 v0.0.1 不应判定为有更新");
                Assert(!UpdateService.IsNewer("不是版本号"), "非法 tag 不应判定为有更新");
            });

            Check("更新包：版本比较、资产挑选与嵌入脚本", () =>
            {
                Assert(UpdateService.TryParseVersion("v1.2.6-beta.1", out var pre) && pre.Build == 6,
                    "预发布后缀应被剥离后解析");

                var release = new ReleaseInfo
                {
                    TagName = "v9.9.9",
                    Assets =
                    [
                        new ReleaseAsset { Name = "HomeworkReminder-v9.9.9-android.apk", Size = 1, BrowserDownloadUrl = "https://x/apk" },
                        new ReleaseAsset { Name = "HomeworkReminder-v9.9.9-win-x64.zip", Size = 2, BrowserDownloadUrl = "https://x/zip" },
                    ],
                };
                const string manifestJson = """
                    {"version":"9.9.9","file":"HomeworkReminder-v9.9.9-win-x64.zip","sha256":"ABC","size":2,"entry":"HomeworkReminder.Desktop.exe"}
                    """;

                // ---- 有清单：按清单文件名精确匹配 ----
                var chosen = UpdateService.ChoosePackage(release, "测试源", manifestJson);
                Assert(chosen is not null, "有清单时应能挑到包");
                Assert(chosen!.Asset.Name == "HomeworkReminder-v9.9.9-win-x64.zip",
                    $"应按清单文件名挑到 win-x64 包，实际 {chosen.Asset.Name}");
                Assert(chosen.Manifest?.Sha256 == "ABC", "清单内容应被解析出来");

                // 清单点名了不存在的文件 → 退化为按平台特征挑，而不是彻底失败
                Assert(UpdateService.ChoosePackage(release, "测试源", """{"file":"不存在.zip"}""")
                        ?.Asset.Name == "HomeworkReminder-v9.9.9-win-x64.zip",
                    "清单文件不存在时应按平台特征兜底挑包");

                // 清单 JSON 坏掉 → 同样兜底，不应抛异常
                Assert(UpdateService.ChoosePackage(release, "测试源", "{ 这不是 JSON")
                        ?.Asset.Name == "HomeworkReminder-v9.9.9-win-x64.zip",
                    "清单损坏时应按平台特征兜底挑包");

                // ---- 无清单：挑到包但 Manifest 为 null（无哈希可校验）----
                var noManifest = UpdateService.ChoosePackage(release, "测试源", null);
                Assert(noManifest is not null && noManifest.Manifest is null,
                    "无清单时应挑到包但 Manifest 为 null");
                Assert(noManifest!.Asset.Name!.Contains("win-x64"), "兜底挑包不应挑到 Android 包");

                // ---- 只有 Android 资产时必须挑不到：绝不能把 APK 当 Windows 包 ----
                var androidOnly = new ReleaseInfo
                {
                    TagName = "v9.9.9",
                    Assets = [new ReleaseAsset { Name = "HomeworkReminder-v9.9.9-android.apk", Size = 1 }],
                };
                Assert(UpdateService.ChoosePackage(androidOnly, "测试源", null) is null,
                    "只有 Android 资产时不应挑出包");

                Assert(UpdateService.ChoosePackage(new ReleaseInfo { TagName = "v9.9.9", Assets = [] }, "测试源", null) is null,
                    "无资产时应返回 null");

                // ---- SHA256：用已知内容的固定期望值确认算法与编码 ----
                var probe = Path.Combine(Path.GetTempPath(), $"hr-sha-{Guid.NewGuid():N}.bin");
                try
                {
                    File.WriteAllText(probe, "abc");
                    Assert(UpdateService.ComputeSha256(probe)
                            == "BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD",
                        "SHA256 不正确（算法或十六进制编码不符预期）");
                }
                finally
                {
                    try { File.Delete(probe); } catch { /* 清理失败不影响断言 */ }
                }

                // ---- 嵌入的替换脚本必须真的打进程序集 ----
                // 漏打的话只在运行时才暴露（下载完才发现更新不了），所以在这里拦住。
                Assert(UpdateApplier.ScriptResourceAvailable(),
                    "嵌入资源 apply-update.ps1 缺失：更新替换会失败，检查 csproj 的 EmbeddedResource");
                var script = UpdateApplier.ExtractScript();
                Assert(File.Exists(script) && new FileInfo(script).Length > 500,
                    "释放出的替换脚本为空或过小");
                Assert(File.ReadAllText(script).Contains("TargetExe"),
                    "释放出的替换脚本内容不对（缺少参数名）");

                // 字节级校验：必须恰好一个 UTF-8 BOM。
                // 少了 → PowerShell 5.1 按 ANSI 解码中文注释，语法被破坏（实测 7 个解析错误）；
                // 多了 → 变成双重 BOM，解析器把第二个当作「[CmdletBinding()] 前的意外标记」
                //        而拒绝整个脚本（实测踩到，且只在运行时才暴露）。
                var scriptBytes = File.ReadAllBytes(script);
                var bomCount = 0;
                for (var i = 0; i + 2 < scriptBytes.Length; i++)
                {
                    if (scriptBytes[i] == 0xEF && scriptBytes[i + 1] == 0xBB && scriptBytes[i + 2] == 0xBF)
                        bomCount++;
                }
                Assert(bomCount == 1,
                    $"释放出的脚本 BOM 数量应为 1，实际 {bomCount}"
                    + "（0=PowerShell 5.1 会解析失败；>1=双重 BOM 会被解析器拒绝）");

                // ---- 开发态防护 ----
                var path = UpdateApplier.CurrentExecutablePath;
                Assert(path is null || File.Exists(path),
                    "CurrentExecutablePath 要么为 null（开发态），要么指向真实存在的文件");
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

        // ---- issue #1（w3743 报告）的 5 项修复回归：离线假 API，不触网 ----

        Check("issue#1 跨课同名同截止作业不误合并", () =>
        {
            var api = new FakeYktApi(
                [Course(100, "课程A"), Course(200, "课程B")],
                new Dictionary<long, List<YktActivity>>
                {
                    [100] = [HwActivity(1, 1001, "第一次作业")],
                    [200] = [HwActivity(2, 2002, "第一次作业")],
                });
            var r = new SyncService(api).SyncAsync(null).GetAwaiter().GetResult();
            Assert(r.Homework.Count == 2, $"同名同截止作业被误合并：{r.Homework.Count} 项");
            Assert(r.Homework.Select(h => h.ClassroomId).Distinct().Count() == 2, "误合并丢了一个课堂");
        });

        Check("issue#1 同课重复日志仍去重", () =>
        {
            var api = new FakeYktApi(
                [Course(100, "课程A")],
                new Dictionary<long, List<YktActivity>>
                {
                    [100] = [HwActivity(1, 1001, "作业"), HwActivity(2, 1001, "作业")],
                });
            var r = new SyncService(api).SyncAsync(null).GetAwaiter().GetResult();
            Assert(r.Homework.Count == 1, $"同一作业的稳定 Id 应去重，实际 {r.Homework.Count} 项");
        });

        Check("issue#1 全量校验时间戳不被增量刷新", () =>
        {
            var api = new FakeYktApi(
                [Course(100, "课程A")],
                new Dictionary<long, List<YktActivity>> { [100] = [HwActivity(1, 1001, "作业")] });
            var svc = new SyncService(api);

            var r1 = svc.SyncAsync(null).GetAwaiter().GetResult();
            Assert(r1.ReusedCourses == 0 && r1.CourseFullSyncedAt.ContainsKey(100), "首次同步应走全量并记录时间戳");

            var cache = CacheOf(r1);
            var r2 = svc.SyncAsync(cache).GetAwaiter().GetResult();
            Assert(r2.ReusedCourses == 1, "缓存命中应走增量");
            Assert(r2.CourseFullSyncedAt[100] == r1.CourseFullSyncedAt[100],
                "增量路径不得刷新全量校验时间戳");

            // 超龄（>24h）→ 该课自动全量校验
            cache.CourseFullSyncedAt[100] = DateTimeOffset.Now - TimeSpan.FromHours(25);
            var r3 = svc.SyncAsync(cache).GetAwaiter().GetResult();
            Assert(r3.ReusedCourses == 0, "超龄缓存应触发全量校验");
            Assert(r3.CourseFullSyncedAt[100] > r1.CourseFullSyncedAt[100], "全量路径应刷新时间戳");

            // 旧缓存没有时间戳字段 → 同样全量
            var legacy = CacheOf(r1);
            legacy.CourseFullSyncedAt = [];
            var r4 = svc.SyncAsync(legacy).GetAwaiter().GetResult();
            Assert(r4.ReusedCourses == 0, "缺全量时间戳的旧缓存应触发全量校验");
        });

        Check("归档课程参与同步", () =>
        {
            // 只在归档列表里的班级（courses/list 不返回）：合并后应按学期过滤参与同步。
            var api = new FakeYktApi(
                [],
                new Dictionary<long, List<YktActivity>>
                {
                    [100] = [HwActivity(1, 1001, "归档课作业")],
                })
            {
                Archived = [Course(100, "归档课")],
            };
            var r = new SyncService(api).SyncAsync(null).GetAwaiter().GetResult();
            Assert(r.CourseCount == 1 && r.ArchivedCourses == 1,
                $"归档课程未参与同步：courses={r.CourseCount} archived={r.ArchivedCourses}");
            Assert(r.Homework.Count == 1, "归档课程的作业未同步出来");
        });

        Check("归档课程不影响在册课程", () =>
        {
            // 同一班级同时在册与归档（理论上不该发生）：去重后不重复同步。
            var api = new FakeYktApi(
                [Course(100, "课程A")],
                new Dictionary<long, List<YktActivity>>
                {
                    [100] = [HwActivity(1, 1001, "作业")],
                })
            {
                Archived = [Course(100, "课程A"), new YktCourse { ClassroomId = 200, Name = "别的学期课", Term = 202501 }],
            };
            var r = new SyncService(api).SyncAsync(null).GetAwaiter().GetResult();
            Assert(r.CourseCount == 1, $"在册+归档重复时应只同步一次，实际 {r.CourseCount} 门");
            // 归档的 200 是别的学期，不进本次统计
            Assert(r.ArchivedCourses == 0, $"非当前学期的归档课不应计入：{r.ArchivedCourses}");
        });

        Check("issue#1 已读公告不被默认过滤", () =>
        {
            var list = new ViewModels.TodoListViewModel();
            var doneHw = new ViewModels.TodoItemViewModel(new TodoItem
            {
                Id = "hw-1-1", Kind = TodoKind.Homework, Title = "作业", Status = TodoStatus.NotStarted,
            }) { IsDone = true };
            var readAnn = new ViewModels.TodoItemViewModel(new TodoItem
            {
                Id = "ann-1-1", Kind = TodoKind.Announcement, Title = "公告", Status = TodoStatus.Unknown,
            }) { IsDone = true };
            list.Replace([doneHw, readAnn]);

            Assert(list.VisibleItems.Count == 1
                   && list.VisibleItems[0].Model.Kind == TodoKind.Announcement,
                "已读公告被「隐藏已勾选」过滤掉了");
            list.HideLocallyDone = false;
            Assert(list.VisibleItems.Count == 2, "关闭过滤后应全部可见");
        });

        Check("issue#1 Cookie 清理判定", () =>
        {
            Assert(new YktLoginService.CookieClearResult(0, 0, 0, null).Ok, "干净清除应判定成功");
            Assert(!new YktLoginService.CookieClearResult(0, 0, 0, "CookieManager 不可用").Ok,
                "带错误的 Remaining=0 不得判定成功");
            Assert(!new YktLoginService.CookieClearResult(1, 1, 0, null).Ok, "有残留不得判定成功");
        });

        Check("作业深链构造", () =>
        {
            // 试卷类（type=5）：用日志的 courseware_id 直达 /exam/{cid}/{examId}
            var api = new FakeYktApi(
                [Course(100, "课程A")],
                new Dictionary<long, List<YktActivity>>
                {
                    [100] =
                    [
                        JsonSerializer.Deserialize(
                            """{"id":1,"type":5,"title":"试卷作业","create_time":"1758000000000","courseware_id":"2144775"}""",
                            AppJsonContext.Default.YktActivity)!,
                        // 章节作业（type=19）：用 content 的 leaf_type_id + sku_id 直达 exercise 页
                        JsonSerializer.Deserialize(
                            """{"id":2,"type":19,"title":"章节作业","create_time":"1758000000000","content":{"leaf_id":48737438,"score_d":1790000000000,"leaf_type_id":3745003,"sku_id":12893715} }""",
                            AppJsonContext.Default.YktActivity)!,
                        // 缺直达字段：回落课程日志页
                        HwActivity(3, 9001, "缺字段作业"),
                    ],
                });
            var r = new SyncService(api).SyncAsync(null).GetAwaiter().GetResult();
            var byTitle = r.Homework.ToDictionary(h => h.Title);
            Assert(byTitle["试卷作业"].SourceUrl == "https://changjiang.yuketang.cn/v2/web/exam/100/2144775",
                $"试卷类深链不符：{byTitle["试卷作业"].SourceUrl}");
            Assert(byTitle["章节作业"].SourceUrl == "https://changjiang.yuketang.cn/v2/web/exercise/100/3745003/12893715",
                $"章节作业深链不符：{byTitle["章节作业"].SourceUrl}");
            Assert(byTitle["缺字段作业"].SourceUrl!.Contains("/v2/web/studentLog/100"),
                $"缺字段时应回落课程页：{byTitle["缺字段作业"].SourceUrl}");
        });

        // ---------------- 课表（教务系统导入） ----------------

        Check("课表：周次/节次/星期解析", () =>
        {
            // 周次：区间、多段、单双周、单周
            var w = ZhengfangParser.ParseWeekRange("1-16周");
            Assert(w.Count == 1 && w[0].StartWeek == 1 && w[0].EndWeek == 16 && w[0].Parity == WeekParity.Every,
                "「1-16周」应解析为一段 1-16 每周");

            // 关键分支：不连续周次要拆成两段，否则「第 9 周有没有课」会算错
            var multi = ZhengfangParser.ParseWeekRange("1-8,10-16周");
            Assert(multi.Count == 2, $"「1-8,10-16周」应拆为 2 段，实际 {multi.Count}");
            Assert(multi[0].StartWeek == 1 && multi[0].EndWeek == 8 && multi[1].StartWeek == 10 && multi[1].EndWeek == 16,
                "不连续周次的段边界不符");

            Assert(ZhengfangParser.ParseWeekRange("1-15周(单)")[0].Parity == WeekParity.Odd, "单周应识别为 Odd");
            Assert(ZhengfangParser.ParseWeekRange("2-14周（双）")[0].Parity == WeekParity.Even, "全角括号双周应识别为 Even");
            Assert(ZhengfangParser.ParseWeekRange("5周")[0] is { StartWeek: 5, EndWeek: 5 }, "「5周」应解析为单周 5-5");
            Assert(ZhengfangParser.ParseWeekRange("").Count == 0, "空串应返回空");
            Assert(ZhengfangParser.ParseWeekRange("不是周次").Count == 0, "无法识别的文本应返回空");

            // 节次：区间、带「第/节」、四位补零、单节
            Assert(ZhengfangParser.ParsePeriodLabel("1-2节") is { StartPeriod: 1, EndPeriod: 2 }, "「1-2节」解析不符");
            Assert(ZhengfangParser.ParsePeriodLabel("第3-4节") is { StartPeriod: 3, EndPeriod: 4 }, "「第3-4节」解析不符");
            Assert(ZhengfangParser.ParsePeriodLabel("0102") is { StartPeriod: 1, EndPeriod: 2 }, "四位补零「0102」应解析为 1-2");
            Assert(ZhengfangParser.ParsePeriodLabel("第3节") is { StartPeriod: 3, EndPeriod: 3 }, "「第3节」应解析为 3-3");
            Assert(ZhengfangParser.ParsePeriodLabel("0-2") is null, "起始节为 0 应判为非法");
            Assert(ZhengfangParser.ParsePeriodLabel("3-1") is null, "起始大于结束应判为非法");

            // 星期：数字优先，回退中文
            Assert(ZhengfangParser.ParseWeekday(3, null) == 3, "数字星期应直取");
            Assert(ZhengfangParser.ParseWeekday(null, "星期三") == 3, "「星期三」应解析为 3");
            Assert(ZhengfangParser.ParseWeekday(null, "周四") == 4, "「周四」应解析为 4");
            Assert(ZhengfangParser.ParseWeekday(null, "星期八") is null, "非法星期名应返回 null");
            Assert(ZhengfangParser.ParseWeekday(9, "星期一") == 1, "数字越界时应回退中文名");
        });

        Check("课表：HTML 实体与去标签", () =>
        {
            Assert(ZhengfangHtml.DecodeEntities("A&amp;B") == "A&B", "命名实体转义失败");
            Assert(ZhengfangHtml.DecodeEntities("&#65;&#x42;") == "AB", "数字实体（十进制/十六进制）转义失败");
            Assert(ZhengfangHtml.DecodeEntities("&nbsp;") == " ", "nbsp 应转为空格");
            Assert(ZhengfangHtml.DecodeEntities("&unknown;") == "&unknown;", "未知实体应原样保留");

            // 关键：单元格用 <br> 分隔「课程名/教师/周次/地点」，必须保留换行才能切块
            var stripped = ZhengfangHtml.StripTags("高等数学<br>张三<br>1-16周<br>教一楼101");
            var lines = stripped.Split('\n');
            Assert(lines.Length == 4, $"应保留 4 行，实际 {lines.Length}：{stripped}");
            Assert(lines[0] == "高等数学" && lines[3] == "教一楼101", "去标签后的行内容不符");

            // script/style 必须整段丢弃，否则会把页面脚本当成课程名
            var withScript = ZhengfangHtml.StripTags("<div>课程A</div><script>var x=1;</script><style>.a{}</style>");
            Assert(!withScript.Contains("var x"), "script 内容应被丢弃");
            Assert(!withScript.Contains(".a{"), "style 内容应被丢弃");
            Assert(withScript.Contains("课程A"), "正常文本应保留");
        });

        Check("课表：正方 kbList JSON 直取", () =>
        {
            // 照正方 xskbcx_cxXsKb 的字段写：kcmc/xm/cdmc/xqj/xqjmc/jcs/zcd
            const string json = """
                {"kbList":[
                  {"kcmc":"高等数学","xm":"张三","cdmc":"教一楼101","xqj":1,"xqjmc":"星期一","jcs":"1-2","zcd":"1-16周"},
                  {"kcmc":"大学物理","xm":"李四","cdmc":"理科楼203","xqj":"3","xqjmc":"星期三","jcs":"3-4","zcd":"1-8,10-16周"}
                ]}
                """;
            var parsed = ZhengfangParser.ParseSnapshot(json, 202601);

            // 第一条 1 段 + 第二条 2 段（不连续周次拆分）= 3 条
            Assert(parsed.Items.Count == 3, $"应解析出 3 条，实际 {parsed.Items.Count}（警告：{string.Join("；", parsed.Warnings)}）");
            Assert(parsed.Warnings.Count == 0, $"不应有警告：{string.Join("；", parsed.Warnings)}");

            var math = parsed.Items.First(i => i.Name == "高等数学");
            Assert(math is { Weekday: 1, StartPeriod: 1, EndPeriod: 2, StartWeek: 1, EndWeek: 16, Teacher: "张三", Location: "教一楼101" },
                "高等数学字段映射不符");
            // 时间由节次表推导：第 1 节 08:00 起、第 2 节 09:40 止
            Assert(math.StartTime == "08:00" && math.EndTime == "09:40",
                $"时间应由节次表推导，实际 {math.StartTime}-{math.EndTime}");

            var physics = parsed.Items.Where(i => i.Name == "大学物理").OrderBy(i => i.StartWeek).ToList();
            Assert(physics.Count == 2, "不连续周次应拆为 2 条");
            Assert(physics[0] is { StartWeek: 1, EndWeek: 8 } && physics[1] is { StartWeek: 10, EndWeek: 16 },
                "拆分后的周次段不符");
            Assert(physics.All(i => i.Weekday == 3), "星期用字符串「3」也应解析为 3");

            // 全角/缺字段的容错
            var bad = ZhengfangParser.ParseSnapshot("""{"kbList":[{"kcmc":"","xqj":1,"jcs":"1-2","zcd":"1周"}]}""", 1);
            Assert(bad.Items.Count == 0 && bad.Warnings.Count == 1, "课程名为空应记一条警告且不产出条目");
            var noKb = ZhengfangParser.ParseSnapshot("""{"result":"ok"}""", 1);
            Assert(noKb.Items.Count == 0 && noKb.Warnings.Any(x => x.Contains("kbList")),
                "JSON 无 kbList 时应明确说明");
        });

        Check("课表：列表视图 HTML 抽取", () =>
        {
            // 照正方 kblist_table 的列表视图结构写：tbody#xq_N 按星期分、tr 内 timetable_con 带标签
            const string html = """
                <table id="kblist_table">
                  <tbody id="xq_1">
                    <tr>
                      <td id="jc_1-1-2"><div class="festival">1-2</div></td>
                      <td><div class="timetable_con">
                        <span class="title">高等数学</span>
                        <p>周数：1-16</p><p>教师：张三</p><p>上课地点：教一楼101</p>
                      </div></td>
                    </tr>
                  </tbody>
                  <tbody id="xq_5">
                    <tr>
                      <td id="jc_5-5-6"><div class="festival">5-6</div></td>
                      <td><div class="timetable_con">
                        <span class="title">大学英语</span>
                        <p>周数：1-8,10-16</p><p>教师：王五</p><p>上课地点：外语楼305</p>
                      </div></td>
                    </tr>
                  </tbody>
                </table>
                """;
            var parsed = ZhengfangParser.ParseSnapshot(html, 202601);

            // 高等数学 1 段 + 大学英语 2 段 = 3
            Assert(parsed.Items.Count == 3, $"应解析出 3 条，实际 {parsed.Items.Count}（警告：{string.Join("；", parsed.Warnings)}）");

            var math = parsed.Items.First(i => i.Name == "高等数学");
            Assert(math.Weekday == 1 && math.StartPeriod == 1 && math.EndPeriod == 2,
                "星期/节次应来自 tbody#xq_1 与 id=jc_1-1-2");
            Assert(math.Teacher == "张三" && math.Location == "教一楼101", "教师/地点应取自标签字段");

            var english = parsed.Items.Where(i => i.Name == "大学英语").ToList();
            Assert(english.Count == 2 && english.All(i => i.Weekday == 5), "周五的英语课应拆为 2 段");
        });

        Check("课表：网格表格兜底", () =>
        {
            // 普通课表：表头含星期、首列节次、单元格内以 <br> 分行
            const string html = """
                <table>
                  <tr><th>节次</th><th>星期一</th><th>星期二</th><th>星期三</th></tr>
                  <tr><td>1-2</td>
                      <td>高等数学<br>张三<br>1-16周<br>教一楼101</td>
                      <td></td>
                      <td>大学物理<br>李四<br>1-8周<br>理科楼203</td></tr>
                </table>
                """;
            var parsed = ZhengfangParser.ParseSnapshot(html, 7);

            Assert(parsed.Items.Count == 2, $"应解析出 2 条，实际 {parsed.Items.Count}（警告：{string.Join("；", parsed.Warnings)}）");
            var math = parsed.Items.First(i => i.Name == "高等数学");
            // 节次来自首列，单元格内没有「节」行 → 回退到行首列
            Assert(math is { Weekday: 1, StartPeriod: 1, EndPeriod: 2, StartWeek: 1, EndWeek: 16 },
                "网格兜底的行首节次回退失败");
            Assert(math.Teacher == "张三" && math.Location == "教一楼101", "网格兜底的教师/地点顺序不符");
            Assert(parsed.Items.First(i => i.Name == "大学物理").Weekday == 3, "列→星期映射错误");

            // 无星期表头时必须明确报错，而不是静默产出空课表
            var noHeader = ZhengfangParser.ParseSnapshot("<table><tr><td>1-2</td><td>课程A</td></tr></table>", 1);
            Assert(noHeader.Items.Count == 0 && noHeader.Warnings.Any(w => w.Contains("星期表头")),
                "无星期表头时应给出明确警告");
        });

        Check("课表：节次时间表与周次展开", () =>
        {
            // 停用节次（开始与结束都是 00:00）不参与重叠校验
            var periods = Timetable.CreateDefaultPeriods();
            periods[0] = new PeriodSlot { Period = 1, StartTime = "00:00", EndTime = "00:00" };
            Timetable.ValidatePeriods(periods); // 不应抛
            Assert(Timetable.IsPeriodDisabled(periods[0]), "00:00-00:00 应判为停用节");
            Assert(Timetable.ResolvePeriodTime(periods, 1) is null, "停用节应取不到时间");
            Assert(Timetable.RangeIncludesDisabled(periods, 1, 2), "跨停用节的课块应被标记");

            // 节次越界 → 回退条目自带时间并标记，而不是静默给错时间
            var outOfRange = new ScheduleItem
            {
                Name = "越界课", Weekday = 1, StartPeriod = 13, EndPeriod = 14,
                StartWeek = 1, EndWeek = 1, StartTime = "07:00", EndTime = "07:45",
            };
            var resolved = Timetable.ResolveSessionTime(outOfRange, Timetable.DefaultPeriods);
            Assert(resolved.OutOfRange && resolved.StartTime == "07:00" && resolved.EndTime == "07:45",
                "节次越界时应回退条目自带时间并标记越界");

            // 非法节次表整体回退默认（宁可回默认，不可展示错乱时间）
            var bad = new List<PeriodSlot>
            {
                new() { Period = 1, StartTime = "08:00", EndTime = "09:00" },
                new() { Period = 2, StartTime = "08:30", EndTime = "09:30" }, // 与第 1 节重叠
            };
            var normalized = Timetable.NormalizePeriods(bad);
            Assert(normalized.Count == Timetable.DefaultPeriods.Count, "非法节次表应整体回退默认");

            // 周次换算：2026-09-07 是周一
            Assert(Timetable.ComputeWeekNumber("2026-09-07", "2026-09-07") == 1, "学期首周应算第 1 周");
            Assert(Timetable.ComputeWeekNumber("2026-09-14", "2026-09-07") == 2, "第二周应为 2");
            // 周日归上一周：09-13 是周日，仍属第 1 周
            Assert(Timetable.ComputeWeekNumber("2026-09-13", "2026-09-07") == 1, "周日应归上一周");
            // 基准不是周一时也要规整到周一
            Assert(Timetable.ComputeWeekNumber("2026-09-09", "2026-09-09") == 1, "基准为周三时当天应算第 1 周");
            Assert(Timetable.ComputeWeekNumber("2026-09-07", null) is null, "无基准应返回 null");

            // 展开：1-2 周的周一 1-2 节 → 落在 09-07 与 09-14
            var items = new List<ScheduleItem>
            {
                new()
                {
                    Id = "a", Name = "高数", Weekday = 1, StartPeriod = 1, EndPeriod = 2,
                    StartWeek = 1, EndWeek = 2, StartTime = "08:00", EndTime = "09:40", Term = 202601,
                },
            };
            var sessions = Timetable.Expand(items, "2026-09-07", Timetable.DefaultPeriods,
                fromDate: "2026-09-01", days: 20, term: 202601);
            Assert(sessions.Count == 2, $"应展开 2 次课，实际 {sessions.Count}");
            Assert(sessions[0].Date == "2026-09-07" && sessions[1].Date == "2026-09-14", "展开日期不符");
            Assert(sessions[0].StartTime == "08:00" && sessions[0].EndTime == "09:40", "展开时间应由节次表推导");

            // 单双周：1-4 周单周 → 只展开第 1、3 周
            var odd = new List<ScheduleItem>
            {
                new()
                {
                    Id = "b", Name = "单周课", Weekday = 1, StartPeriod = 1, EndPeriod = 2,
                    StartWeek = 1, EndWeek = 4, WeekParity = WeekParity.Odd,
                    StartTime = "08:00", EndTime = "09:40", Term = 1,
                },
            };
            var oddSessions = Timetable.Expand(odd, "2026-09-07", Timetable.DefaultPeriods,
                fromDate: "2026-09-01", days: 30, term: 1);
            Assert(oddSessions.Count == 2, $"单周应只展开 2 次，实际 {oddSessions.Count}");
            Assert(oddSessions.All(s => s.Date is "2026-09-07" or "2026-09-21"), "单周展开日期不符");

            // 按周过滤：第 2 周只应有每周课，不应有单周课
            var week2 = Timetable.FilterForWeek(items, 2, 202601);
            Assert(week2.Count == 1 && week2[0].Name == "高数", "按周过滤（每周）不符");
            Assert(Timetable.FilterForWeek(odd, 2, 1).Count == 0, "第 2 周不应有单周课");
            Assert(Timetable.FilterForWeek(odd, 3, 1).Count == 1, "第 3 周应有单周课");
        });

        Check("课表：真实页面误判防护（课程代码/学时/合并单元格）", () =>
        {
            // ---- Bug 1：课程代码被当成周次 ----
            // 真实单元格含 `(2026-2027-1)-B32211`，宽松匹配会得到「第 2026-2027 周」，
            // 进而让分块逻辑在错误的行上切开（实测：整格被切成两块，课程全部解析失败）。
            Assert(ZhengfangParser.ParseWeekRange("(2026-2027-1)-B32211").Count == 0,
                "课程代码不应被当成周次");
            Assert(ZhengfangParser.ParseWeekRange("81-01C").Count == 0, "教学班号不应被当成周次");
            Assert(ZhengfangParser.ParseWeekRange("10-06").Count == 0, "学号片段不应被当成周次");
            Assert(ZhengfangParser.ParseWeekRange("(2026-2027-1)-B03210").Count == 0,
                "课程代码（另一形态）不应被当成周次");

            // ---- Bug 2：学时学分被当成周次 ----
            // `理论:42,实践:3` 按逗号切分后，`实践:3` 里的 3 曾被当成「第 3 周」。
            Assert(ZhengfangParser.ParseWeekRange("理论:42,实践:3").Count == 0,
                "「理论:42,实践:3」不应被当成周次");
            Assert(ZhengfangParser.ParseWeekRange("3.0").Count == 0, "绩点不应被当成周次");
            Assert(ZhengfangParser.ParseWeekRange("42").Count == 0, "孤立数字不应被当成周次");
            Assert(ZhengfangParser.ParseWeekRange("考试").Count == 0, "文字不应被当成周次");

            // 严格化之后合法写法必须仍然全部识别（不能为了排除误判而漏掉正常数据）
            Assert(ZhengfangParser.ParseWeekRange("1-14周").Count == 1, "普通区间应识别");
            Assert(ZhengfangParser.ParseWeekRange("(1-2节)1-14周").Count == 1,
                "节次与周次同行时应能取出周次");
            var odd = ZhengfangParser.ParseWeekRange("(3-4节)1-15周(单)");
            Assert(odd is { Count: 1 } && odd[0].Parity == WeekParity.Odd,
                "尾部括号里的单周标注应识别（真实写法是 (3-4节)1-15周(单)）");
            Assert(ZhengfangParser.ParseWeekRange("(1-2节)13-16周")[0] is { StartWeek: 13, EndWeek: 16 },
                "节次与周次同行时周次范围应正确");
            Assert(ZhengfangParser.ParseWeekRange("1-8,10-16周").Count == 2, "多段周次应识别");
            Assert(ZhengfangParser.ParseWeekRange("11周")[0] is { StartWeek: 11, EndWeek: 11 },
                "单周写法应识别");

            // ---- Bug 3：节次被课程代码干扰 ----
            Assert(ZhengfangParser.ParsePeriodLabel("(2026-2027-1)-B32211") is null,
                "课程代码不应被当成节次");
            Assert(ZhengfangParser.ParsePeriodLabel("1-2节") is { StartPeriod: 1, EndPeriod: 2 },
                "普通节次应识别");
            // 节次与周次同行时必须靠 ExtractPeriodFromMixed：
            // ParsePeriodLabel 要求「整串就是节次」，对这种组合文本应老实返回 null。
            Assert(ZhengfangParser.ParsePeriodLabel("(1-2节)1-14周") is null,
                "「(1-2节)1-14周」整体不是纯节次，ParsePeriodLabel 应返回 null");
            Assert(ZhengfangParser.ExtractPeriodFromMixed("(1-2节)1-14周") is { StartPeriod: 1, EndPeriod: 2 },
                "应从混合文本里取出节次 1-2");
            Assert(ZhengfangParser.ExtractPeriodFromMixed("(3-4节)1-15周(单)") is { StartPeriod: 3, EndPeriod: 4 },
                "应从混合文本里取出节次 3-4");

            // ---- Bug 4：富单元格不应被切碎 ----
            // 真实单元格约 12 行（含学号/班级/考试类型/学时学分），原分块逻辑会切成两块。
            const string rich = "中国近现代史纲要★\n(1-2节)1-14周\n珠海 明德楼阶梯B107\n郑雨\n"
                                + "(2026-2027-1)-B32211\n10-06\n自动化2507-08,通信2501-03\n考试\n"
                                + "理论:42,实践:3\n3\n42\n3.0";
            var blocks = ZhengfangParser.SplitCellBlocks(rich);
            Assert(blocks.Count == 1, $"富单元格应切成 1 块，实际 {blocks.Count}（附加行被误当成新课）");
            Assert(blocks[0].Count == 12, $"单块应保留全部 12 行，实际 {blocks[0].Count}");

            // 真的有两门课时仍要能切开
            var twoBlocks = ZhengfangParser.SplitCellBlocks(
                rich + "\n大学英语BIII★\n(3-4节)1-16周\n珠海 明德楼小教A313\n莫飘");
            Assert(twoBlocks.Count == 2, $"两门课应切成 2 块，实际 {twoBlocks.Count}");

            // ---- Bug 5：rowspan 必须展开 ----
            // 真实课表用 rowspan 做布局（「上午」占两行、节次合并）；
            // 不展开会让后续行列号整体前移，把课挂到错误的星期上。
            const string merged = """
                <table>
                  <tr><th>时间段</th><th>节次</th><th>星期一</th><th>星期二</th></tr>
                  <tr><td rowspan="2">上午</td><td rowspan="2">1</td>
                      <td>高等数学<br>(1-2节)1-16周<br>教一楼101<br>张三</td>
                      <td>大学物理<br>(1-2节)1-8周<br>理科楼203<br>李四</td></tr>
                  <tr><td>班级A</td><td>班级B</td></tr>
                  <tr><td></td><td>3</td>
                      <td>大学英语<br>(3-4节)1-16周<br>外语楼305<br>王五</td>
                      <td>数据结构<br>(3-4节)1-8周<br>信息楼401<br>赵六</td></tr>
                </table>
                """;
            var mergedRows = ZhengfangHtml.ExtractTableRows(merged);
            Assert(mergedRows.Count == 1, "应解析出 1 张表");
            Assert(mergedRows[0].Count == 4, $"应解析出 4 行，实际 {mergedRows[0].Count}");
            var dataRow = mergedRows[0][2];
            Assert(dataRow.Count == 4, $"数据行应有 4 列，实际 {dataRow.Count}");
            Assert(dataRow[0] == string.Empty && dataRow[1] == string.Empty,
                "被 rowspan 覆盖的列应填空白占位，否则列号会前移");

            var mergedParsed = ZhengfangParser.ParseSnapshot(merged, 1);
            var math = mergedParsed.Items.FirstOrDefault(i => i.Name == "高等数学");
            var english = mergedParsed.Items.FirstOrDefault(i => i.Name == "大学英语");
            Assert(math is { Weekday: 1, StartPeriod: 1, EndPeriod: 2 }, "高数应在周一 1-2 节");
            Assert(english is { Weekday: 1, StartPeriod: 3, EndPeriod: 4 },
                "英语应在周一 3-4 节（rowspan 未展开时会错位到其它星期）");
            var ds = mergedParsed.Items.FirstOrDefault(i => i.Name == "数据结构");
            Assert(ds is { Weekday: 2, StartPeriod: 3, EndPeriod: 4 }, "数据结构应在周二 3-4 节");

            // 富单元格（含学号/班级/考试类型/学时学分）必须解析出正确的**节次**。
            // 曾经因为块内把纯数字附加行（学时里的 3）当成节次行占位，
            // 导致全表节次都错成同一个值——这是真实页面上课程全部解析失败的第二个根因。
            Assert(math is { StartPeriod: 1, EndPeriod: 2 },
                "高数节次必须取自单元格的「(1-2节)」，不能被附加的纯数字行覆盖");
            Assert(english is { StartPeriod: 3, EndPeriod: 4 },
                "英语节次必须取自单元格的「(3-4节)」");
            // 课程格**不得**产生警告——这才是回归保护点。
            // 填充格（班级名之类，本就没有课程）产生警告是正常的，故不断言总数为 0。
            foreach (var courseName in new[] { "高等数学", "大学物理", "大学英语", "数据结构" })
            {
                Assert(!mergedParsed.Warnings.Any(w => w.Contains(courseName, StringComparison.Ordinal)),
                    $"课程「{courseName}」不应产生解析警告，实际：{string.Join("；", mergedParsed.Warnings)}");
            }
        });

        Check("课表：真实页面 + InvokeScript JSON 壳（抓取不到课表的根因）", () =>
        {
            // 根因回放：WebView 的 InvokeScript 会把脚本返回值**序列化成 JSON 字符串**交给 C#，
            // 而它自己的脚本外壳就是一层 JSON.stringify —— 于是抓到的整页 HTML 变成
            // 「满篇 \u003C、一个尖括号都没有」的字符串，三条解析路径全部落空，
            // 而且**连警告都没有**（那些标记压根没进解析器）。
            // 真机快照实测：179KB 里 <html 出现 0 次、字面 \u003C 出现 5601 次。
            // 真机实测的原始返回形态（--jwcheck 探针）：
            //   脚本 return '<div class="x">中</div>'
            //     -> C# 拿到 "\u003Cdiv class=\"x\">中\u003C/div>"   （一层 JSON 字符串壳）
            // 片段按真实页面结构写：列表视图要求课块自带「周数：」标签，节次来自 td 的 jc_ 或 festival。
            const string realSnippet =
                "<table id=\"kblist_table\"><tbody id=\"xq_1\">"
                + "<tr><td id=\"jc_1-1-2\"><span class=\"festival\">1-2</span></td>"
                + "<td><div class=\"timetable_con text-left\"><span class=\"title\">高等数学★</span>"
                + "<p><span class=\"glyphicon glyphicon-calendar\"></span> 周数：1-16周</p>"
                + "<p><span class=\"glyphicon glyphicon-map-marker\"></span> 上课地点：明德楼B107</p>"
                + "<p><span class=\"glyphicon glyphicon-user\"></span> 教师 ：张三</p></div></td></tr>"
                + "</tbody></table>";

            // 真机同款的一层 JSON 壳
            var wrapped = JsonSerializer.Serialize(realSnippet);
            Assert(wrapped.StartsWith("\"\\u003Ctable", StringComparison.Ordinal),
                $"构造的壳应形如 \"\\u003Ctable…\"，实际 {wrapped[..Math.Min(24, wrapped.Length)]}");

            var fromWrapped = ZhengfangParser.ParseSnapshot(wrapped, 202601);
            var fromBare = ZhengfangParser.ParseSnapshot(realSnippet, 202601);
            Assert(fromBare.Items.Count > 0, "裸 HTML 应能解析出课程（作为对照）");
            Assert(fromWrapped.Items.Count == fromBare.Items.Count,
                $"带 JSON 壳的文本必须拆壳后再解析，否则一条课都读不出来"
                + $"（裸 HTML {fromBare.Items.Count} 条 vs 带壳 {fromWrapped.Items.Count} 条）");
            Assert(fromWrapped.Items.Any(i => i.Name.Contains("高等数学", StringComparison.Ordinal)),
                "拆壳后应能解析出课程名");

            // 真机抓下来的完整课表页片段（珠海科技学院正方系统，2026-10-16 实测抓取）
            var fixture = Path.Combine(AppContext.BaseDirectory, "fixtures", "zcst-kblist-table.html");
            if (!File.Exists(fixture))
            {
                fixture = Path.Combine("docs", "fixtures", "zcst-kblist-table.html");
            }

            if (File.Exists(fixture))
            {
                var realHtml = File.ReadAllText(fixture);
                // 样本里 23 个课块（真实课表，2026-10-16 抓取）。
                var courseCount = Regex.Matches(realHtml, "<span class=\"title\">").Count;
                Assert(courseCount >= 20, $"真实样本应含 20 门以上课程，实际 {courseCount}");

                // 直接喂裸 HTML
                var direct = ZhengfangParser.ParseSnapshot(realHtml, 202601);
                Assert(direct.Items.Count >= courseCount,
                    $"真实页面裸 HTML 应解析出 {courseCount} 条以上（不连续周次会拆多条），"
                    + $"实际 {direct.Items.Count}（警告：{string.Join("；", direct.Warnings)}）");
                Assert(direct.Warnings.Count == 0,
                    $"真实课表页不应产生解析警告，实际：{string.Join("；", direct.Warnings)}");

                // 喂真机同款的 JSON 壳（**这就是线上真正发生的事情**）：
                // 修复前这里会得到 0 条，因为整页 HTML 是一个 \u003C 满篇的字符串。
                var shielded = ZhengfangParser.ParseSnapshot(JsonSerializer.Serialize(realHtml), 202601);
                Assert(shielded.Items.Count == direct.Items.Count,
                    $"真实页面套上 JSON 壳后应与裸 HTML 得到同样结果，"
                    + $"实际 {shielded.Items.Count} vs {direct.Items.Count}");

                // 抽查课程落在正确的「星期 × 节次」上，确认不是「数量对但内容错」
                foreach (var (name, weekday, startPeriod) in new (string, int, int)[]
                {
                    ("中国近现代史纲要", 1, 1),
                })
                {
                    var hit = shielded.Items.FirstOrDefault(i => i.Name.Contains(name, StringComparison.Ordinal));
                    Assert(hit is not null, $"真实页面应解析出「{name}」");
                    Assert(hit!.Weekday == weekday && hit.StartPeriod == startPeriod,
                        $"「{name}」应在星期 {weekday} 第 {startPeriod} 节，实际星期 {hit.Weekday} 第 {hit.StartPeriod} 节");
                }

                // 周一到周五都要有课，避免「只解析出第一列」
                var weekdays = shielded.Items.Select(i => i.Weekday).Distinct().OrderBy(x => x).ToList();
                Assert(weekdays.Count >= 5, $"真实课表应覆盖 5 天以上，实际 {weekdays.Count} 天（{string.Join(",", weekdays)}）");

                // 课程名不应带上「周数：」这类标签（标签与标题必须分开取）
                Assert(shielded.Items.All(i => !i.Name.Contains("周数", StringComparison.Ordinal)
                                               && !i.Name.Contains("教师", StringComparison.Ordinal)),
                    "课程名不应混入「周数/教师」等标签文本");

                // ---- 全链路复刻：抓取脚本 → 框架外壳 → 拆壳 → 落盘 → 解析 ----
                // 这一段刻意把**线上真实经过的每一层**都跑一遍，任一层改动破坏链路都会在这里失败：
                //   ① 快照脚本从整页 HTML 里取出 outerHTML
                //   ② 框架把返回值序列化成 JSON 字符串（真机实测行为）
                //   ③ JwCaptureService 拆壳（本次修复点）→ 落盘
                //   ④ 解析器读落盘文件
                // 修复前 ①→③ 之间少了一层：落盘的是 \u003C 满篇的字符串，④ 必然 0 条。
                var captureScriptMatch = Regex.Match(
                    JwCaptureService.BuildCaptureScript(), @"outerHTML");
                Assert(captureScriptMatch.Success, "抓取脚本应读取 documentElement.outerHTML");

                var asFrameworkReturns = JsonSerializer.Serialize(realHtml);   // ②
                var asServiceReturns = JwCaptureService.UnwrapScriptResult(asFrameworkReturns);  // ③
                Assert(asServiceReturns == realHtml,
                    "拆壳结果应与脚本真正返回的整页 HTML 完全一致（含引号与换行）");

                var savedPath = Path.Combine(Path.GetTempPath(), "hr-jw-selftest-snapshot.html");
                File.WriteAllText(savedPath, asServiceReturns!);
                var fromFile = ZhengfangParser.ParseSnapshot(File.ReadAllText(savedPath), 202601);
                Assert(fromFile.Items.Count == direct.Items.Count,
                    $"落盘后再解析应与直读一致，实际 {fromFile.Items.Count} vs {direct.Items.Count}");
                try { File.Delete(savedPath); } catch (IOException) { /* 清理失败不影响结论 */ }
            }
        });

        Check("课表：时间轴几何（跨节次占高、分栏、行列一致）", () =>
        {
            // 核心不变量：课块必须按**真实节次跨度**占高，否则「1-2 节」的课只占一行，
            // 第 2 节的位置留一个空洞，整张表看起来是断的。
            const double rowH = TimetableLayout.PeriodRowHeight;
            const double gap = TimetableLayout.RowGap;

            Assert(Math.Abs(TimetableLayout.RowTop(1) - 0) < 0.01, "第 1 节行顶应为 0");
            Assert(Math.Abs(TimetableLayout.RowTop(2) - (rowH + gap)) < 0.01,
                $"第 2 节行顶应为 {rowH + gap}（行高 + 行距），实际 {TimetableLayout.RowTop(2)}");

            // 单节块高 == 行高；两节块高 == 两行 + 中间那一个间距
            Assert(Math.Abs(TimetableLayout.RowSpanHeight(1) - rowH) < 0.01, "单节块高应等于行高");
            Assert(Math.Abs(TimetableLayout.RowSpanHeight(2) - (2 * rowH + gap)) < 0.01,
                "两节块高应为 2 行 + 1 个间距");

            // 关键不变量：跨节次的块**不能压到下一行的起点**，
            // 与下一节课之间的空隙也不该超过一个行距（否则时间轴就断了）。
            var block12 = TimetableLayout.Measure(1, 1, 2, 700);
            var row3Top = TimetableLayout.RowTop(3);
            Assert(Math.Abs(block12.Y - 0) < 0.01, "1-2 节的块应从顶部开始");
            Assert(block12.Y + block12.Height <= row3Top + 0.01,
                $"1-2 节的块底({block12.Y + block12.Height})不得越过第 3 节行顶({row3Top})");
            Assert(row3Top - (block12.Y + block12.Height) <= gap + 0.01,
                "1-2 节的块与第 3 节之间最多只应留一个行距");

            // 同一行里相邻的两节课各自占满行高，且首尾相接（中间只有行距）
            var block34 = TimetableLayout.Measure(1, 3, 4, 700);
            Assert(Math.Abs(block34.Y - row3Top) < 0.01, "3-4 节的块应从第 3 节行顶开始");
            Assert(block34.Y - (block12.Y + block12.Height) <= gap + 0.01,
                "连续两节课之间不应超过一个行距的空隙");

            // 7 天等宽：每天宽度 = (总宽 - 6 个列间距) / 7
            var seven = TimetableLayout.Measure(1, 1, 1, 700, 0, 1, 7);
            Assert(Math.Abs(seven.Width - (700 - 6 * TimetableLayout.ColumnGap) / 7) < 0.01,
                $"每天宽度应为 (700-6*间距)/7，实际 {seven.Width}");

            // 只显示工作日时列更宽（不是简单裁掉周六周日）
            var five = TimetableLayout.Measure(1, 1, 1, 700, 0, 1, 5);
            Assert(five.Width > seven.Width, "只显示 5 天时每天的宽度应更大");
            Assert(Math.Abs(five.Width - (700 - 4 * TimetableLayout.ColumnGap) / 5) < 0.01,
                "5 天模式宽度应为 (700-4*间距)/5");

            // 星期越界要被夹住（数据脏了也不能画出界）
            var clamped = TimetableLayout.Measure(9, 1, 1, 700, 0, 1, 7);
            Assert(clamped.X <= 700, "星期越界应被夹到最后一列，不能画到区域外");

            // ---- 同格多门课分栏 ----
            // 两门课节次重叠 → 各占一列；不重叠 → 都占满宽
            var overlap = TimetableLayout.AssignColumns([(1, 2), (2, 3)]);
            Assert(overlap.Length == 2 && overlap[0].ColumnCount == 2 && overlap[1].ColumnCount == 2,
                "节次重叠的两门课应分成 2 栏");
            Assert(overlap[0].Column != overlap[1].Column, "重叠的两门课不应分到同一栏");

            var disjoint = TimetableLayout.AssignColumns([(1, 2), (5, 6)]);
            Assert(disjoint[0].ColumnCount == 1 && disjoint[1].ColumnCount == 1,
                "节次不重叠的课应各占满宽，不该无谓分栏");

            var three = TimetableLayout.AssignColumns([(1, 4), (2, 3), (3, 5)]);
            Assert(three.All(t => t.ColumnCount == 3), "三门互相重叠的课应分成 3 栏");
            Assert(three.Select(t => t.Column).Distinct().Count() == 3, "三门重叠的课应占三个不同栏");

            // 分栏后左右两块的 X 必须真的分开且不越界
            var left = TimetableLayout.Measure(1, 1, 2, 700, 0, 2);
            var right = TimetableLayout.Measure(1, 1, 2, 700, 1, 2);
            Assert(right.X > left.X + left.Width - 0.01, "分栏后右栏应在左栏右侧且不重叠");
            Assert(right.X + right.Width <= seven.X + seven.Width + 0.01, "分栏后右栏不应越出当天区域");

            // 停用节次行不应被画出来（末尾连续的停用行整段砍掉）
            Assert(TimetableLayout.VisibleRowCount(4, 12, _ => false) == 10,
                "课少时也应至少画 10 行（只画 4 行会像出错）");
            Assert(TimetableLayout.VisibleRowCount(14, 12, _ => false) == 12, "行数不能超过节次表长度");
            Assert(TimetableLayout.VisibleRowCount(12, 12, p => p >= 11) == 10,
                "末尾停用的节次行不应画出来");

            // ---- 分栏后仍要可读：栏太窄就让整张网格横向滚动 ----
            // 实测教训：4 门课挤在同一天同一节时，若强行把天宽切成 4 栏，
            // 每栏不到 30px，课程名断成单字、地点全被截掉，等于没显示。
            var wide = TimetableLayout.RequiredDaysWidth(846, 7, 1);
            Assert(Math.Abs(wide - 846) < 1, $"无冲突时应正好铺满可用宽度，实际 {wide}");

            var narrow = TimetableLayout.RequiredDaysWidth(846, 7, 4);
            Assert(narrow > wide, "分栏多时应撑宽网格（交给横向滚动），而不是把每栏压窄");
            var perColumn = (narrow - 6 * TimetableLayout.ColumnGap) / 7 / 4;
            Assert(perColumn >= TimetableLayout.MinColumnWidth - 0.01,
                $"分 4 栏后每栏应不小于 {TimetableLayout.MinColumnWidth}px，实际 {perColumn:0.#}");

            // 可用宽度很小（窄窗口）时也要保证单栏可读
            var tiny = TimetableLayout.RequiredDaysWidth(200, 7, 1);
            Assert(tiny >= 7 * TimetableLayout.MinDayWidth, $"窄窗口下单天不应小于 {TimetableLayout.MinDayWidth}px，实际 {tiny}");
        });

        Check("课表：课程配色稳定且不串色", () =>
        {
            // 同一门课必须永远同色（含跨次运行）：这里断言的是「同输入同输出」，
            // 且**不依赖 string.GetHashCode()**（后者每次进程启动加盐，会让颜色天天变）。
            Assert(CourseColor.IndexOf("高等数学") == CourseColor.IndexOf("高等数学"),
                "同一课程名必须取到同一个调色板下标");
            Assert(CourseColor.IndexOf("高等数学") == CourseColor.IndexOf(" 高等数学 "),
                "课程名首尾空白不应影响取色（否则导入前后会换色）");
            Assert(CourseColor.IndexOf("") == CourseColor.IndexOf(null),
                "空课程名应走同一个占位色");
            Assert(CourseColor.IndexOf("高等数学") is >= 0 and < CourseColor.PaletteSize,
                "取色下标必须在调色板范围内");

            // 常见课程名应分散到多个颜色，避免「一整屏全一个色」
            var names = new[] { "高等数学", "大学英语", "程序设计基础", "大学物理", "体育",
                                "中国近现代史纲要", "复变函数与积分变换", "概率论与数理统计B",
                                "人工智能与法律", "大学生劳动教育" };
            var used = names.Select(CourseColor.IndexOf).Distinct().Count();
            Assert(used >= 5, $"10 门课至少应落到 5 种颜色，实际 {used} 种");

            // 两套主题的调色板都要齐全，且颜色格式合法（视图直接把它们当画刷用）
            foreach (var dark in new[] { false, true })
            {
                var palette = CourseColor.All(dark);
                Assert(palette.Count == CourseColor.PaletteSize,
                    $"{(dark ? "深色" : "浅色")}调色板应有 {CourseColor.PaletteSize} 项，实际 {palette.Count}");
                foreach (var p in palette)
                {
                    Assert(IsHexColor(p.Fill) && IsHexColor(p.Line) && IsHexColor(p.Text),
                        $"{(dark ? "深色" : "浅色")}调色板含非法颜色：{p.Fill}/{p.Line}/{p.Text}");
                }

                // 深浅两套不能是同一批颜色（深色主题下淡底色会糊在白字上）
                Assert(!palette.Zip(CourseColor.All(!dark)).Any(pair => pair.First.Fill == pair.Second.Fill),
                    "深色与浅色主题的课块底色不应完全相同");
            }
        });

        Check("课表：周视图投影（跨节次块、本周判定、非本周弱化）", () =>
        {
            // 2026-2027 学年第 1 学期：第 1 周周一 = 2026-09-07（周一）
            const string termStart = "2026-09-07";
            var periods = Timetable.CreateDefaultPeriods();

            var items = new List<ScheduleItem>
            {
                new() { Id = "a", Name = "高等数学", Weekday = 1, StartPeriod = 1, EndPeriod = 2,
                        StartWeek = 1, EndWeek = 16, Location = "明德楼B107", Teacher = "张三" },
                new() { Id = "b", Name = "大学英语", Weekday = 1, StartPeriod = 3, EndPeriod = 4,
                        StartWeek = 1, EndWeek = 16 },
                new() { Id = "c", Name = "单周实验", Weekday = 2, StartPeriod = 5, EndPeriod = 6,
                        StartWeek = 1, EndWeek = 15, WeekParity = WeekParity.Odd },
            };

            var vm = new TimetableViewModels { TodayDate = "2026-09-07" };   // 第 1 周周一
            vm.GridWidth = 900;
            vm.Rebuild(items, 1, termStart, periods, darkTheme: false);

            // 第 1 周：三门课都上（单周课在第 1 周也应上）
            Assert(vm.BlockCount == 3, $"第 1 周应有 3 门课，实际 {vm.BlockCount}");
            Assert(vm.Blocks.All(b => b.IsCurrentWeek), "第 1 周的课都应标记为本周课");

            // 跨节次：1-2 节的块高必须等于两行 + 一个间距（这正是本次修掉的缺陷）
            var math = vm.Blocks.First(b => b.Name == "高等数学");
            var expected = TimetableLayout.RowSpanHeight(2);
            Assert(Math.Abs(math.Height - expected) < 0.01,
                $"1-2 节的课块应高 {expected}（两行），实际 {math.Height}");

            // 同一天相邻：英语块应正好排在数学块下方（相隔不超过一个行距，不重叠）
            var english = vm.Blocks.First(b => b.Name == "大学英语");
            Assert(english.Y >= math.Y + math.Height - 0.01,
                $"3-4 节的课不应压住 1-2 节，实际 Y={english.Y}，前一块底部={math.Y + math.Height}");
            Assert(english.Y - (math.Y + math.Height) <= TimetableLayout.RowGap + 0.01,
                $"3-4 节的课应紧跟 1-2 节之后（最多一个行距），实际间隙={english.Y - (math.Y + math.Height)}");

            // 节次刻度的 Top 必须与课块坐标用**同一套**行距公式。
            // 刻度与课块各自摆在自己的画布里，两边只要有一处用了别的行距、或者
            // 被容器多推了一个表头的高度差，整张表就会整体错位
            //（实测：课块比刻度低 12.8px，因为两个表头各自成行、高度不同）。
            Assert(vm.Periods.Count > 0, "应至少有一个节次刻度");
            for (var i = 0; i < vm.Periods.Count; i++)
            {
                Assert(Math.Abs(vm.Periods[i].Top - TimetableLayout.RowTop(i + 1)) < 0.01,
                    $"第 {i + 1} 节刻度的 Top 应为 {TimetableLayout.RowTop(i + 1)}，实际 {vm.Periods[i].Top}");
            }
            Assert(Math.Abs(vm.Periods[^1].Top + TimetableLayout.PeriodRowHeight - vm.GridHeight) < 0.01,
                "最后一个节次刻度的底部应与网格高度一致（刻度不能比课块区高或矮）");

            // 今天是第 1 周周一 → 周一列头应高亮，且显示的是本周
            Assert(vm.IsCurrentWeekDisplayed, "显示第 1 周且今天在第 1 周时，应标记为「本周」");
            Assert(vm.DayHeaders[0].IsToday, "今天（周一）应高亮");
            Assert(!vm.DayHeaders[1].IsToday, "周二不应高亮");
            Assert(vm.DayHeaders[0].HasDate, "配置了学期基准后列头应有日期");

            // 第 2 周：单周课不上 → 只剩 2 门；且「下一节课」提示不应再出现（不是本周）
            vm.Rebuild(items, 2, termStart, periods, darkTheme: false);
            Assert(vm.BlockCount == 2, $"第 2 周单周课不上，应剩 2 门，实际 {vm.BlockCount}");
            Assert(!vm.IsCurrentWeekDisplayed, "显示第 2 周时不应标记为「本周」");
            Assert(vm.DayHeaders.All(d => !d.IsToday), "非本周不应高亮任何一天");
            Assert(vm.NextClassText.Length == 0, "非本周不应显示「下一节课」");

            // 打开「显示非本周」→ 单周课要重新出现，但被标记为非本周
            vm.ShowOtherWeeks = true;
            vm.Rebuild(items, 2, termStart, periods, darkTheme: false);
            var odd = vm.Blocks.FirstOrDefault(b => b.Name == "单周实验");
            Assert(odd is not null, "打开「显示非本周」后单周课应出现");
            Assert(odd!.IsOtherWeek && !odd.IsCurrentWeek, "单周课在第 2 周应标记为非本周");
            Assert(odd.WeekBadge.Length > 0, "非本周课块应标注实际周次，避免看起来像排错了");
            Assert(vm.BlockCount == 2, $"「本周课程数」不应把非本周的课算进去，实际 {vm.BlockCount}");

            // 隐藏周末 → 列头只剩 5 天
            vm.ShowWeekend = false;
            vm.Rebuild(items, 1, termStart, periods, darkTheme: false);
            Assert(vm.DayHeaders.Count == 5, $"隐藏周末后应只有 5 个列头，实际 {vm.DayHeaders.Count}");
            vm.ShowWeekend = true;

            // 「下一节课」：今天（周一）上午 8 点前，第一节课就应在提示里
            var probe = new TimetableViewModels { TodayDate = "2026-09-07" };
            probe.GridWidth = 900;
            probe.Rebuild(items, 1, termStart, periods, darkTheme: false);
            Assert(probe.HasNextClass == probe.NextClassText.Length > 0,
                "HasNextClass 必须与文案一致");
            Assert(probe.NextClassText.Contains("高等数学", StringComparison.Ordinal)
                   || probe.NextClassText.Contains("下一节", StringComparison.Ordinal)
                   || probe.NextClassText.Contains("明天", StringComparison.Ordinal),
                $"「下一节课」文案应指向具体课程，实际：{probe.NextClassText}");

            // 未配置学期基准：不编造日期，但仍要画出课块
            var noAnchor = new TimetableViewModels { TodayDate = "2026-09-07" };
            noAnchor.GridWidth = 900;
            noAnchor.Rebuild(items, 1, null, periods, darkTheme: false);
            Assert(noAnchor.BlockCount == 3, "没有学期基准时仍应画出本周课块");
            Assert(noAnchor.DayHeaders.All(d => !d.HasDate), "没有学期基准时不应显示日期");
            Assert(!noAnchor.IsCurrentWeekDisplayed, "没有学期基准时无法判定今天在第几周");
        });

        Check("课表：周次变化必须触发重建（箭头点了要有反应）", () =>
        {
            // 这条是回归保护：「周次变了、课表没变」看着像按钮坏了，实际是漏了一个
            // OnWeekNumberChanged 钩子——WeekNumber 从 7 一路加到 11，WeekLabel 还停在「第 7 周」。
            const string termStart = "2026-09-07";
            var periods = Timetable.CreateDefaultPeriods();
            var items = new List<ScheduleItem>
            {
                new() { Id = "a", Name = "高等数学", Weekday = 1, StartPeriod = 1, EndPeriod = 2,
                        StartWeek = 1, EndWeek = 16 },
            };

            var vm = new TimetableViewModels { TodayDate = "2026-09-07" };
            vm.GridWidth = 900;

            var rebuilds = 0;
            vm.RebuildRequested = () =>
            {
                rebuilds++;
                vm.Rebuild(items, vm.WeekNumber, termStart, periods, darkTheme: false);
            };

            vm.Rebuild(items, 1, termStart, periods, darkTheme: false);
            rebuilds = 0;

            // 改周次 = 模拟点「下一周」按钮（MainViewModel.ShiftTimetableWeek 只做这一件事）
            vm.WeekNumber = 2;
            Assert(rebuilds == 1, $"改周次应触发恰好一次重建，实际 {rebuilds} 次");
            Assert(vm.WeekLabel.Contains("第 2 周", StringComparison.Ordinal),
                $"周次文案应跟着变，实际「{vm.WeekLabel}」");

            vm.WeekNumber = 3;
            Assert(rebuilds == 2, $"连续改周次每次都该重建，实际 {rebuilds} 次");
            Assert(vm.WeekLabel.Contains("第 3 周", StringComparison.Ordinal),
                $"周次文案应跟着变，实际「{vm.WeekLabel}」");

            // 每个会改变网格外观的开关都必须触发重建，否则界面会与数据不一致
            foreach (var (name, toggle) in new (string, Action)[]
            {
                ("周末开关", () => vm.ShowWeekend = false),
                ("显示非本周", () => vm.ShowOtherWeeks = true),
                ("网格宽度", () => vm.GridWidth = 700),
            })
            {
                var beforeCount = rebuilds;
                toggle();
                Assert(rebuilds == beforeCount + 1, $"「{name}」变化应触发重建，实际没有");
            }
        });

        Check("课表：节次时间表校验（保存时必须拦住非法时间）", () =>
        {
            // 内置默认表本身必须合法——它是「恢复默认」的落点，也是回退值。
            Timetable.ValidatePeriods(Timetable.CreateDefaultPeriods());

            static PeriodSlot Slot(int period, string start, string end) =>
                new() { Period = period, StartTime = start, EndTime = end };

            // 每条非法输入都必须抛出**带具体原因**的异常，而不是静默接受或静默改回默认：
            // 写坏时间表会让整张课表、待办提醒的时间全错。
            var cases = new (string Name, List<PeriodSlot> Periods)[]
            {
                ("序号不从 1 起", [Slot(2, "08:00", "08:45")]),
                ("序号不连续", [Slot(1, "08:00", "08:45"), Slot(3, "09:00", "09:45")]),
                ("时间格式不对（缺冒号）", [Slot(1, "0800", "08:45")]),
                ("时间格式不对（非数字）", [Slot(1, "aa:bb", "08:45")]),
                ("时间格式不对（越界 25:00）", [Slot(1, "08:00", "25:00")]),
                ("开始晚于结束", [Slot(1, "09:00", "08:45")]),
                ("开始等于结束", [Slot(1, "08:00", "08:00")]),
                ("相邻节次重叠", [Slot(1, "08:00", "09:00"), Slot(2, "08:50", "09:40")]),
                ("节次数量为 0", []),
            };

            foreach (var (name, periods) in cases)
            {
                var threw = false;
                try
                {
                    Timetable.ValidatePeriods(periods);
                }
                catch (ArgumentException ex)
                {
                    threw = true;
                    Assert(ex.Message.Length > 0, $"「{name}」的异常必须带说明");
                }
                Assert(threw, $"「{name}」应被校验拦住，实际通过了");
            }

            // 00:00-00:00 是**停用哨兵**，不是非法时间：不参与先后/重叠校验。
            var withDisabled = new List<PeriodSlot>
            {
                Slot(1, "08:00", "08:45"),
                Slot(2, "00:00", "00:00"),          // 停用
                Slot(3, "10:00", "10:45"),
            };
            Timetable.ValidatePeriods(withDisabled);   // 不应抛

            // 停用的节次即使「时间乱写」也不该校验失败——它整节不排课。
            Assert(Timetable.IsPeriodDisabled(Slot(2, "00:00", "00:00")), "00:00-00:00 应判为停用");
            Assert(!Timetable.IsPeriodDisabled(Slot(1, "08:00", "08:45")), "正常时间不应判为停用");
            Assert(Timetable.ResolvePeriodTime(withDisabled, 2) is null, "停用节次取不到时间");
            Assert(Timetable.ResolvePeriodTime(withDisabled, 1) is ("08:00", "08:45"), "启用节次应取到时间");

            // 相邻但不重叠（前一节结束 == 后一节开始）是合法的
            Timetable.ValidatePeriods([Slot(1, "08:00", "08:45"), Slot(2, "08:45", "09:30")]);

            // 超过节次上限要被拦住
            var tooMany = Enumerable.Range(1, Timetable.MaxPeriod + 1)
                .Select(i => Slot(i, "08:00", "08:45")).ToList();
            var capped = false;
            try { Timetable.ValidatePeriods(tooMany); }
            catch (ArgumentException) { capped = true; }
            Assert(capped, $"超过 {Timetable.MaxPeriod} 节应被拦住");

            // 规范化：非法表整体回退默认（读取路径的兜底，与保存路径的「报错」分工不同）
            Assert(Timetable.NormalizePeriods([Slot(1, "09:00", "08:00")]).Count == Timetable.DefaultPeriods.Count,
                "非法节次表应整体回退默认");
            Assert(Timetable.NormalizePeriods(withDisabled).Count == 3, "合法表（含停用节次）应原样保留");
        });

        Check("课表：时间表编辑器（增删改与保存拦截）", () =>
        {
            var editor = new PeriodsEditorViewModel();
            var saved = new List<PeriodSlot>();
            editor.SaveRequested = periods =>
            {
                saved = periods.ToList();
                return Task.FromResult("已保存");
            };

            editor.Load(Timetable.CreateDefaultPeriods());
            Assert(editor.Rows.Count == Timetable.DefaultPeriods.Count,
                $"载入后应显示全部节次，实际 {editor.Rows.Count}");
            Assert(editor.Rows[0] is { Period: 1, StartTime: "08:00", EndTime: "08:45" },
                "载入后应保留原时间");
            Assert(!editor.IsDirty, "刚载入时不应是「有改动」");

            // 改一行 → 变 dirty → 保存成功
            editor.Rows[0].StartTime = "07:50";
            Assert(editor.IsDirty, "改过时间后应标记为有改动");
            Assert(editor.SaveAsync().GetAwaiter().GetResult(), "合法时间应保存成功");
            Assert(saved.Count == Timetable.DefaultPeriods.Count && saved[0].StartTime == "07:50",
                "保存应把改动写出去");
            Assert(!editor.IsDirty, "保存成功后应清掉「有改动」");
            Assert(!editor.StatusIsError, "成功提示不应标红");

            // ---- 非法输入必须被拦住，且不写出去 ----
            var beforeCount = saved.Count;
            editor.Rows[0].StartTime = "09:00";   // 8:45 结束 → 开始晚于结束
            editor.Rows[0].EndTime = "08:45";
            editor.Rows[1].StartTime = "09:00";
            editor.Rows[1].EndTime = "09:40";
            editor.Rows[0].StartTime = "10:00";   // 与第 2 节重叠
            Assert(!editor.SaveAsync().GetAwaiter().GetResult(), "重叠的时间表不应保存成功");
            Assert(editor.StatusIsError, "校验失败必须标红");
            Assert(editor.Status.Contains("时间表有误", StringComparison.Ordinal),
                $"提示应说明是时间表有误，实际「{editor.Status}」");
            Assert(saved.Count == beforeCount, "校验失败时不该写出任何数据");

            // ---- 停用 / 启用 ----
            editor.Load(Timetable.CreateDefaultPeriods());
            var target = editor.Rows[4];
            editor.Disable(target);
            Assert(target.IsDisabled && target.HasDisabledHint, "停用后应标记为停用并给出提示");
            Assert(target.StartTime == Timetable.DisabledPeriodTime, "停用应写入 00:00 哨兵");
            Assert(editor.SaveAsync().GetAwaiter().GetResult(), "含停用节次的表应能保存");
            Assert(Timetable.IsPeriodDisabled(saved[4]), "保存结果里第 5 节应是停用状态");

            editor.Enable(target);
            Assert(!target.IsDisabled, "启用后不应再是停用状态");
            Assert(Timetable.IsValidTime(target.StartTime) && Timetable.IsValidTime(target.EndTime),
                "启用后应给出一组合法时间");

            // ---- 增删节次 ----
            editor.Load(Timetable.CreateDefaultPeriods());
            var originalCount = editor.Rows.Count;
            editor.AddPeriod();
            Assert(editor.Rows.Count == originalCount + 1, "新增一节应追加到末尾");
            Assert(editor.Rows[^1].Period == originalCount + 1, "新增节的序号应连续");
            Assert(Timetable.IsValidTime(editor.Rows[^1].StartTime), "新增节应带一组合法默认时间");
            Assert(editor.SaveAsync().GetAwaiter().GetResult(), "新增一节后应能保存");

            editor.RemoveLast();
            Assert(editor.Rows.Count == originalCount, "删除末节应减一");
            Assert(editor.Rows.Select(r => r.Period).SequenceEqual(Enumerable.Range(1, originalCount)),
                "增删之后节次序号必须仍然从 1 连续");

            // 只剩一节时不能再删
            while (editor.Rows.Count > 1) editor.RemoveLast();
            editor.RemoveLast();
            Assert(editor.Rows.Count == 1 && editor.StatusIsError, "至少要保留 1 节，且要给出提示");

            // ---- 恢复默认 ----
            editor.ResetToDefault();
            Assert(editor.Rows.Count == Timetable.DefaultPeriods.Count, "恢复默认应回到内置节次数");
            Assert(editor.SaveAsync().GetAwaiter().GetResult(), "恢复默认后应能保存");
            Assert(saved.Count == Timetable.DefaultPeriods.Count, "恢复默认保存的应是完整默认表");
        });

        Check("课表：上课提醒（该提醒谁 / 不重复 / 不补过时）", () =>
        {
            static ClassSession S(string id, string date, string start, string end, string name = "高等数学",
                                  string? location = "明德楼B107") =>
                new()
                {
                    Id = id, Name = name, Location = location, Date = date,
                    StartTime = start, EndTime = end,
                };

            const string today = "2026-10-12";
            var sessions = new List<ClassSession>
            {
                S("s1", today, "08:00", "08:45"),          // 已开始
                S("s2", today, "10:00", "10:45"),          // 10 分钟后
                S("s3", today, "14:00", "14:45"),          // 还早
                S("s4", "2026-10-13", "10:00", "10:45"),   // 明天
            };
            var now = new DateTime(2026, 10, 12, 9, 50, 0);
            var notified = new HashSet<string>(StringComparer.Ordinal);

            // 提前 15 分钟 → 只有 10:00 那节该提醒（08:00 已开始、14:00 还早、明天不提醒）
            var due = Timetable.DueReminders(sessions, now, 15, notified);
            Assert(due.Count == 1 && due[0].Session.Id == "s2",
                $"应只提醒 10:00 那节，实际 {due.Count} 条");
            Assert(due[0].MinutesUntil == 10, $"距上课应剩 10 分钟，实际 {due[0].MinutesUntil}");

            // 已提醒过就不再提醒（否则每 30 秒轮询一次会刷屏）
            notified.Add("s2");
            Assert(Timetable.DueReminders(sessions, now, 15, notified).Count == 0,
                "已提醒过的课不应重复提醒");

            // 提前量为 0 = 关闭提醒
            Assert(Timetable.DueReminders(sessions, now, 0, new HashSet<string>()).Count == 0,
                "提前量为 0 时不应提醒");
            Assert(Timetable.DueReminders(sessions, now, -5, new HashSet<string>()).Count == 0,
                "负的提前量不应提醒");

            // 边界：正好在提前量的那一分钟 → 提醒；再早一分钟 → 不提醒
            var at5 = new DateTime(2026, 10, 12, 9, 55, 0);
            Assert(Timetable.DueReminders(sessions, at5, 5, new HashSet<string>()).Count == 1,
                "正好进入提前量窗口时应提醒");
            var at4 = new DateTime(2026, 10, 12, 9, 56, 0);
            Assert(Timetable.DueReminders(sessions, at4, 5, new HashSet<string>()).Count == 1,
                "距上课 4 分钟（已进入窗口）应提醒");

            // 正好到点 → 不提醒（已开始就不该补）
            var atStart = new DateTime(2026, 10, 12, 10, 0, 0);
            Assert(Timetable.DueReminders(sessions, atStart, 15, new HashSet<string>()).Count == 0,
                "正好上课时间不应再提醒");

            // 上课之后：这一节的提醒作废；14:00 那节此时仍在窗口外 → 一条都不该有
            var afterStart = new DateTime(2026, 10, 12, 10, 1, 0);
            Assert(Timetable.DueReminders(sessions, afterStart, 15, new HashSet<string>()).Count == 0,
                "上课后该节的提醒应作废，且 14:00 那节此时还早（不应补过时提醒）");

            // 提前量放大到 5 小时 → 下午那节也进窗口，且按时间排序
            var wide = Timetable.DueReminders(sessions, now, 300, new HashSet<string>());
            Assert(wide.Count == 2 && wide[0].Session.Id == "s2" && wide[1].Session.Id == "s3",
                $"提前量放大后应按时间升序返回 2 条，实际 {wide.Count} 条");
            Assert(wide[1].MinutesUntil == 250, $"14:00 距 09:50 应为 250 分钟，实际 {wide[1].MinutesUntil}");

            // 跨天：明天的课不该在今晚被提醒
            var tonight = new DateTime(2026, 10, 12, 23, 55, 0);
            Assert(Timetable.DueReminders(sessions, tonight, 120, new HashSet<string>()).Count == 0,
                "明天的课不应在今晚提醒");

            // 时间非法的课次直接跳过（坏数据不该让提醒循环抛异常）
            var bad = new List<ClassSession> { S("bad", today, "9:00", "10:00") };
            Assert(Timetable.DueReminders(bad, now, 60, new HashSet<string>()).Count == 0,
                "时间格式非法的课次应被跳过");

            // 通知文案
            var n = Timetable.BuildReminderNotification(sessions[1], 10);
            Assert(n.Title == "高等数学", $"通知标题应为课程名，实际「{n.Title}」");
            Assert(n.Body.Contains("10:00-10:45", StringComparison.Ordinal)
                   && n.Body.Contains("明德楼B107", StringComparison.Ordinal),
                $"通知正文应含时间与地点，实际「{n.Body}」");
            Assert(n.Attribution == "10 分钟后上课", $"角标应说明还剩多久，实际「{n.Attribution}」");

            var soon = Timetable.BuildReminderNotification(sessions[1], 1);
            Assert(soon.Attribution == "马上上课", $"剩 1 分钟时应提示「马上上课」，实际「{soon.Attribution}」");

            var noLocation = Timetable.BuildReminderNotification(S("x", today, "10:00", "10:45", location: null), 10);
            Assert(!noLocation.Body.Contains("·", StringComparison.Ordinal),
                $"没有地点时不应留下孤立的分隔符，实际「{noLocation.Body}」");
        });

        Check("课表：上课提醒服务（轮询一次的真实行为）", () =>
        {
            // 2026-2027 学年第 1 学期第 1 周周一 = 2026-09-07
            const string termStart = "2026-09-07";

            // 用**显式**节次表而不是内置默认值：内置值将来可能调整，
            // 而这条用例要断言的是「几点上课 → 什么时候提醒」，不该被默认值绑架。
            var periods = new List<PeriodSlot>
            {
                new() { Period = 1, StartTime = "08:00", EndTime = "08:45" },
                new() { Period = 2, StartTime = "09:00", EndTime = "09:45" },
                new() { Period = 3, StartTime = "10:00", EndTime = "10:45" },
            };

            var file = new ScheduleFile
            {
                Periods = periods,
                Items =
                [
                    new() { Id = "a", Name = "高等数学", Weekday = 1, StartPeriod = 3, EndPeriod = 3,
                            StartWeek = 1, EndWeek = 16, Location = "明德楼B107",
                            StartTime = "10:00", EndTime = "10:45" },
                ],
            };

            var sink = new List<AppNotification>();
            var notifier = new RecordingNotifier(sink);
            var now = new DateTime(2026, 9, 7, 9, 50, 0);   // 周一 09:50，距 10:00 还有 10 分钟

            var service = new ClassReminderService(
                () => file, () => termStart, () => 15, notifier, log: null, now: () => now);

            // 先确认上游：expand 得先真的给出「今天这一节」，否则提醒逻辑再对也白搭。
            var todaySessions = Timetable.Expand(
                file.Items, termStart, periods, fromDate: "2026-09-07", days: 1);
            Assert(todaySessions.Count == 1,
                $"2026-09-07（第 1 周周一）应展开出 1 节课，实际 {todaySessions.Count}");
            Assert(todaySessions[0].StartTime == "10:00",
                $"该节开始时间应取节次表（第 3 节 = 10:00），实际 {todaySessions[0].StartTime}");

            Assert(service.CheckOnce() == 1, "距上课 10 分钟、提前量 15 分钟 → 应发 1 条提醒");
            Assert(sink.Count == 1 && sink[0].Title == "高等数学",
                $"通知内容应为该课程，实际 {sink.Count} 条");

            // 再轮询一次：同一节课不能重复提醒
            Assert(service.CheckOnce() == 0, "同一次课不应重复提醒");

            // 关掉提醒（提前量 0）→ 什么都不发
            var off = new ClassReminderService(
                () => file, () => termStart, () => 0, notifier, log: null, now: () => now);
            Assert(off.CheckOnce() == 0, "提前量为 0 时应完全不提醒");

            // 没有学期基准 → 算不出今天几点上课，安静地跳过而不是抛异常
            var noAnchor = new ClassReminderService(
                () => file, () => null, () => 15, notifier, log: null, now: () => now);
            Assert(noAnchor.CheckOnce() == 0, "没有学期基准时应安静跳过");

            // 没有课表 → 同样安静跳过
            var empty = new ClassReminderService(
                () => new ScheduleFile { Periods = periods },
                () => termStart, () => 15, notifier, log: null, now: () => now);
            Assert(empty.CheckOnce() == 0, "没有课表时应安静跳过");

            // 通知平台不可用：不该抛异常，且不应反复重试同一节课
            var failing = new ClassReminderService(
                () => file, () => termStart, () => 15, new FailingNotifier(), log: null, now: () => now);
            Assert(failing.CheckOnce() == 0, "通知失败时应返回 0 且不抛异常");
            Assert(failing.CheckOnce() == 0, "通知失败后不应反复重试同一节课");

            // 重置后可以重新提醒（换学期/重新导入课表的语义）
            service.ResetNotified();
            Assert(service.CheckOnce() == 1, "重置「已提醒」后应能再次提醒");
        });

        Check("上课提醒：前提提示（有课表就不能说「还没有课表」）", () =>
        {
            // 回归：启动时课表是异步读盘的，而提示原先在启动读盘那条路径上**忘了刷新**，
            // 于是「程序里明明有课表，设置里却说还没有课表」。
            var previousSettings = AppSettings.Current.ClassReminderMinutes;
            var previousTermStart = AppSettings.Current.TermStartDate;
            var previousNotifier = MainViewModel.Notifier;
            try
            {
                AppSettings.Current.ClassReminderMinutes = 15;
                // 必须显式清掉学期基准：AppSettings.Current 读的是本机真实的 settings.json，
                // 那上面通常已经填了学期第一周，不清就会让下面的断言全部失真。
                AppSettings.Current.TermStartDate = null;
                MainViewModel.Notifier = new RecordingNotifier([]);

                var vm = new MainViewModel();
                var withItems = new ScheduleFile
                {
                    Periods = Timetable.CreateDefaultPeriods(),
                    Items =
                    [
                        new() { Id = "a", Name = "高等数学", Weekday = 1, StartPeriod = 1, EndPeriod = 2,
                                StartWeek = 1, EndWeek = 16, StartTime = "08:00", EndTime = "09:40" },
                    ],
                };

                // 关键：写入课表时**必须主动通知**「上课提醒前提」重算。
                // 真实路径上，界面绑定提示的时机在异步读盘之前，拿到的是一份「还没有课表」；
                // 之后读了盘却不通知，那条提示就会一直挂在那里——
                // 这正是「程序里明明有课表，设置里却说还没有课表」的成因。
                var notifications = new List<string>();
                vm.PropertyChanged += (_, e) => notifications.Add(e.PropertyName ?? string.Empty);

                vm.LoadScheduleForCheck(withItems);
                Assert(notifications.Contains(nameof(MainViewModel.ClassReminderHint))
                       || notifications.Contains(nameof(MainViewModel.HasClassReminderHint)),
                    "写入课表后必须通知「上课提醒前提」重算，"
                    + "否则设置页会一直显示读盘前那份「还没有课表」");

                Assert(vm.HasClassReminderHint,
                    "没设学期基准时应当提示原因（但原因不能是「没有课表」）");
                Assert(vm.ClassReminderHint is not null
                       && !vm.ClassReminderHint.Contains("没有课表", StringComparison.Ordinal),
                    $"已经有课表了，就不该说「还没有课表」，实际「{vm.ClassReminderHint}」");
                Assert(vm.ClassReminderHint!.Contains("学期第一周", StringComparison.Ordinal),
                    $"此时真正缺的是学期基准，实际「{vm.ClassReminderHint}」");

                // 填上学期基准 → 前提齐了，不该再有提示
                AppSettings.Current.TermStartDate = "2026-09-07";
                vm.LoadScheduleForCheck(withItems);
                Assert(!vm.HasClassReminderHint,
                    $"学期基准与课表都齐了就不该再提示，实际「{vm.ClassReminderHint}」");

                // 换成空课表 → 这次才轮到「还没有课表」
                vm.LoadScheduleForCheck(new ScheduleFile { Periods = Timetable.CreateDefaultPeriods() });
                Assert(vm.ClassReminderHint is not null
                       && vm.ClassReminderHint.Contains("没有课表", StringComparison.Ordinal),
                    $"空课表时才该提示「还没有课表」，实际「{vm.ClassReminderHint}」");

                // 关掉提醒（0 分钟）→ 不提示任何东西，别拿无关的告警烦用户
                AppSettings.Current.ClassReminderMinutes = 0;
                vm.LoadScheduleForCheck(new ScheduleFile { Periods = Timetable.CreateDefaultPeriods() });
                Assert(!vm.HasClassReminderHint, "提前量为 0 时不该有任何提示");
            }
            finally
            {
                AppSettings.Current.ClassReminderMinutes = previousSettings;
                AppSettings.Current.TermStartDate = previousTermStart;
                MainViewModel.Notifier = previousNotifier;
            }
        });

        Check("更新检查：超时与取消必须分开报（不要把 .NET 英文原文甩给用户）", () =>
        {
            // 回归：用户截图里看到的是
            // 「检查更新失败：The request was canceled due to the configured
            //   HttpClient.Timeout of 15 seconds elapsing.」
            // 成因是 TaskCanceledException 派生自 OperationCanceledException，
            // 而旧代码用 catch (OperationCanceledException) { throw; } 想让「用户取消」透传，
            // 结果把「超时」也一起抛到了界面上。
            Assert(typeof(OperationCanceledException).IsAssignableFrom(typeof(TaskCanceledException)),
                "TaskCanceledException 应派生自 OperationCanceledException"
                + "（这正是「只按异常类型分不出超时与取消」的根源，别用类型判断）");

            // ① 超时必须翻成人话，且不留 .NET 原文
            var timeout = UpdateService.DescribeFailure(new TaskCanceledException(
                "The request was canceled due to the configured HttpClient.Timeout of 15 seconds elapsing."));
            Assert(!timeout.Contains("HttpClient", StringComparison.Ordinal)
                   && !timeout.Contains("canceled", StringComparison.OrdinalIgnoreCase),
                $"不该把 HttpClient 原文给用户看，实际「{timeout}」");
            Assert(timeout.Contains("超时", StringComparison.Ordinal), $"超时要明说，实际「{timeout}」");

            // ② 我们自己显式抛的超时带秒数，要原样保留（用户才知道是「慢」还是「不通」）
            var explicitTimeout = UpdateService.DescribeFailure(
                new TimeoutException("请求超时（30 秒内没有响应）"));
            Assert(explicitTimeout.Contains("30 秒", StringComparison.Ordinal),
                $"显式超应保留秒数，实际「{explicitTimeout}」");

            // ③ 真取消仍然说「已取消」，④ 网络错误要能区分
            Assert(UpdateService.DescribeFailure(new OperationCanceledException()) == "已取消",
                "用户主动取消应报「已取消」");
            Assert(UpdateService.DescribeFailure(new HttpRequestException("Connection refused"))
                       .Contains("网络不可达", StringComparison.Ordinal),
                "网络错误应报「网络不可达」");
        });

        Check("全部已读：远程失败不该拖垮本地已读", () =>
        {
            // 公告的已读判据是「本地 ReadAnnouncements ∪ 学习日志 hasRead」（见 SyncService），
            // 与通知中心的 user_read_all 接口无关；后者只是顺手替用户清雨课堂的通知中心。
            // 旧代码「远程失败即整体失败」，于是用户点了没反应、只看到一句无从下手的报错。
            var vm = new MainViewModel();
            var r = vm.MarkAllReadAsync().GetAwaiter().GetResult();
            Assert(!r.LocalApplied, "未登录时本地已读无法落地");
            Assert(r.RemoteFailureReason == "未登录", $"未登录必须给出确切原因，实际「{r.RemoteFailureReason}」");
            Assert(!r.RemoteSynced && !r.AuthExpired, "未登录既没同步成功，也不是「登录态失效」");
        });

        Check("课表：合并导入（增/改/跳过，不删既有）", () =>
        {
            var existing = new List<ScheduleItem>
            {
                new()
                {
                    Id = "keep", Name = "手动补录课", Weekday = 2, StartPeriod = 7, EndPeriod = 8,
                    StartWeek = 1, EndWeek = 16, StartTime = "16:00", EndTime = "17:40", Term = 1,
                },
            };
            List<ScheduleItem> Incoming() =>
            [
                new()
                {
                    Name = "高等数学", Teacher = "张三", Location = "101", Weekday = 1,
                    StartPeriod = 1, EndPeriod = 2, StartWeek = 1, EndWeek = 16,
                    StartTime = "08:00", EndTime = "09:40",
                },
            ];

            // 首次导入：新增 1
            var (first, r1) = ScheduleMerge.Merge(existing, Incoming(), 1);
            Assert(r1 is { Added: 1, Updated: 0, Skipped: 0 }, $"首次导入统计不符：{r1}");
            Assert(first.Count == 2, "既有条目必须保留（合并导入不删除）");
            Assert(first.Any(i => i.Id == "keep" && i.Name == "手动补录课"), "手动条目不应被删");

            // 再导一次同样内容：跳过（幂等），不产生重复
            var (second, r2) = ScheduleMerge.Merge(first, Incoming(), 1);
            Assert(r2 is { Added: 0, Updated: 0, Skipped: 1 }, $"重复导入应全部跳过：{r2}");
            Assert(second.Count == 2, "幂等导入不应产生重复条目");

            // 内容变化（换教师）：更新，且沿用原 Id
            var changed = Incoming();
            changed[0].Teacher = "李四";
            var (third, r3) = ScheduleMerge.Merge(second, changed, 1);
            Assert(r3 is { Added: 0, Updated: 1, Skipped: 0 }, $"内容变化应更新：{r3}");
            var mathId = second.First(i => i.Name == "高等数学").Id;
            Assert(third.First(i => i.Name == "高等数学").Teacher == "李四", "更新后的教师不符");
            Assert(third.First(i => i.Name == "高等数学").Id == mathId, "更新应沿用原 Id");
            Assert(third.Count == 2, "更新不应改变条目总数");

            // 匹配键含周次：同一门课不同周次段是两条独立记录
            var twoSegments = new List<ScheduleItem>
            {
                new() { Name = "物理", Weekday = 3, StartPeriod = 3, EndPeriod = 4, StartWeek = 1, EndWeek = 8, StartTime = "10:00", EndTime = "11:40" },
                new() { Name = "物理", Weekday = 3, StartPeriod = 3, EndPeriod = 4, StartWeek = 10, EndWeek = 16, StartTime = "10:00", EndTime = "11:40" },
            };
            var (_, r4) = ScheduleMerge.Merge([], twoSegments, 1);
            Assert(r4.Added == 2, $"不连续周次的两段应各算一条：{r4}");

            // 整批校验：一条非法则一条都不写（不留「导入了一半」的课表）
            var withBad = new List<ScheduleItem>
            {
                new() { Name = "合法课", Weekday = 1, StartPeriod = 1, EndPeriod = 2, StartWeek = 1, EndWeek = 4, StartTime = "08:00", EndTime = "09:40" },
                new() { Name = "非法课", Weekday = 9, StartPeriod = 1, EndPeriod = 2, StartWeek = 1, EndWeek = 4, StartTime = "08:00", EndTime = "09:40" },
            };
            var threw = false;
            try { ScheduleMerge.Merge([], withBad, 1); }
            catch (ArgumentException) { threw = true; }
            Assert(threw, "含非法条目时应抛异常而不是部分写入");
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

    /// <summary>是否形如 <c>#RRGGBB</c>（课程配色会被视图直接当画刷用，格式必须合法）。</summary>
    private static bool IsHexColor(string? value) =>
        value is { Length: 7 } && value[0] == '#'
        && value.Skip(1).All(Uri.IsHexDigit);

    /// <summary>
    /// 两个时刻是否足够接近。JSON 往返会把时间截到微秒，测试自身也有耗时，
    /// 因此不能直接比相等；这里给 5 秒余量——远小于任何真实的时间计算错误。
    /// </summary>
    private static bool CloseEnough(DateTimeOffset? a, DateTimeOffset? b, double seconds = 5)
        => a is { } x && b is { } y && (x - y).Duration() < TimeSpan.FromSeconds(seconds);

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

    /// <summary>永远发送失败的通知器：验证「通知平台不可用时提醒循环不炸、也不反复重试」。</summary>
    private sealed class FailingNotifier : INotifier
    {
        public bool IsAvailable => false;

        public string? UnavailableReason => "（自检）通知平台不可用";

        public string? Show(AppNotification notification) => UnavailableReason;
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

    // ---- issue#1 回归用的假 API ----

    private static YktCourse Course(long classroomId, string name) => new()
    {
        ClassroomId = classroomId,
        Name = name,
        Term = 202601,
    };

    /// <summary>type=19（章节作业）的日志条；leaf_id / score_d 走真实 DTO 反序列化。</summary>
    private static YktActivity HwActivity(long id, long leafId, string title)
        => JsonSerializer.Deserialize(
            """{"id":$ID$,"type":19,"title":"$TITLE$","create_time":"1758000000000","content":{"leaf_id":$LEAF$,"score_d":1790000000000} }"""
                .Replace("$ID$", id.ToString(CultureInfo.InvariantCulture))
                .Replace("$TITLE$", title)
                .Replace("$LEAF$", leafId.ToString(CultureInfo.InvariantCulture)),
            AppJsonContext.Default.YktActivity)!;

    private static TodoCacheFile CacheOf(SyncResult r) => new()
    {
        SyncedAt = r.SyncedAt,
        Homework = r.HomeworkForCache.ToList(),
        Announcements = r.Announcements.ToList(),
        CourseSignatures = new Dictionary<long, string>(r.CourseSignatures),
        CourseFullSyncedAt = new Dictionary<long, DateTimeOffset>(r.CourseFullSyncedAt),
    };

    /// <summary>离线的 IYktApi：固定课程表 + 固定学习日志，进度/章节/详情为空。</summary>
    private sealed class FakeYktApi(
        IReadOnlyList<YktCourse> courses,
        Dictionary<long, List<YktActivity>> logs) : IYktApi
    {
        public YktSession Session { get; } = new()
        {
            SessionId = "fake",
            UniversityId = 1,
            Term = 202601,
        };

        public Task<IReadOnlyList<YktCourse>> GetCoursesAsync(CancellationToken ct = default)
            => Task.FromResult(courses);

        /// <summary>归档班级，默认没有；需要覆盖归档合并路径的用例可以传入。</summary>
        public IReadOnlyList<YktCourse> Archived { get; init; } = [];

        public Task<IReadOnlyList<YktCourse>> GetArchivedCoursesAsync(CancellationToken ct = default)
            => Task.FromResult(Archived);

        public Task<IReadOnlyList<YktActivity>> GetLearnLogsAsync(long classroomId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<YktActivity>>(logs.GetValueOrDefault(classroomId, []));

        public Task<IReadOnlyDictionary<string, YktLeafProgress>> GetProgressAsync(long classroomId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyDictionary<string, YktLeafProgress>>(new Dictionary<string, YktLeafProgress>());

        public Task<YktLeafDetail?> GetLeafDetailAsync(long classroomId, long leafId, CancellationToken ct = default)
            => Task.FromResult<YktLeafDetail?>(null);

        public Task<IReadOnlyList<YktChapter>> GetChaptersAsync(long classroomId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<YktChapter>>([]);

        public Task<int> GetUnreadCountAsync(CancellationToken ct = default) => Task.FromResult(0);

        public Task<YktUserProfile?> GetUserProfileAsync(CancellationToken ct = default)
            => Task.FromResult<YktUserProfile?>(null);

        public Task<bool> MarkAllReadAsync(CancellationToken ct = default) => Task.FromResult(true);

        public void Dispose() { }
    }
}
