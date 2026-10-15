using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Linq;
using System.Threading;
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
