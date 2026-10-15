using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using HomeworkReminder.Services;

namespace HomeworkReminder.Desktop;

/// <summary>
/// 学习通任务点在线自检（<c>--cxcheck</c>）。
/// <para>
/// 用本机已保存的学习通会话真实跑一遍「课程列表 → 逐课任务点」，
/// 打印每门课的未完成任务点数与明细。
/// 存在的理由：任务点的解析口径（只算叶子、防父层汇总重复计数）只有对着
/// **真实页面**才能确认；离线用例用的是手写样本，验不出选择器是否还对得上线上结构。
/// </para>
/// <para>
/// 只读：不修改任何远端状态。会话来自 DPAPI 加密的 chaoxing-session.json。
/// </para>
/// </summary>
internal static class ChaoxingCheck
{
    public static int Run()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        try
        {
            var store = new ChaoxingSessionStore();
            var session = store.LoadAsync().GetAwaiter().GetResult();
            if (session is not { HasValue: true })
            {
                Console.WriteLine($"会话文件无有效登录态：{store.Location}");
                Console.WriteLine("请先在应用里登录学习通，再运行本自检。");
                return 3;
            }

            Console.WriteLine($"会话：{store.Location}（{session.CookieHeader!.Length} 字符）");
            Console.WriteLine("开始同步课程任务点…");

            var progress = new Progress<string>(s => Console.WriteLine("  " + s));
            var (courses, warnings) = ChaoxingSyncService
                .SyncCoursesAsync(session.CookieHeader!, progress)
                .GetAwaiter().GetResult();

            Console.WriteLine();
            Console.WriteLine("=== 课程未完成任务点 ===");
            var withPending = 0;
            var total = 0;
            foreach (var c in courses.OrderByDescending(c => c.PendingTaskPoints))
            {
                total += c.PendingTaskPoints;
                if (c.PendingTaskPoints > 0) withPending++;
                var retire = c.IsRetired ? " [已结课]" : string.Empty;
                Console.WriteLine($"  {c.PendingTaskPoints,4}  {c.CourseName}（{c.ClazzName}）{retire}");
                foreach (var l in c.Leaves)
                    Console.WriteLine($"          {l.ChapterNumber,8} {l.Name}  +{l.PendingCount}");
            }

            Console.WriteLine();
            Console.WriteLine($"  课程数 {courses.Count}；有未完成任务点的 {withPending} 门；任务点合计 {total}");
            if (warnings.Count > 0)
            {
                Console.WriteLine("  警告：");
                foreach (var w in warnings) Console.WriteLine("    " + w);
            }

            // 失败条件：一门课都没读到（说明课程列表或解析坏了）。
            if (courses.Count == 0)
            {
                Console.WriteLine("结论：没有读到任何课程 ✗");
                return 4;
            }

            Console.WriteLine("结论：课程与任务点读取正常 ✓");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"自检异常：{ex.GetType().Name}: {ex.Message}");
            return 5;
        }
    }
}
